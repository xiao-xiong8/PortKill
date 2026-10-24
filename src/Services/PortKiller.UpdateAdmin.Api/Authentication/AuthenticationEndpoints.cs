using Microsoft.AspNetCore.Authentication;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed record LoginRequest(string UserName, string Password);
public sealed record AuthenticationResponse(string Next, SessionPhase Phase);
public sealed record AuthenticatorCodeRequest(string Code);
public sealed record RecoveryCodeRequest(string Code);
public sealed record RecoveryAcknowledgementRequest(bool Acknowledged);
public sealed record ReauthenticationRequest(string Password, string Code);
public sealed record RecoveryCodesResponse(string[] RecoveryCodes);

public static class AuthenticationEndpoints
{
    public static void MapAdminAuthentication(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/login", async (LoginRequest request, PasswordAuthenticationService authentication,
            AdminSessionService sessions, HttpContext context) =>
        {
            var ticket = await authentication.LoginAsync(request.UserName, request.Password, context.RequestAborted);
            await SignInAsync(context, sessions, ticket);
            return new AuthenticationResponse(ticket.Session.Phase switch
            { SessionPhase.EnrollmentRequired => "enrollMfa", SessionPhase.RecoveryAcknowledgementRequired => "acknowledgeRecovery", _ => "verifyMfa" }, ticket.Session.Phase);
        }).AllowAnonymous().WithName("Login").ProducesProblem(400).ProducesProblem(401);

        var pending = app.MapGroup("/api/v1/auth/mfa").RequireAuthorization(AdminIdentity.PendingPolicy);
        pending.MapPost("/enrollment/start", async (AdminMfaService mfa, HttpContext context)
            => await mfa.StartEnrollmentAsync(context.User, context.RequestAborted))
            .WithName("StartMfaEnrollment").ProducesProblem(401).ProducesProblem(403);
        pending.MapPost("/enrollment/confirm", async (AuthenticatorCodeRequest request, AdminMfaService mfa,
            AdminSessionService sessions, HttpContext context) =>
        {
            var result = await mfa.ConfirmEnrollmentAsync(context.User, request.Code, context.RequestAborted);
            await SignInAsync(context, sessions, result.Ticket);
            return new RecoveryCodesResponse(result.RecoveryCodes);
        }).WithName("ConfirmMfaEnrollment").ProducesProblem(401).ProducesProblem(403);
        pending.MapPost("/enrollment/acknowledge", async (RecoveryAcknowledgementRequest request, AdminMfaService mfa,
            AdminSessionService sessions, HttpContext context) =>
        {
            var ticket = await mfa.AcknowledgeRecoveryAsync(context.User, request.Acknowledged, context.RequestAborted);
            await SignInAsync(context, sessions, ticket);
            return new AuthenticationResponse("complete", ticket.Session.Phase);
        }).WithName("AcknowledgeRecoveryCodes").ProducesProblem(400).ProducesProblem(401);
        pending.MapPost("/verify", async (AuthenticatorCodeRequest request, AdminMfaService mfa,
            AdminSessionService sessions, HttpContext context) =>
        {
            var ticket = await mfa.VerifyAsync(context.User, request.Code, context.RequestAborted);
            await SignInAsync(context, sessions, ticket);
            return new AuthenticationResponse("complete", ticket.Session.Phase);
        }).WithName("VerifyMfa").ProducesProblem(401).ProducesProblem(403);
        pending.MapPost("/recovery", async (RecoveryCodeRequest request, AdminMfaService mfa,
            AdminSessionService sessions, HttpContext context) =>
        {
            var ticket = await mfa.RecoverAsync(context.User, request.Code, context.RequestAborted);
            await SignInAsync(context, sessions, ticket);
            return new AuthenticationResponse("complete", ticket.Session.Phase);
        }).WithName("UseRecoveryCode").ProducesProblem(401).ProducesProblem(403);
        app.MapPost("/api/v1/auth/reauthenticate", async (ReauthenticationRequest request, AdminMfaService mfa,
            AdminSessionService sessions, HttpContext context) =>
        {
            var ticket = await mfa.ReauthenticateAsync(context.User, request.Password, request.Code, context.RequestAborted);
            await SignInAsync(context, sessions, ticket);
            return new AuthenticationResponse("complete", ticket.Session.Phase);
        }).WithName("Reauthenticate").ProducesProblem(401).ProducesProblem(403);
    }

    internal static async Task SignInAsync(HttpContext context, AdminSessionService sessions, SessionTicket ticket)
        => await context.SignInAsync(AdminIdentity.Scheme, await sessions.PrincipalAsync(ticket), new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = false,
            IssuedUtc = DateTimeOffset.FromUnixTimeMilliseconds(ticket.Session.CreatedUtcMs),
            ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(ticket.Session.AbsoluteExpiresUtcMs)
        });
}
