using Microsoft.AspNetCore.Mvc;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Releases;

public static class JobEventEndpoints
{
    public static void MapJobEventEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/v1/jobs/{id}").RequireAuthorization(AdminPermissions.Read);
        jobs.MapGet("", async (string id, JobEventQuery query, HttpContext context)
            => await query.SnapshotAsync(context.User, id, context.RequestAborted))
            .WithName("GetJobSnapshot").ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        jobs.MapGet("/events", async (string id, JobEventQuery query, HttpContext context, string? after = null, int limit = 100)
            => await query.ReadAsync(context.User, id, after, limit, context.RequestAborted))
            .WithName("ReadJobEvents").ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        jobs.MapGet("/events/stream", async (string id, JobEventStream stream, HttpContext context, string? after = null,
            [FromHeader(Name = "Last-Event-ID")] string? lastEventId = null)
            => await stream.WriteAsync(context, id, after, lastEventId))
            .WithName("StreamJobEvents").Produces<string>(200, "text/event-stream")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
