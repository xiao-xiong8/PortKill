using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record EnrollmentSecret(string SharedKey, string AuthenticatorUri);
public sealed record EnrollmentConfirmation(SessionTicket Ticket, string[] RecoveryCodes);

public sealed class AdminMfaService(UpdateAdminDbContext database, UserManager<AdminUser> users,
    AdminSessionService sessions, TimeProvider clock)
{
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private async Task<SessionTicket> RequireAsync(ClaimsPrincipal principal, CancellationToken token, params SessionPhase[] phases)
    {
        var ticket = await sessions.ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效，请重新登录。");
        await database.Entry(ticket.User).ReloadAsync(token);
        await database.Entry(ticket.Session).ReloadAsync(token);
        ticket = await sessions.ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效，请重新登录。");
        if (!phases.Contains(ticket.Session.Phase)) throw new AdminFailure("mfa_phase_invalid", 403, "当前认证阶段不允许此操作。");
        return ticket;
    }

    public async Task<EnrollmentSecret> StartEnrollmentAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.EnrollmentRequired);
        string? key = await users.GetAuthenticatorKeyAsync(ticket.User);
        if (string.IsNullOrWhiteSpace(key))
        {
            AdminBootstrap.Ensure(await users.ResetAuthenticatorKeyAsync(ticket.User));
            key = await users.GetAuthenticatorKeyAsync(ticket.User);
            ticket.Session.SecurityStamp = await users.GetSecurityStampAsync(ticket.User);
            await database.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        string issuer = Uri.EscapeDataString("PortKill Administration");
        string account = Uri.EscapeDataString(ticket.User.UserName!);
        return new(key!, $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&digits=6&period=30");
    }

    public async Task<EnrollmentConfirmation> ConfirmEnrollmentAsync(ClaimsPrincipal principal, string? code, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.EnrollmentRequired, SessionPhase.RecoveryAcknowledgementRequired);
        await ConsumeTotpAsync(ticket.User, code, transaction, token);
        AdminBootstrap.Ensure(await users.SetTwoFactorEnabledAsync(ticket.User, true));
        string[] recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(ticket.User, 10))?.ToArray()
            ?? throw new AdminFailure("mfa_enrollment_failed", 409, "恢复码生成失败，请重新验证。");
        ticket.User.RecoveryCodesAcknowledged = false;
        AdminBootstrap.Ensure(await users.UpdateAsync(ticket.User));
        ticket.Session.Phase = SessionPhase.RecoveryAcknowledgementRequired;
        ticket.Session.StrongAuthenticationUtcMs = Now;
        ticket.Session.SecurityStamp = await users.GetSecurityStampAsync(ticket.User);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(ticket, recovery);
    }

    public async Task<SessionTicket> AcknowledgeRecoveryAsync(ClaimsPrincipal principal, bool acknowledged, CancellationToken token)
    {
        if (!acknowledged) throw new AdminFailure("recovery_acknowledgement_required", 400, "请先保存并确认恢复码。");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.RecoveryAcknowledgementRequired);
        if (!ticket.User.TwoFactorEnabled || ticket.Session.StrongAuthenticationUtcMs == null)
            throw new AdminFailure("mfa_phase_invalid", 403, "请先完成验证器绑定。");
        ticket.User.RecoveryCodesAcknowledged = true;
        AdminBootstrap.Ensure(await users.UpdateAsync(ticket.User));
        var full = await UpgradeAsync(ticket, "totp", ticket.Session.StrongAuthenticationUtcMs, token);
        await transaction.CommitAsync(token);
        return full;
    }

    public async Task<SessionTicket> VerifyAsync(ClaimsPrincipal principal, string? code, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.MfaRequired);
        await ConsumeTotpAsync(ticket.User, code, transaction, token);
        var full = await UpgradeAsync(ticket, "totp", Now, token);
        await transaction.CommitAsync(token);
        return full;
    }

    public async Task<SessionTicket> RecoverAsync(ClaimsPrincipal principal, string? code, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.MfaRequired);
        if (await users.IsLockedOutAsync(ticket.User) || code is not { Length: > 0 and <= 128 }
            || !(await users.RedeemTwoFactorRecoveryCodeAsync(ticket.User, code)).Succeeded)
            await RejectAsync(ticket.User, transaction, token);
        AdminBootstrap.Ensure(await users.ResetAccessFailedCountAsync(ticket.User));
        var full = await UpgradeAsync(ticket, "recovery", null, token);
        await transaction.CommitAsync(token);
        return full;
    }

    public async Task<SessionTicket> ReauthenticateAsync(ClaimsPrincipal principal, string? password, string? code, CancellationToken token)
    {
        var before = await sessions.ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        if (before.Session.Phase != SessionPhase.Full || password is not { Length: > 0 and <= 256 }
            || !await users.CheckPasswordAsync(before.User, password))
            throw new AdminFailure("invalid_credentials", 401, "账号或凭据无效。");
        string? verifiedHash = before.User.PasswordHash;
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var ticket = await RequireAsync(principal, token, SessionPhase.Full);
        if (ticket.User.PasswordHash != verifiedHash) throw new AdminFailure("invalid_credentials", 401, "账号或凭据无效。");
        await ConsumeTotpAsync(ticket.User, code, transaction, token);
        ticket.Session.StrongAuthenticationUtcMs = Now;
        ticket.Session.AuthenticationMethod = "totp";
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return ticket;
    }

    private async Task ConsumeTotpAsync(AdminUser user, string? code, IDbContextTransaction transaction, CancellationToken token)
    {
        if (await users.IsLockedOutAsync(user) || code is not { Length: 6 } || !code.All(char.IsAsciiDigit)
            || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
            await RejectAsync(user, transaction, token);
        string key = await users.GetAuthenticatorKeyAsync(user) ?? "";
        // Identity validates TOTP; this ledger consumes a code atomically across its allowed time window.
        string fingerprint = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(code!)));
        if (await database.TotpConsumptions.AnyAsync(x => x.UserId == user.Id && x.Fingerprint == fingerprint && x.ExpiresUtcMs > Now, token))
            throw new AdminFailure("mfa_replayed", 401, "验证码已使用，请等待新的验证码。");
        await database.TotpConsumptions.Where(x => x.UserId == user.Id && x.Fingerprint == fingerprint && x.ExpiresUtcMs <= Now).ExecuteDeleteAsync(token);
        database.TotpConsumptions.Add(new TotpConsumption { UserId = user.Id, Fingerprint = fingerprint, ExpiresUtcMs = Now + 180_000 });
        AdminBootstrap.Ensure(await users.ResetAccessFailedCountAsync(user));
        await database.SaveChangesAsync(token);
    }

    private async Task RejectAsync(AdminUser user, IDbContextTransaction transaction, CancellationToken token)
    {
        if (!await users.IsLockedOutAsync(user)) AdminBootstrap.Ensure(await users.AccessFailedAsync(user));
        await transaction.CommitAsync(token);
        throw new AdminFailure("mfa_invalid", 401, "验证码或恢复码无效。");
    }

    private async Task<SessionTicket> UpgradeAsync(SessionTicket pending, string method, long? strongAt, CancellationToken token)
    {
        pending.Session.RevokedUtcMs = Now;
        await database.SaveChangesAsync(token);
        var full = await sessions.CreateAsync(pending.User, SessionPhase.Full, method, token);
        full.Session.StrongAuthenticationUtcMs = strongAt;
        await database.SaveChangesAsync(token);
        return full;
    }
}
