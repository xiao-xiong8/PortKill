using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Processes;

/// <summary>应用级共享名称缓存。身份采集同步运行于扫描线程，文件读取由两个有界后台消费者执行。</summary>
public sealed class WindowsProcessMetadataProvider : IProcessMetadataProvider
{
    private readonly object _sync = new();
    private readonly IProcessMetadataReader _reader;
    private readonly TimeProvider _time;
    private readonly Dictionary<ProcessIdentity, Entry> _entries = new();
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<FileEntry> _queue = Channel.CreateBounded<FileEntry>(2048);
    private readonly CancellationTokenSource _stop = new();
    private ImmutableDictionary<ProcessIdentity, ProcessMetadata> _snapshot = ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty;
    private int _started, _notifying;
    private bool _disposed;
    internal long FileReads;
    public WindowsProcessMetadataProvider() : this(new WindowsProcessMetadataReader(), TimeProvider.System) { }
    internal WindowsProcessMetadataProvider(IProcessMetadataReader reader, TimeProvider? time = null)
    { _reader = reader; _time = time ?? TimeProvider.System; }
    public event Action? Changed;
    public IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> Snapshot => Volatile.Read(ref _snapshot);
    public IProcessMetadataSession CreateSession()
    {
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); return new Session(this); }
    }

    private sealed class Entry(FileEntry file)
    { public readonly FileEntry File = file; public int References; }
    private sealed class FileEntry(string path)
    {
        public readonly string Path = path;
        public readonly HashSet<ProcessIdentity> Identities = new();
        public ProcessMetadata Metadata = ProcessMetadata.Pending;
        public ExecutableStamp? Stamp;
        public DateTimeOffset NextCheck, LastUse;
        public bool Queued, Needed;
    }

    private sealed class Session(WindowsProcessMetadataProvider owner) : IProcessMetadataSession
    {
        internal HashSet<ProcessIdentity> Identities = new();
        private Dictionary<int, ProcessObservation> _previous = new();
        private readonly Dictionary<ProcessReference, DateTimeOffset> _failures = new();
        private volatile bool _disposed;
        public IReadOnlyDictionary<int, ProcessObservation> Observe(IReadOnlyList<ProcessReference> processes)
        {
            if (_disposed) return ImmutableDictionary<int, ProcessObservation>.Empty;
            var observations = new Dictionary<int, ProcessObservation>(processes.Count);
            var next = new HashSet<ProcessIdentity>();
            var now = owner._time.GetUtcNow();
            foreach (var process in processes)
            {
                if (process.Pid <= 4 || observations.ContainsKey(process.Pid)) continue;
                if (_failures.TryGetValue(process, out var retry) && retry > now) continue;
                _previous.TryGetValue(process.Pid, out var previous);
                ProcessObservation? observation;
                try { observation = owner._reader.Observe(process, previous); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { observation = null; }
                if (observation is not { Identity.IsVerified: true, ProcessPath: not null })
                { _failures[process] = now.AddSeconds(5); continue; }
                observations[process.Pid] = observation;
                // UNC 路径不进入可能阻塞本地名称补全的文件读取队列。
                if (!observation.ProcessPath.StartsWith(@"\\", StringComparison.Ordinal)) next.Add(observation.Identity);
            }
            var active = processes.ToHashSet();
            foreach (var key in _failures.Keys.Where(x => !active.Contains(x)).ToArray()) _failures.Remove(key);
            lock (owner._sync)
            {
                if (_disposed || owner._disposed) return ImmutableDictionary<int, ProcessObservation>.Empty;
                foreach (var id in Identities.Except(next)) owner.Release(id);
                foreach (var observation in observations.Values)
                {
                    if (!next.Contains(observation.Identity)) continue;
                    var path = observation.ProcessPath!;
                    if (!owner._entries.TryGetValue(observation.Identity, out var entry))
                    {
                        if (!owner._files.TryGetValue(path, out var file))
                            owner._files[path] = file = new FileEntry(path);
                        owner._entries[observation.Identity] = entry = new Entry(file);
                        file.Identities.Add(observation.Identity);
                        owner._snapshot = owner._snapshot.SetItem(observation.Identity, file.Metadata);
                        file.Needed = true; // 新实例也重新检查文件版本，但相同 stamp 不重复解析。
                    }
                    if (!Identities.Contains(observation.Identity)) entry.References++;
                    entry.File.LastUse = now;
                    if (entry.File.NextCheck <= now) entry.File.Needed = true;
                }
                Identities = next;
                _previous = observations;
                owner.FillQueue();
                owner.TrimColdFiles();
            }
            return observations;
        }
        public void Dispose()
        {
            lock (owner._sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var id in Identities) owner.Release(id);
                // Observe 的原生查询可能尚在退出；不并发清空它私有的观测/失败字典。
                Identities.Clear();
                owner.TrimColdFiles();
            }
        }
    }

    private void Release(ProcessIdentity id)
    {
        if (!_entries.TryGetValue(id, out var entry) || --entry.References > 0) return;
        entry.File.Identities.Remove(id);
        _entries.Remove(id);
        _snapshot = _snapshot.Remove(id);
    }
    private void FillQueue()
    {
        if (_disposed) return;
        foreach (var file in _files.Values)
        {
            if (!file.Needed || file.Queued || file.Identities.Count == 0) continue;
            if (!_queue.Writer.TryWrite(file)) break; // 需求仍留在 FileEntry，队列腾空后补调度。
            file.Queued = true; file.Needed = false;
        }
        if (Interlocked.Exchange(ref _started, 1) == 0)
            for (int i = 0; i < 2; i++) _ = Task.Run(ReadFilesAsync);
    }
    private async Task ReadFilesAsync()
    {
        try
        {
            await foreach (var file in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    if (file.Identities.Count == 0) { file.Queued = false; FillQueue(); continue; }
                }
                ProcessMetadata metadata;
                ExecutableStamp? stamp = null;
                bool failed = false;
                try
                {
                    stamp = _reader.GetStamp(file.Path);
                    if (file.Stamp == stamp && file.Metadata.State == ProcessMetadataState.Available) metadata = file.Metadata;
                    else { Interlocked.Increment(ref FileReads); metadata = _reader.Read(file.Path); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
                { metadata = ProcessMetadata.Unavailable; failed = true; }
                bool changed = false;
                lock (_sync)
                {
                    if (_disposed) return;
                    file.Stamp = stamp; file.Metadata = metadata; file.Queued = false;
                    file.NextCheck = _time.GetUtcNow().AddSeconds(failed ? 5 : metadata.State == ProcessMetadataState.Unavailable ? 60 : 30);
                    foreach (var id in file.Identities)
                    {
                        if (!_snapshot.TryGetValue(id, out var old) || old != metadata)
                        { _snapshot = _snapshot.SetItem(id, metadata); changed = true; }
                    }
                    FillQueue(); TrimColdFiles();
                }
                if (changed) ScheduleNotification();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private void ScheduleNotification()
    {
        if (Interlocked.Exchange(ref _notifying, 1) != 0) return;
        _ = NotifyAsync();
    }
    private async Task NotifyAsync()
    {
        try
        {
            await Task.Delay(24, _stop.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _notifying, 0);
            if (!_stop.IsCancellationRequested) Changed?.Invoke();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private void TrimColdFiles()
    {
        if (_files.Count <= 4096) return;
        foreach (var file in _files.Values.Where(x => x.Identities.Count == 0 && !x.Queued)
                     .OrderBy(x => x.LastUse).Take(_files.Count - 4096).ToArray()) _files.Remove(file.Path);
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel(); _queue.Writer.TryComplete();
            _entries.Clear(); _files.Clear(); _snapshot = ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty;
            Changed = null;
        }
    }
}
