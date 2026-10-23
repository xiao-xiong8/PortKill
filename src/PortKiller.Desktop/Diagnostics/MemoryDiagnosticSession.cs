using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace PortKiller.Services;

/// <summary>仅显式启用的临时采样：后台写 CSV，不强制 GC，不持有历史行/窗口集合。</summary>
internal sealed class MemoryDiagnosticSession
{
    internal const string Argument = "--memory-log";
    internal static bool IsRequested => Environment.GetCommandLineArgs().Contains(Argument, StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private int _manualStop;
    internal string LogPath { get; }
    internal Task Completion { get; }

    private MemoryDiagnosticSession(Func<MemoryDiagnosticState> capture, string path, TimeSpan duration, TimeSpan interval)
    {
        LogPath = path;
        _stop.CancelAfter(duration);
        Completion = Task.Run(() => RunAsync(capture, duration, interval));
    }

    internal static MemoryDiagnosticSession Start(Func<MemoryDiagnosticState> capture, string? path = null,
        TimeSpan? duration = null, TimeSpan? interval = null)
    {
        var sampleDuration = duration ?? TimeSpan.FromHours(2);
        var sampleInterval = interval ?? TimeSpan.FromSeconds(5);
        if (sampleDuration <= TimeSpan.Zero || sampleDuration > TimeSpan.FromHours(2) || sampleInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortKiller", "Diagnostics", $"memory-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.csv");
        return new(capture, Path.GetFullPath(path), sampleDuration, sampleInterval);
    }

    internal void Stop()
    {
        Interlocked.Exchange(ref _manualStop, 1);
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(Func<MemoryDiagnosticState> capture, TimeSpan duration, TimeSpan interval)
    {
        string reason = "duration-limit";
        int samples = 0;
        bool ownsLog = false;
        var clock = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var writer = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };
            ownsLog = true;
            await File.WriteAllTextAsync(LogPath + ".metadata.txt",
                $"Started={DateTimeOffset.Now:o}\nPid={Environment.ProcessId}\nExecutable={Environment.ProcessPath}\n"
                + $"ApplicationDirectory={AppContext.BaseDirectory}\nRuntime={RuntimeInformation.FrameworkDescription}\n"
                + $"DynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported}\nServerGC={GCSettings.IsServerGC}\n"
                + $"AssemblyVersion={typeof(MemoryDiagnosticSession).Assembly.GetName().Version}\n"
                + $"AssemblyBuildId={typeof(MemoryDiagnosticSession).Module.ModuleVersionId}\n"
                + $"IntervalSeconds={interval.TotalSeconds.ToString(CultureInfo.InvariantCulture)}\n"
                + "No forced GC; ManagedBytes is an estimate. Heap/commit/fragmentation describe the last GC.\n"
                + "WorkingSetBytes includes shared pages; use external PrivateWorkingSetBytes for the Task Manager comparison.\n"
                + "Allocation totals include the small overhead of this logger. Stops at 2h / 1441 samples / 4MiB.\n",
                Encoding.UTF8);
            await writer.WriteLineAsync("Timestamp,ElapsedSeconds,Pid,WorkingSetBytes,PrivateBytes,ManagedBytes,LastGcHeapBytes,GcCommittedBytes,LastGcFragmentedBytes,TotalAllocatedBytes,AllocatedDeltaBytes,Gen0,Gen1,Gen2,Handles,Threads,GdiObjects,UserObjects,CpuSeconds,UiCaptureDelayMs,SnapshotRows,FilteredRows,DetailNodes,DetailPorts,HudRows,KernelRows,AutoRefresh,Scanning,ScanError,ListHolds,MergePending,CurrentView,HudVisible,WindowVisible,Minimized,RetainedContainers,CompletedScans,LastScanMs,PublishedScans,ChangedPublishes,Merges,InspectorQueries,InspectorPending");
            long previousAllocated = GC.GetTotalAllocatedBytes(false);
            using var process = Process.GetCurrentProcess();
            while (clock.Elapsed <= duration && samples < 1441)
            {
                _stop.Token.ThrowIfCancellationRequested();
                long requested = Stopwatch.GetTimestamp();
                var state = await Dispatcher.UIThread.InvokeAsync(capture, DispatcherPriority.Background, _stop.Token);
                double uiDelay = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                process.Refresh();
                var gc = GC.GetGCMemoryInfo();
                long allocated = GC.GetTotalAllocatedBytes(false);
                object[] values =
                {
                    DateTimeOffset.Now.ToString("o"), clock.Elapsed.TotalSeconds, Environment.ProcessId,
                    process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), gc.HeapSizeBytes,
                    gc.TotalCommittedBytes, gc.FragmentedBytes, allocated, allocated - previousAllocated,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), process.HandleCount,
                    process.Threads.Count, GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1),
                    process.TotalProcessorTime.TotalSeconds, uiDelay, state.SnapshotRows, state.FilteredRows,
                    state.DetailNodes, state.DetailPorts, state.HudRows, state.KernelRows, state.AutoRefresh,
                    state.Scanning, state.ScanError, state.ListHolds, state.MergePending, state.CurrentView,
                    state.HudVisible, state.WindowVisible, state.Minimized, state.RetainedContainers,
                    state.CompletedScans, state.LastScanMs, state.PublishedScans, state.ChangedPublishes,
                    state.Merges, state.InspectorQueries, state.InspectorPending
                };
                await writer.WriteLineAsync(string.Join(",", values.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))));
                previousAllocated = allocated;
                samples++;
                if (writer.BaseStream.Position >= 4 * 1024 * 1024) { reason = "size-limit"; break; }
                await Task.Delay(interval, _stop.Token);
            }
            if (samples >= 1441) reason = "sample-limit";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            reason = Volatile.Read(ref _manualStop) != 0 ? "stopped" : "duration-limit";
        }
        catch (Exception ex)
        {
            reason = "error:" + ex.GetType().Name;
            App.WriteLog("MemoryDiagnostics", ex);
        }
        finally
        {
            try
            {
                if (ownsLog)
                    await File.AppendAllTextAsync(LogPath + ".metadata.txt", $"Ended={DateTimeOffset.Now:o}\nSamples={samples}\nReason={reason}\n");
            }
            catch { }
            _stop.Dispose();
        }
    }

    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
}

internal readonly record struct MemoryDiagnosticState(
    int SnapshotRows, int FilteredRows, int DetailNodes, int DetailPorts, int HudRows, int KernelRows,
    bool AutoRefresh, bool Scanning, bool ScanError, int ListHolds, bool MergePending, int CurrentView,
    bool HudVisible, bool WindowVisible, bool Minimized, int RetainedContainers, long CompletedScans,
    double LastScanMs, long PublishedScans, long ChangedPublishes, long Merges, long InspectorQueries, long InspectorPending);
