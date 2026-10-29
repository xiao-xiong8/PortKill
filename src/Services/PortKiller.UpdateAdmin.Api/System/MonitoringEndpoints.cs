using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Monitoring;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Monitoring;

public static class MonitoringEndpoints
{
    public static void MapMonitoring(this WebApplication app) => app.MapGet("/api/v1/system/monitoring",
        async (MonitoringService monitoring, CancellationToken token) => await monitoring.GetAsync(token))
        .RequireAuthorization(AdminPermissions.Read).WithName("GetMonitoring")
        .Produces<MonitoringResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
}
