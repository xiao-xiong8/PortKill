using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record AccountView(string Id, string UserName, bool Disabled, bool MfaEnabled, bool RecoveryAcknowledged, string[] Roles);
public sealed record CreateAccountRequest(string UserName, string Password, string[] Roles);
public sealed record ChangeRolesRequest(string[] Roles);
public sealed record AccountStateRequest(bool Disabled);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed class AdminAccountService(UpdateAdminDbContext database, UserManager<AdminUser> users,
    AdminSessionService sessions, AdminOptions options, TimeProvider clock, IUserStore<AdminUser> userStore)
{
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static string[] ValidateRoles(string[]? roles)
    {
        if (roles is not { Length: > 0 and <= 4 } || roles.Distinct().Count() != roles.Length || roles.Any(role => !AdminIdentity.Roles.Contains(role)))
            throw new AdminFailure("invalid_roles", 400, "角色配置无效。");
        return roles;
    }

    public async Task<PageResponse<AccountView>> ListAsync(int page, int pageSize, CancellationToken token)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100) throw new AdminFailure("invalid_pagination", 400, "分页参数超出范围。");
        var selected = await database.Users.AsNoTracking().OrderBy(x => x.CreatedUtcMs).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var ids = selected.Select(x => x.Id).ToArray();
        var roles = await database.UserRoles.Where(x => ids.Contains(x.UserId)).Join(database.Roles,
            link => link.RoleId, role => role.Id, (link, role) => new { link.UserId, role.Name }).ToListAsync(token);
        return new(selected.Select(user => new AccountView(user.Id, user.UserName!, user.Disabled, user.TwoFactorEnabled,
            user.RecoveryCodesAcknowledged, roles.Where(role => role.UserId == user.Id).Select(role => role.Name!).Order().ToArray())).ToArray(),
            page, pageSize, await database.Users.LongCountAsync(token));
    }

    // Recheck permissions and session inside each short write transaction, including in-flight revocations.
    private async Task<SessionTicket> RequireActorAsync(ClaimsPrincipal principal, bool administrator, CancellationToken token)
    {
        var actor = await sessions.ValidateAsync(principal, token) ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        await database.Entry(actor.User).ReloadAsync(token); await database.Entry(actor.Session).ReloadAsync(token);
        if (!await sessions.IsActiveAsync(actor.Session.Id, actor.User.Id, SessionPhase.Full, token))
            throw new AdminFailure("authentication_required", 401, "会话已失效。");
        if (actor.Session.StrongAuthenticationUtcMs is not { } at || Now < at
            || Now - at >= TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds)
            throw new AdminFailure("reauthentication_required", 403, "请先完成近期强认证。");
        if (administrator && !await users.IsInRoleAsync(actor.User, "Administrator"))
            throw new AdminFailure("permission_denied", 403, "当前账号无权执行此操作。");
        return actor;
    }

    private async Task<AdminUser> TargetAsync(string id, CancellationToken token)
    {
        var user = await users.FindByIdAsync(id) ?? throw new AdminFailure("resource_not_found", 404, "账号不存在。");
        await database.Entry(user).ReloadAsync(token); return user;
    }

    private async Task ProtectLastAdministratorAsync(AdminUser target, CancellationToken token)
    {
        if (target.Disabled || !target.TwoFactorEnabled || !target.RecoveryCodesAcknowledged || !await users.IsInRoleAsync(target, "Administrator")) return;
        bool another = await (from user in database.Users
            join link in database.UserRoles on user.Id equals link.UserId
            join role in database.Roles on link.RoleId equals role.Id
            where user.Id != target.Id && !user.Disabled && user.TwoFactorEnabled && user.RecoveryCodesAcknowledged && role.Name == "Administrator"
            select user.Id).AnyAsync(token);
        if (!another) throw new AdminFailure("last_administrator", 409, "必须保留至少一个已完成 MFA 的有效管理员。");
    }

    private async Task InvalidateAsync(AdminUser target, CancellationToken token)
    {
        AdminBootstrap.Ensure(await users.UpdateSecurityStampAsync(target));
        await database.AdminSessions.Where(x => x.UserId == target.Id && x.RevokedUtcMs == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedUtcMs, Now), token);
    }

    public async Task<string> CreateAsync(ClaimsPrincipal principal, CreateAccountRequest request, CancellationToken token)
    {
        string[] roles = ValidateRoles(request.Roles);
        if (!AdminIdentity.ValidUserName(request.UserName) || request.Password is not { Length: <= 256 })
            throw new AdminFailure("invalid_request", 400, "账号或密码格式无效。");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RequireActorAsync(principal, true, token);
        var user = new AdminUser { UserName = request.UserName, CreatedUtcMs = Now, LockoutEnabled = true };
        AdminBootstrap.Ensure(await users.CreateAsync(user, request.Password));
        AdminBootstrap.Ensure(await users.AddToRolesAsync(user, roles));
        await transaction.CommitAsync(token); return user.Id;
    }

    public async Task ChangeRolesAsync(ClaimsPrincipal principal, string id, string[]? requestedRoles, CancellationToken token)
    {
        string[] roles = ValidateRoles(requestedRoles);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RequireActorAsync(principal, true, token); var target = await TargetAsync(id, token);
        if (!roles.Contains("Administrator")) await ProtectLastAdministratorAsync(target, token);
        var current = await users.GetRolesAsync(target);
        AdminBootstrap.Ensure(await users.RemoveFromRolesAsync(target, current.Except(roles)));
        AdminBootstrap.Ensure(await users.AddToRolesAsync(target, roles.Except(current)));
        await InvalidateAsync(target, token); await transaction.CommitAsync(token);
    }

    public async Task SetDisabledAsync(ClaimsPrincipal principal, string id, bool disabled, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RequireActorAsync(principal, true, token); var target = await TargetAsync(id, token);
        if (disabled) await ProtectLastAdministratorAsync(target, token);
        target.Disabled = disabled; AdminBootstrap.Ensure(await users.UpdateAsync(target));
        await InvalidateAsync(target, token); await transaction.CommitAsync(token);
    }

    public async Task ResetMfaAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RequireActorAsync(principal, true, token); var target = await TargetAsync(id, token);
        await ProtectLastAdministratorAsync(target, token);
        await ResetMfaCoreAsync(target, token); await transaction.CommitAsync(token);
    }

    public async Task ResetMfaOfflineAsync(string name, CancellationToken token = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var target = await users.FindByNameAsync(name) ?? throw new AdminFailure("resource_not_found", 404, "账号不存在。");
        await ResetMfaCoreAsync(target, token); await transaction.CommitAsync(token);
    }

    private async Task ResetMfaCoreAsync(AdminUser target, CancellationToken token)
    {
        AdminBootstrap.Ensure(await users.SetTwoFactorEnabledAsync(target, false));
        AdminBootstrap.Ensure(await users.ResetAuthenticatorKeyAsync(target));
        var store = (IUserTwoFactorRecoveryCodeStore<AdminUser>)userStore;
        await store.ReplaceCodesAsync(target, [], token);
        target.RecoveryCodesAcknowledged = false;
        AdminBootstrap.Ensure(await users.UpdateAsync(target)); await InvalidateAsync(target, token);
    }

    public async Task ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest request, CancellationToken token)
    {
        if (request.CurrentPassword is not { Length: > 0 and <= 256 } || request.NewPassword is not { Length: <= 256 })
            throw new AdminFailure("invalid_request", 400, "密码格式无效。");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var actor = await RequireActorAsync(principal, false, token);
        AdminBootstrap.Ensure(await users.ChangePasswordAsync(actor.User, request.CurrentPassword, request.NewPassword));
        await InvalidateAsync(actor.User, token); await transaction.CommitAsync(token);
    }

    public async Task<string[]> RegenerateRecoveryCodesAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var actor = await RequireActorAsync(principal, false, token);
        string[] codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(actor.User, 10))!.ToArray();
        await InvalidateAsync(actor.User, token);
        // Keep only the current authenticated session; all other cookies become invalid immediately.
        actor.Session.RevokedUtcMs = null; actor.Session.SecurityStamp = await users.GetSecurityStampAsync(actor.User);
        database.Entry(actor.Session).Property(x => x.RevokedUtcMs).IsModified = true;
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token); return codes;
    }
}
