using System.Net;
using PortKiller.UpdateManagement.Configuration;

namespace PortKiller.UpdateManagement.Security;

/// <summary>Single-instance, bounded password failure windows; no credentials are stored.</summary>
public sealed class LoginProtectionService(AdminOptions options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FailureState> _states = new(StringComparer.Ordinal);
    private long _nextCleanup;

    public bool IsBlocked(string? userName, IPAddress? address, out int retryAfterSeconds)
    {
        lock (_gate)
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            Cleanup(now);
            string account = Account(userName), pair = Pair(account, address);
            retryAfterSeconds = Math.Max(Remaining(account, now), Remaining(pair, now));
            if (retryAfterSeconds == 0 && _states.Count + Missing(account, pair) > options.LoginProtectionMaximumEntries)
                retryAfterSeconds = 60; // Fail closed rather than evicting active protection under a spray attack.
            return retryAfterSeconds > 0;
        }
    }

    public int RecordFailure(string? userName, IPAddress? address)
    {
        lock (_gate)
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            Cleanup(now);
            string account = Account(userName), pair = Pair(account, address);
            int wait = Math.Max(Remaining(account, now), Remaining(pair, now));
            if (wait > 0) return wait;
            if (_states.Count + Missing(account, pair) > options.LoginProtectionMaximumEntries) return 60;
            int accountWait = Failure(account, now, options.LoginAccountFailureLimit, options.LoginAccountLockoutMinutes * 60);
            int pairWait = Failure(pair, now, options.LoginAccountIpFailureLimit, options.LoginAccountIpLockoutSeconds);
            return Math.Max(accountWait, pairWait);
        }
    }

    // Called only after complete authentication, never merely after a correct password.
    public void RecordSuccess(string? userName, IPAddress? address)
    {
        lock (_gate)
        {
            string account = Account(userName);
            _states.Remove(account); _states.Remove(Pair(account, address));
        }
    }

    private int Missing(string account, string pair) => (_states.ContainsKey(account) ? 0 : 1) + (_states.ContainsKey(pair) ? 0 : 1);
    private int Remaining(string key, long now)
    {
        if (!_states.TryGetValue(key, out var state)) return 0;
        if (state.BlockedUntil > now) return Seconds(state.BlockedUntil, now);
        if (state.BlockedUntil != 0 || state.WindowEnd <= now) _states.Remove(key);
        return 0;
    }
    private int Failure(string key, long now, int limit, int lockoutSeconds)
    {
        if (!_states.TryGetValue(key, out var state))
            _states[key] = state = new() { WindowEnd = now + options.LoginFailureWindowMinutes * 60_000L };
        if (++state.Failures < limit) return 0;
        state.BlockedUntil = now + lockoutSeconds * 1000L;
        return lockoutSeconds;
    }
    private void Cleanup(long now)
    {
        if (now < _nextCleanup && _states.Count < options.LoginProtectionMaximumEntries) return;
        foreach (string key in _states.Where(x => x.Value.BlockedUntil == 0 ? x.Value.WindowEnd <= now : x.Value.BlockedUntil <= now)
            .Select(x => x.Key).ToArray()) _states.Remove(key);
        _nextCleanup = now + 30_000;
    }
    private static int Seconds(long deadline, long now) => Math.Max(1, (int)Math.Ceiling((deadline - now) / 1000d));
    private static string Account(string? name)
    {
        string normalized = (name ?? "").Trim().ToUpperInvariant();
        return "account:" + (normalized.Length > 128 ? normalized[..128] : normalized);
    }
    private static string Pair(string account, IPAddress? address) => account + "\u001f" + (address?.ToString() ?? "unknown");
    private sealed class FailureState
    {
        public int Failures;
        public long WindowEnd;
        public long BlockedUntil;
    }
}
