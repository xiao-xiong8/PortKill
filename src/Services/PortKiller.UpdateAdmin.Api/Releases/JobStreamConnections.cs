using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Releases;

/// <summary>Per-process limits are keyed by account, including connections from different sessions.</summary>
public sealed class JobStreamConnections(AdminOptions options)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _users = new(StringComparer.Ordinal);
    private int _total;
    public int ActiveConnections { get { lock (_gate) return _total; } }

    public IDisposable Acquire(string userId)
    {
        lock (_gate)
        {
            int count = _users.GetValueOrDefault(userId);
            if (_total >= options.JobStreamMaximumConnections || count >= options.JobStreamMaximumConnectionsPerUser)
                throw new AdminFailure("job_stream_limit", 429, "实时任务连接已达上限，请稍后重试。");
            _users[userId] = count + 1; _total++;
        }
        return new Lease(() =>
        {
            lock (_gate)
            {
                if (_users[userId] == 1) _users.Remove(userId); else _users[userId]--;
                _total--;
            }
        });
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
