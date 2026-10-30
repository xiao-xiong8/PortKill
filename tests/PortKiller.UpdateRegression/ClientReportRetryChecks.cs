using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class ClientReportRetryChecks
{
    internal static async Task Run()
    {
        using var fixture = new Fixture();
        var endpoint = new Uri("https://updates.example.com/api/v1/client-installations/");
        var clock = new ReportClock();
        var store = new ClientIdentityStore(Path.Combine(fixture.Root, "offline.json"));
        bool online = false;
        int requests = 0;
        using (var service = new ClientReportService(endpoint, store: store, clock: clock,
            handler: new Handler((request, _) => {
                requests++;
                if (!online) throw new HttpRequestException("offline");
                return Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("register")
                    ? HttpStatusCode.Created : HttpStatusCode.NoContent));
            })))
        {
            await service.ReportAsync("1.2.3", "installed");
            That(requests == 3, "Initial retries are not bounded");
            using (store.AcquireLease()) That(store.LoadOrCreate().PendingReport?.Result == "installed", "Offline outcome was lost");
            online = true;
            await service.RetryPendingAsync(networkAvailable: true);
            That(requests == 5, "Network recovery did not register and deliver the pending report");
            using (store.AcquireLease()) That(store.LoadOrCreate().PendingReport == null, "Successful delivery stayed pending");
            clock.Advance(TimeSpan.FromHours(1));
            await service.RetryPendingAsync();
            That(requests == 5, "Reporting created an idle heartbeat after delivery");
        }

        string pendingPath = Path.Combine(fixture.Root, "rate-limited.json");
        var rateStore = new ClientIdentityStore(pendingPath);
        using (var first = new ClientReportService(endpoint, store: rateStore, clock: clock,
            handler: new Handler((request, _) => {
                var response = new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("register")
                    ? HttpStatusCode.Created : HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return Task.FromResult(response);
            }))) await first.ReportAsync("1.2.3", "installed");

        requests = 0;
        string? deliveredResult = null;
        using (var restarted = new ClientReportService(endpoint, store: new ClientIdentityStore(pendingPath), clock: clock,
            handler: new Handler(async (request, token) => {
                requests++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                deliveredResult = json.RootElement.GetProperty("result").GetString();
                return new(HttpStatusCode.NoContent);
            })))
        {
            That(await restarted.QueueAsync("1.2.3", "started"), "Restart could not queue its startup state");
            await restarted.RetryPendingAsync(networkAvailable: true);
            That(requests == 0, "Network notification bypassed persisted Retry-After");
            clock.Advance(TimeSpan.FromSeconds(59)); await restarted.RetryPendingAsync();
            That(requests == 0, "Report retried before the rate limit deadline");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Eventually(() => requests == 1);
            await restarted.RetryPendingAsync();
            That(deliveredResult == "installed", "Startup overwrote an undelivered installation outcome");
            using (rateStore.AcquireLease()) That(rateStore.LoadOrCreate().PendingReport == null, "Restarted delivery was not acknowledged");
        }

        requests = 0;
        var budgetStore = new ClientIdentityStore(Path.Combine(fixture.Root, "budget.json"));
        using (var unavailable = new ClientReportService(endpoint, store: budgetStore, clock: clock,
            handler: new Handler((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); })))
        {
            await unavailable.ReportAsync("1.2.3", "started");
            for (int attempt = 2; attempt <= 20; attempt++)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
                int expected = attempt;
                await Eventually(() => requests == expected);
                // Wait for persistence and timer scheduling before advancing the fake clock again.
                await unavailable.RetryPendingAsync();
            }
            clock.Advance(TimeSpan.FromDays(1));
            That(requests == 20, "Scheduled retries are unbounded");
            await unavailable.RetryPendingAsync(networkAvailable: true);
            That(requests == 21, "Explicit network recovery did not resume a deferred report");
        }

        var coalescedStore = new ClientIdentityStore(Path.Combine(fixture.Root, "coalesced.json"));
        string? deliveredVersion = null;
        using (var coalesced = new ClientReportService(endpoint, store: coalescedStore, clock: clock,
            handler: new Handler(async (request, token) => {
                if (request.RequestUri!.AbsolutePath.EndsWith("register")) return new(HttpStatusCode.Created);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                deliveredVersion = json.RootElement.GetProperty("version").GetString();
                return new(HttpStatusCode.NoContent);
            })))
        {
            await coalesced.QueueAsync("1.2.3", "installed");
            await coalesced.QueueAsync("1.2.4", "started");
            await coalesced.RetryPendingAsync();
            That(deliveredVersion == "1.2.4", "An older deferred report overwrote the current version");
        }
    }

    private sealed class ReportClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ReportTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ReportTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class ReportTimer(ReportClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due = DateTimeOffset.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
                return true;
            }
            internal void Fire()
            {
                if (_disposed || _due > clock.GetUtcNow()) return;
                _due = DateTimeOffset.MaxValue; callback(state);
            }
            public void Dispose() { _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
