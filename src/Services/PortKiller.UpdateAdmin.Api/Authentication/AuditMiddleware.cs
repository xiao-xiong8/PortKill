using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IServiceScopeFactory scopes)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1")) { await next(context); return; }
        if (RequestSecurity.IsPublicClientRequest(context))
        {
            await next(context);
            if (context.Response.StatusCode >= 400)
                logger.LogWarning("Client registration/report rejected; status {Status}", context.Response.StatusCode);
            return;
        }
        string operation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "UnknownRoute";
        // Record before headers leave the server, including responses produced by the exception handler.
        context.Response.OnStarting(async () =>
        {
            int status = context.Response.StatusCode;
            if (operation != "GetCsrfToken" && (context.Request.Method is not ("GET" or "HEAD") || status >= 400))
                try
                {
                    // Use a new scope: failed business transactions may have rolled back tracked changes.
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AdminAuditService>().RecordRequestAsync(operation,
                        context.Request.RouteValues["id"]?.ToString(), status,
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier), CancellationToken.None);
                }
                catch (Exception error)
                { logger.LogError("Audit request record unavailable; status {Status}; failure type {FailureType}", status, error.GetType().Name); }
        });
        await next(context);
    }
}
