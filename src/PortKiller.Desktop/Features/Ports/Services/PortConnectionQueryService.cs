using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Features.Ports.Services;

internal sealed record PortQuery(IReadOnlyList<PortConnection> Items,
    IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> Metadata,
    string Search, string Category, string State, int SortColumn, bool Ascending,
    bool RespectInteractionHold = false, bool RefreshInspector = false);
internal sealed record PortQueryResult(long Version, PortQuery Query, List<PortConnection> Items);

/// <summary>单一后台消费者、只保留最新请求；同一字段逻辑同时用于隔离回归。</summary>
internal sealed class PortConnectionQueryService : IDisposable
{
    private readonly Channel<(long Version, PortQuery Query)> _requests = Channel.CreateBounded<(long, PortQuery)>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _running;
    private readonly object _sync = new();
    private long _version;
    private bool _started, _disposed;
    public long Version => Interlocked.Read(ref _version);
    public event Action<PortQueryResult>? Completed;

    public void Enqueue(PortQuery query)
    {
        lock (_sync)
        {
            if (_disposed) return;
            long version = Interlocked.Increment(ref _version);
            _running?.Cancel();
            _requests.Writer.TryWrite((version, query));
            if (!_started) { _started = true; _ = Task.Run(RunAsync); }
        }
    }
    public void Invalidate()
    {
        lock (_sync) { Interlocked.Increment(ref _version); _running?.Cancel(); }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lock (_sync)
                {
                    if (_disposed) return;
                    if (request.Version != Version) continue;
                    _running = cancellation;
                }
                try
                {
                    var rows = Evaluate(request.Query, cancellation.Token);
                    if (request.Version == Version && !cancellation.IsCancellationRequested)
                        Completed?.Invoke(new(request.Version, request.Query, rows));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally { lock (_sync) if (ReferenceEquals(_running, cancellation)) _running = null; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal static List<PortConnection> Evaluate(PortQuery request, CancellationToken token = default)
    {
        string keyword = request.Search.Trim();
        var tokens = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int min = 0, max = 0;
        bool range = keyword.Contains('-') && TryRange(keyword, out min, out max);
        var result = new List<PortConnection>();
        foreach (var item in request.Items)
        {
            token.ThrowIfCancellationRequested();
            if (request.Category != "全部分类" && item.ServiceCategory != request.Category) continue;
            if (request.State switch { "LISTENING" => !item.IsListening, "ESTABLISHED" => !item.IsEstablished,
                    "TIME_WAIT" => !item.IsTimeWait, _ => false }) continue;
            if (range)
            { if (item.LocalPort < min || item.LocalPort > max) continue; }
            else
            {
                var metadata = GetMetadata(item, request.Metadata);
                bool matches = true;
                foreach (var word in tokens)
                {
                    bool number = int.TryParse(word.AsSpan(word.StartsWith(':') ? 1 : 0), out int numeric);
                    bool named = Contains(item.ProcessName, word) || Contains(metadata?.ApplicationName, word)
                        || Contains(metadata?.FileDescription, word) || Contains(metadata?.ProductName, word);
                    if (number ? !named && item.LocalPort != numeric && item.RemotePort != numeric && item.Pid != numeric
                        : !named && !Contains(item.ServiceCategory, word) && !Contains(item.State, word)
                            && !Contains(item.LocalAddress, word) && !Contains(item.Protocol, word))
                    { matches = false; break; }
                }
                if (!matches) continue;
            }
            result.Add(item);
        }
        var compare = StringComparer.CurrentCulture;
        int direction = request.Ascending ? 1 : -1;
        result.Sort((a, b) =>
        {
            int value;
            if (request.SortColumn == 7)
            {
                string an = GetMetadata(a, request.Metadata)?.ApplicationName ?? "";
                string bn = GetMetadata(b, request.Metadata)?.ApplicationName ?? "";
                // 缺失名称在升降序下均位于末尾。
                value = string.IsNullOrEmpty(an).CompareTo(string.IsNullOrEmpty(bn));
                if (value != 0) return value;
                value = direction * compare.Compare(an, bn);
            }
            else value = direction * (request.SortColumn switch
            {
                1 => compare.Compare(a.State, b.State), 2 => compare.Compare(a.ServiceCategory, b.ServiceCategory),
                3 => compare.Compare(a.ProcessName, b.ProcessName), 4 => a.Pid.CompareTo(b.Pid),
                5 => compare.Compare(a.LocalEndpoint, b.LocalEndpoint), _ => a.LocalPort.CompareTo(b.LocalPort)
            });
            if (value != 0) return value;
            if (request.SortColumn == 7 && (value = compare.Compare(a.ProcessName, b.ProcessName)) != 0) return value;
            if ((value = a.Pid.CompareTo(b.Pid)) != 0) return value;
            if ((value = a.LocalPort.CompareTo(b.LocalPort)) != 0) return value;
            if ((value = a.RemotePort.CompareTo(b.RemotePort)) != 0) return value;
            // List.Sort 不稳定：完整身份兜底，避免无关连接增删或采集顺序变化让原有行跳动。
            if ((value = StringComparer.Ordinal.Compare(a.Protocol, b.Protocol)) != 0) return value;
            if ((value = StringComparer.Ordinal.Compare(a.IpVersion, b.IpVersion)) != 0) return value;
            if ((value = StringComparer.Ordinal.Compare(a.LocalAddress, b.LocalAddress)) != 0) return value;
            if ((value = StringComparer.Ordinal.Compare(a.RemoteAddress, b.RemoteAddress)) != 0) return value;
            if ((value = StringComparer.Ordinal.Compare(a.ProcessName, b.ProcessName)) != 0) return value;
            return a.ProcessCreationTime.CompareTo(b.ProcessCreationTime);
        });
        token.ThrowIfCancellationRequested();
        return result;
    }
    internal static ProcessMetadata? GetMetadata(PortConnection item, IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> metadata)
        => !item.IsTimeWait && item.ProcessIdentity.IsVerified && metadata.TryGetValue(item.ProcessIdentity, out var value) ? value : null;
    private static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
    private static bool TryRange(string text, out int min, out int max)
    {
        min = max = 0;
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out min) || !int.TryParse(parts[1], out max)) return false;
        if (min > max) (min, max) = (max, min);
        return true;
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; Invalidate(); _stop.Cancel(); _requests.Writer.TryComplete(); Completed = null;
        }
    }
}
