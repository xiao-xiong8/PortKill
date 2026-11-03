using Microsoft.AspNetCore.Http.Features;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.ServerUpdates;

namespace PortKiller.UpdateAdmin.Api.ServerUpdates;

public static class ServerUpdateEndpoints
{
    public static void MapServerUpdates(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/server-updates");
        group.MapGet("/", (ServerUpdateService service, CancellationToken token) => service.SummaryAsync(token))
            .RequireAuthorization(AdminPermissions.ServerRead).WithName("GetServerUpdates");
        group.MapPost("/packages", async (ServerUpdateService service, ServerUpdateOptions options, HttpContext context) =>
        {
            service.RequireEnabled();
            if (context.Request.ContentType != "application/octet-stream") throw new AdminFailure("content_type_invalid", 415, "请上传一个 ZIP 文件。");
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = options.MaximumPackageBytes;
            return await service.UploadAsync(context.Request.Body, context.Request.ContentLength, context.RequestAborted);
        }).RequireAuthorization(AdminPermissions.ServerUpdate).WithName("UploadServerPackage");
        group.MapDelete("/packages/{id}", (string id, ServerUpdateService service) => { service.DeletePackage(id); return Results.NoContent(); })
            .RequireAuthorization(AdminPermissions.ServerUpdate).WithName("DeleteServerPackage");
        group.MapPost("/packages/{id}/apply", (string id, ServerUpdateService service, HttpContext context) => service.StartAsync(id, context.User))
            .RequireAuthorization(AdminPermissions.ServerUpdate).WithName("ApplyServerUpdate");
        group.MapGet("/tasks/{id}", (string id, ServerUpdateService service, ServerUpdateStore store) => { service.RequireEnabled(); return store.Task(id); })
            .RequireAuthorization(AdminPermissions.ServerRead).WithName("GetServerUpdateTask");
        group.MapGet("/tasks/{id}/logs", (string id, ServerUpdateService service, ServerUpdateStore store, long after = 0) =>
            { service.RequireEnabled(); return store.Logs(id, Math.Max(0, after)); })
            .RequireAuthorization(AdminPermissions.ServerRead).WithName("GetServerUpdateLogs");
        group.MapPost("/tasks/{id}/rollback", (string id, ServerUpdateService service, ServerUpdateStore store, HttpContext context)
            => service.StartAsync(store.Task(id).PackageId, context.User, id))
            .RequireAuthorization(AdminPermissions.ServerUpdate).WithName("RollbackServerUpdate");
    }
}
