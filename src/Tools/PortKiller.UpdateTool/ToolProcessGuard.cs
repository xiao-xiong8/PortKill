using System.Diagnostics;
using System.Globalization;
using PortKiller.Updates;

namespace PortKiller.UpdateTool;

/// <summary>Stops an owned tool after its publisher exits, including before a delayed child starts work.</summary>
public sealed class ToolProcessGuard : IDisposable
{
    private readonly Process _owner;
    private readonly long _ownerStartTicks;
    private readonly TimeSpan _maximumRuntime;
    private readonly Stopwatch _runtime = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watchdog;

    private ToolProcessGuard(int ownerPid, long ownerStartTicks, int maximumSeconds)
    {
        _owner = Process.GetProcessById(ownerPid);
        _ownerStartTicks = ownerStartTicks;
        _maximumRuntime = TimeSpan.FromSeconds(maximumSeconds);
        try { Check(); }
        catch { _owner.Dispose(); _stop.Dispose(); throw; }
        _watchdog = WatchAsync();
    }

    public static ToolProcessGuard? FromArguments(IReadOnlyDictionary<string, string> flags, int maximumAllowedSeconds = 60)
    {
        bool pid = flags.TryGetValue("--owner-pid", out string? pidText);
        bool start = flags.TryGetValue("--owner-start-ticks", out string? startText);
        bool runtime = flags.TryGetValue("--max-runtime-seconds", out string? runtimeText);
        if (!pid && !start && !runtime) return null;
        if (!pid || !start || !runtime
            || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int ownerPid) || ownerPid <= 0
            || !long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0
            || !int.TryParse(runtimeText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds < 1 || seconds > maximumAllowedSeconds)
            throw new ArgumentException("The tool owner identity and runtime bound are required together.");
        try { return new(ownerPid, ticks, seconds); }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
        { throw new InvalidOperationException("The publisher process is no longer available."); }
    }

    public void Check()
    {
        if (_runtime.Elapsed >= _maximumRuntime || _owner.HasExited
            || PublisherProcessIdentity.ReadStartToken(_owner) != _ownerStartTicks)
            throw new InvalidOperationException("The publisher process or execution deadline is no longer valid.");
    }

    private async Task WatchAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(200, _stop.Token);
                // Hard exit releases the OS file locks even if local disk I/O is stalled.
                try { Check(); }
                catch { Environment.Exit(1); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _watchdog.GetAwaiter().GetResult();
        _stop.Dispose();
        _owner.Dispose();
    }
}
