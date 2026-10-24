using Microsoft.AspNetCore.Http.Features;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Uploads;

public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this WebApplication app)
    {
        var uploads = app.MapGroup("/api/v1/uploads");
        uploads.MapPost("/", async (CreateUploadRequest request, UploadManagementService service, HttpContext context) =>
        {
            var created = await service.CreateAsync(context.User, request, context.RequestAborted);
            return Results.Created($"/api/v1/uploads/{created.Id}", created);
        })
            .RequireAuthorization(AdminPermissions.Upload).WithName("CreateUpload").ProducesProblem(400).ProducesProblem(429);
        uploads.MapGet("/{id}", async (string id, UploadManagementService service, HttpContext context)
            => await service.GetAsync(context.User, id, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Read).WithName("GetUpload").ProducesProblem(401).ProducesProblem(404);
        uploads.MapPut("/{id}/manifest", async (string id, UploadManagementService service, HttpContext context)
            => await ReceiveAsync(id, UploadPart.Manifest, service, context, 65_536))
            .RequireAuthorization(AdminPermissions.Upload).WithName("UploadManifest").ProducesProblem(400).ProducesProblem(404).ProducesProblem(413);
        uploads.MapPut("/{id}/package", async (string id, UploadManagementService service, HttpContext context)
            => await ReceiveAsync(id, UploadPart.Package, service, context, serviceMaximum: true))
            .RequireAuthorization(AdminPermissions.Upload).WithName("UploadPackage").ProducesProblem(400).ProducesProblem(404).ProducesProblem(413);
        uploads.MapPost("/{id}/complete", async (string id, UploadManagementService service, HttpContext context)
            => await service.CompleteAsync(context.User, id, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Upload).WithName("CompleteUpload").ProducesProblem(404).ProducesProblem(409);
        uploads.MapDelete("/{id}", async (string id, UploadManagementService service, HttpContext context) =>
        { await service.CancelAsync(context.User, id, context.RequestAborted); return Results.NoContent(); })
            .RequireAuthorization(AdminPermissions.Upload).WithName("CancelUpload").ProducesProblem(404).ProducesProblem(409);
    }

    private static async Task<IResult> ReceiveAsync(string id, UploadPart part, UploadManagementService service,
        HttpContext context, long limit = 0, bool serviceMaximum = false)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new PortKiller.UpdateManagement.Security.AdminFailure("content_type_invalid", 415, "上传内容必须使用 application/octet-stream。");
        var upload = await service.GetAsync(context.User, id, context.RequestAborted);
        long expectedLimit = serviceMaximum ? upload.PackageExpectedBytes : limit;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = expectedLimit;
        return Results.Ok(await service.WritePartAsync(context.User, id, part, context.Request.Body,
            context.Request.ContentLength, context.RequestAborted));
    }
}
