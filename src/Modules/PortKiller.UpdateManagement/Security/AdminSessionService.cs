using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record SessionTicket(AdminUser User, AdminSession Session);

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
            || now >= session.AbsoluteExpiresUtcMs || now - session.LastSeenUtcMs >= idle)
            return null;
        if (now - session.LastSeenUtcMs >= Math.Min(60_000, idle / 3))
        { session.LastSeenUtcMs = now; await database.SaveChangesAsync(token); }
        return new(user, session);
    }

    public async Task<ClaimsPrincipal> PrincipalAsync(SessionTicket ticket)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, ticket.User.Id), new(ClaimTypes.Name, ticket.User.UserName!),
            new(AdminIdentity.SessionClaim, ticket.Session.Id), new(AdminIdentity.PhaseClaim, ticket.Session.Phase.ToString())
        };
        if (ticket.Session.Phase == SessionPhase.Full)
            foreach (string role in await users.GetRolesAsync(ticket.User)) claims.Add(new(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminIdentity.Scheme));
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
