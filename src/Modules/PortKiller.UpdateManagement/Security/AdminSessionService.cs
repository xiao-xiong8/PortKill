using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record SessionTicket(AdminUser User, AdminSession Session);
public sealed record SessionView(string Id, SessionPhase Phase, string AuthenticationMethod, long CreatedUtcMs,
    long LastSeenUtcMs, long ExpiresUtcMs, bool Current);
public sealed record CurrentSessionView(string UserId, string UserName, SessionPhase Phase, string CurrentSessionId,
    string[] Roles, string[] Permissions, long ExpiresUtcMs, long? StrongAuthenticationExpiresUtcMs);

public sealed class AdminSessionService(UpdateAdminDbContext database, UserManager<AdminUser> users, AdminOptions options, TimeProvider clock)
{
    public async Task<SessionTicket> CreateAsync(AdminUser user, SessionPhase phase, string method = "password", CancellationToken token = default)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var session = new AdminSession
        {
            UserId = user.Id, SecurityStamp = await users.GetSecurityStampAsync(user), Phase = phase,
            AuthenticationMethod = method, CreatedUtcMs = now, LastSeenUtcMs = now,
            AbsoluteExpiresUtcMs = now + (long)(phase == SessionPhase.Full ? TimeSpan.FromHours(options.AbsoluteHours) : TimeSpan.FromMinutes(options.PendingMinutes)).TotalMilliseconds,
            StrongAuthenticationUtcMs = method == "totp" ? now : null
        };
        database.AdminSessions.Add(session);
        await database.SaveChangesAsync(token);
        return new(user, session);
    }

    public async Task<SessionTicket?> ValidateAsync(ClaimsPrincipal principal, CancellationToken token = default)
    {
        string? id = principal.FindFirstValue(AdminIdentity.SessionClaim);
        string? userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id == null || userId == null) return null;
        var session = await database.AdminSessions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, token);
        var user = await users.FindByIdAsync(userId);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long idle = (long)TimeSpan.FromMinutes(options.IdleMinutes).TotalMilliseconds;
        if (session == null || user == null || user.Disabled || session.RevokedUtcMs != null
            || session.SecurityStamp != await users.GetSecurityStampAsync(user)
            || principal.FindFirstValue(AdminIdentity.PhaseClaim) != session.Phase.ToString()
            || now >= session.AbsoluteExpiresUtcMs || now - session.LastSeenUtcMs >= idle
            || !await IsActiveAsync(id, userId, session.Phase, token))
            return null;
        if (now - session.LastSeenUtcMs >= Math.Min(60_000, idle / 3))
        { session.LastSeenUtcMs = now; await database.SaveChangesAsync(token); }
        return new(user, session);
    }

    // Long-lived streams can poll this without trusting EF's previously tracked entities.
    public async Task<bool> IsActiveAsync(string sessionId, string userId, SessionPhase phase, CancellationToken token)
    {
        var state = await database.AdminSessions.AsNoTracking().Where(session => session.Id == sessionId && session.UserId == userId)
            .Join(database.Users.AsNoTracking(), session => session.UserId, user => user.Id, (session, user) => new
            {
                session.Phase, session.RevokedUtcMs, session.AbsoluteExpiresUtcMs, session.LastSeenUtcMs,
                StoredStamp = session.SecurityStamp, CurrentStamp = user.SecurityStamp, user.Disabled
            }).SingleOrDefaultAsync(token);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return state != null && !state.Disabled && state.Phase == phase && state.RevokedUtcMs == null
            && state.StoredStamp == state.CurrentStamp && now < state.AbsoluteExpiresUtcMs
            && now - state.LastSeenUtcMs < TimeSpan.FromMinutes(options.IdleMinutes).TotalMilliseconds;
    }

    public async Task<ClaimsPrincipal> PrincipalAsync(SessionTicket ticket)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ticket.User.Id), new(ClaimTypes.Name, ticket.User.UserName!),
            new(AdminIdentity.SessionClaim, ticket.Session.Id), new(AdminIdentity.PhaseClaim, ticket.Session.Phase.ToString())
        };
        if (ticket.Session.Phase == SessionPhase.Full)
        {
            var roles = await users.GetRolesAsync(ticket.User);
            foreach (string role in roles) claims.Add(new(ClaimTypes.Role, role));
            foreach (string permission in AdminPermissions.ForRoles(roles)) claims.Add(new(AdminPermissions.Claim, permission));
            if (ticket.Session.StrongAuthenticationUtcMs is { } strongAt)
                claims.Add(new("strong_auth_at", strongAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminIdentity.Scheme));
    }

    public async Task<CurrentSessionView> CurrentAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        var ticket = await ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        string[] roles = ticket.Session.Phase == SessionPhase.Full ? (await users.GetRolesAsync(ticket.User)).ToArray() : [];
        return new(ticket.User.Id, ticket.User.UserName!, ticket.Session.Phase, ticket.Session.Id,
            roles, AdminPermissions.ForRoles(roles),
            Expires(ticket.Session), ticket.Session.StrongAuthenticationUtcMs is { } strongAt
                ? strongAt + (long)TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds : null);
    }

    private long Expires(AdminSession session) => Math.Min(session.AbsoluteExpiresUtcMs,
        session.LastSeenUtcMs + (long)TimeSpan.FromMinutes(options.IdleMinutes).TotalMilliseconds);

    public async Task<Contracts.PageResponse<SessionView>> ListAsync(ClaimsPrincipal principal, int page, int pageSize, CancellationToken token)
    {
        if (page < 1 || page > 10000 || pageSize is < 1 or > 100) throw new AdminFailure("invalid_pagination", 400, "分页参数超出范围。");
        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        string? currentId = principal.FindFirstValue(AdminIdentity.SessionClaim);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long idleCutoff = now - (long)TimeSpan.FromMinutes(options.IdleMinutes).TotalMilliseconds;
        var query = database.AdminSessions.AsNoTracking().Where(x => x.UserId == userId && x.RevokedUtcMs == null
            && x.AbsoluteExpiresUtcMs > now && x.LastSeenUtcMs > idleCutoff);
        long total = await query.LongCountAsync(token);
        var sessions = await query.OrderByDescending(x => x.CreatedUtcMs).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        return new(sessions.Select(x => new SessionView(x.Id, x.Phase, x.AuthenticationMethod, x.CreatedUtcMs, x.LastSeenUtcMs,
            Expires(x), x.Id == currentId)).ToArray(), page, pageSize, total);
    }

    public async Task RevokeAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        string? userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        int changed = await database.AdminSessions.Where(x => x.Id == id && x.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedUtcMs, now), token);
        if (changed == 0) throw new AdminFailure("resource_not_found", 404, "会话不存在。");
    }

    public async Task CleanupAsync(CancellationToken token)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long cutoff = now - (long)TimeSpan.FromDays(7).TotalMilliseconds;
        await database.TotpConsumptions.Where(x => x.ExpiresUtcMs < now).OrderBy(x => x.ExpiresUtcMs).Take(500).ExecuteDeleteAsync(token);
        await database.AdminSessions.Where(x => (x.AbsoluteExpiresUtcMs < cutoff || x.RevokedUtcMs < cutoff)
            && !database.PublishApprovals.Any(approval => approval.SessionId == x.Id))
            .OrderBy(x => x.AbsoluteExpiresUtcMs).Take(500).ExecuteDeleteAsync(token);
    }
}

public sealed class PasswordAuthenticationService(UserManager<AdminUser> users, SignInManager<AdminUser> signIn,
    AdminSessionService sessions, DummyPasswordVerifier dummy)
{
    public async Task<SessionTicket> LoginAsync(string? userName, string? password, CancellationToken token = default)
    {
        if (!AdminIdentity.ValidUserName(userName) || password is not { Length: > 0 and <= 256 })
            throw new AdminFailure("invalid_credentials", 401, "账号或凭据无效。");
        var user = await users.FindByNameAsync(userName!);
        if (user == null)
        { dummy.Check(password); throw new AdminFailure("invalid_credentials", 401, "账号或凭据无效。"); }
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded || user.Disabled)
            throw new AdminFailure("invalid_credentials", 401, "账号或凭据无效。");
        var phase = !user.TwoFactorEnabled ? SessionPhase.EnrollmentRequired
            : !user.RecoveryCodesAcknowledged ? SessionPhase.RecoveryAcknowledgementRequired : SessionPhase.MfaRequired;
        return await sessions.CreateAsync(user, phase, token: token);
    }
}
