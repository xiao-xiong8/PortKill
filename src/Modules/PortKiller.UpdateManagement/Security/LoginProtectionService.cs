using System.Collections.Concurrent;
using System.Net;
using PortKiller.UpdateManagement.Configuration;

namespace PortKiller.UpdateManagement.Security;

/// <summary>
/// 单实例登录保护。它只保存短期失败计数，不保存密码、令牌或账号资料。
/// </summary>
public sealed class LoginProtectionService(AdminOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, FailureState> _states = new(StringComparer.Ordinal);

    public bool IsBlocked(string? userName, IPAddress? address, out int retryAfterSeconds)
    {
        string key = Key(userName, address);
        if (!_states.TryGetValue(key, out var state))
        {
            retryAfterSeconds = 0;
            return false;
        }

        lock (state)
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (state.BlockedUntilUtcMs <= now)
            {
                if (state.Failures == 0) _states.TryRemove(key, out _);
                retryAfterSeconds = 0;
                return false;
            }

            retryAfterSeconds = SecondsUntil(state.BlockedUntilUtcMs, now);
            return true;
        }
    }

    public int RecordFailure(string? userName, IPAddress? address)
    {
        string key = Key(userName, address);
        var state = _states.GetOrAdd(key, static _ => new FailureState());
        lock (state)
        {
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (state.BlockedUntilUtcMs > now)
                return SecondsUntil(state.BlockedUntilUtcMs, now);

            if (state.BlockedUntilUtcMs != 0)
            {
                state.Failures = 0;
                state.BlockedUntilUtcMs = 0;
            }

            state.Failures++;
            if (state.Failures < options.LoginAccountIpFailureLimit) return 0;

            state.BlockedUntilUtcMs = now + (long)TimeSpan.FromMinutes(options.LoginAccountIpLockoutMinutes).TotalMilliseconds;
            return SecondsUntil(state.BlockedUntilUtcMs, now);
        }
    }

    public void RecordSuccess(string? userName, IPAddress? address)
        => _states.TryRemove(Key(userName, address), out _);

    private static int SecondsUntil(long deadline, long now)
        => Math.Max(1, (int)Math.Ceiling((deadline - now) / 1000d));

    private static string Key(string? userName, IPAddress? address)
    {
        string normalized = (userName ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length > 128) normalized = normalized[..128];
        return normalized + "\u001f" + (address?.ToString() ?? "unknown");
    }

    private sealed class FailureState
    {
        public int Failures;
        public long BlockedUntilUtcMs;
    }
}
