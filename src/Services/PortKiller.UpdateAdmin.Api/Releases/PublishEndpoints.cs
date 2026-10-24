using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Releases;

public static class PublishEndpoints
{
    public static void MapPublishEndpoints(this WebApplication app)
    {
        var releases = app.MapGroup("/api/v1/releases");
        releases.MapPost("/{draftId}/publish/challenge", async (string draftId, PublishManagementService service, HttpContext context) =>
        {
            var challenge = await service.CreateChallengeAsync(context.User, draftId, context.RequestAborted);
            return Results.Ok(challenge);
        })
            .RequireAuthorization(AdminPermissions.Publish).RequireRateLimiting("publish-confirmation").WithName("CreatePublishChallenge")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        releases.MapPost("/{draftId}/publish", async (string draftId, PublishConfirmationRequest request,
            PublishManagementService service, HttpContext context) =>
        {
            string? header = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var job = await service.ConfirmAsync(context.User, draftId, request, header, context.RequestAborted);
            return Results.Accepted($"/api/v1/publish-jobs/{job.Id}", job);
        })
            .RequireAuthorization(AdminPermissions.Publish).RequireRateLimiting("publish-confirmation").WithName("ConfirmPublish")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        app.MapGet("/api/v1/publish-jobs/{id}", async (string id, PublishManagementService service, HttpContext context)
            => await service.GetJobAsync(context.User, id, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Read).WithName("GetPublishJob")
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }
}
