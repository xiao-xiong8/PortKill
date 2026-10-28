using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.ClientInstallations;

public static class ClientInstallationEndpoints
{
    public static void MapClientInstallationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/client-installations/register", async (ClientInstallationRegisterRequest request,
            ClientInstallationService service, CancellationToken token) =>
        {
            var response = await service.RegisterAsync(request, token);
            return TypedResults.Created("/api/v1/client-installations/report", response);
        }).AllowAnonymous().RequireRateLimiting("client-register").WithName("RegisterClientInstallation")
            .Produces<ClientInstallationRegisterResponse>(201).ProducesProblem(400).ProducesProblem(409).ProducesProblem(429);

        app.MapPost("/api/v1/client-installations/report", async (ClientInstallationReportRequest request,
            ClientInstallationService service, HttpRequest http, CancellationToken token) =>
        {
            await service.ReportAsync(http.Headers.Authorization, request, token);
            return TypedResults.NoContent();
        }).AllowAnonymous().RequireRateLimiting("client-report").WithName("ReportClientInstallation")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(429);

        app.MapGet("/api/v1/client-installations", async (ClientInstallationService service, int page = 1,
            int pageSize = 20, string? version = null, string? result = null, string? machineName = null,
            long? reportedBeforeUtcMs = null, bool includeRevoked = false, long? reportedAfterUtcMs = null,
            bool? revoked = null, CancellationToken token = default) =>
            await service.ListAsync(new(page, pageSize, version, result, machineName, reportedBeforeUtcMs, includeRevoked, reportedAfterUtcMs, revoked), token))
            .RequireAuthorization(AdminPermissions.Read).WithName("ListClientInstallations")
            .Produces<PageResponse<ClientInstallationResponse>>(200).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        app.MapPost("/api/v1/client-installations/{id}/revoke", async (string id, ClientInstallationService service,
            HttpContext context, CancellationToken token) =>
        {
            await service.RevokeAsync(context.User, id, token);
            return TypedResults.NoContent();
        }).RequireAuthorization(AdminPermissions.Accounts).RequireRateLimiting("security-change")
            .WithName("RevokeClientInstallation").Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }
}
