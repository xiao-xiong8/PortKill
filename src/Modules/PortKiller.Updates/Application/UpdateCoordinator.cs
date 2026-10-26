using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

/// <summary>Only one pending check is retained. Busy periods and server cooldowns never discard it.</summary>
internal sealed class UpdateCoordinator(UpdateOptions options, HttpClient client, Func<bool> enabled,
    Func<bool> available, Func<CancellationToken, Task<bool>> check, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _pending;
    private long _hintAfter;
    internal void RequestCheck()
    {
        Interlocked.Exchange(ref _hintAfter, 0);
        Interlocked.Exchange(ref _pending, 1);
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private void RequestHint()
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        Interlocked.Exchange(ref _hintAfter, _clock.GetUtcNow().AddMilliseconds(Random.Shared.Next(501)).UtcTicks);
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    internal async Task RunAsync(CancellationToken token)
    {
        CancellationTokenSource? eventsCancellation = null;
        Task? events = null;
        bool wasEnabled = false;
        bool firstTick = true;
        var nextPoll = _clock.GetUtcNow().AddSeconds(10 + Random.Shared.NextDouble() * 5);
        var lastTick = _clock.GetUtcNow();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = _clock.GetUtcNow();
                bool active = enabled();
                // A suspended machine does not tick; the first tick after resume requests a check.
                if (active && now - lastTick > TimeSpan.FromSeconds(30)) RequestCheck();
                lastTick = now;
                if (!active && eventsCancellation != null)
                {
                    eventsCancellation.Cancel();
                    try { await events!; } catch (OperationCanceledException) { }
                    eventsCancellation.Dispose(); eventsCancellation = null; events = null;
                }
                if (active && !wasEnabled && !firstTick) RequestCheck();
                if (active && now >= nextPoll)
                {
                    RequestCheck();
                    nextPoll = now.AddSeconds(options.PollSeconds);
                }
                if (active && Volatile.Read(ref _pending) != 0 && now.UtcTicks >= Interlocked.Read(ref _hintAfter) && available())
                {
                    Interlocked.Exchange(ref _pending, 0);
                    if (!await check(token)) Interlocked.Exchange(ref _pending, 1);
                    if (options.RealtimeEnabled && eventsCancellation == null)
                    {
                        eventsCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        events = new UpdateEventClient(client, options.GetEventsUri()).RunAsync(RequestHint, eventsCancellation.Token);
                    }
                }
                wasEnabled = active;
                firstTick = false;
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), token);
                // A wake generated during a failed check must not create a tight loop.
                if (Volatile.Read(ref _pending) != 0) await Task.Delay(100, token);
            }
        }
        finally
        {
            if (eventsCancellation != null)
            {
                eventsCancellation.Cancel();
                try { await events!; } catch (OperationCanceledException) { }
                eventsCancellation.Dispose();
            }
        }
    }
}
