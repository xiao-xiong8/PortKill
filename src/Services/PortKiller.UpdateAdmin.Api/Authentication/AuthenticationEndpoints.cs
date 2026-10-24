using Microsoft.AspNetCore.Authentication;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed record LoginRequest(string UserName, string Password);
public sealed record AuthenticationResponse(string Next, SessionPhase Phase);

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
    }

    internal static async Task SignInAsync(HttpContext context, AdminSessionService sessions, SessionTicket ticket)
        => await context.SignInAsync(AdminIdentity.Scheme, await sessions.PrincipalAsync(ticket), new AuthenticationProperties
        {
            IsPersistent = false, AllowRefresh = false,
            IssuedUtc = DateTimeOffset.FromUnixTimeMilliseconds(ticket.Session.CreatedUtcMs),
            ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(ticket.Session.AbsoluteExpiresUtcMs)
        });
}
