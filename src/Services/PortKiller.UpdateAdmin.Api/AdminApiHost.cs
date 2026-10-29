using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateAdmin.Api.Authentication;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateAdmin.Api.Uploads;
using PortKiller.UpdateAdmin.Api.Releases;
using PortKiller.UpdateAdmin.Api.ClientInstallations;
using PortKiller.UpdateAdmin.Api.Monitoring;
using PortKiller.UpdateManagement.Monitoring;

namespace PortKiller.UpdateAdmin.Api;

public static class AdminApiHost
{
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
        => CreateCore(args, configure, requireDatabase: true);

    internal static WebApplication CreateForContractExport(string[] args) => CreateCore(args, null, requireDatabase: false);

    private static WebApplication CreateCore(string[] args, Action<WebApplicationBuilder>? configure, bool requireDatabase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);
        var administration = AdminHostConfiguration.Load(builder.Configuration, args);
        builder.Services.AddSingleton(administration);
        var updateTool = new UpdateToolOptions();
        builder.Configuration.GetSection("UpdateTool").Bind(updateTool);
        builder.Services.TryAddSingleton(updateTool);
        builder.Services.AddSingleton<UpdateToolClient>();
        builder.Services.TryAddSingleton<IPublishedReleaseReader, PublishedReleaseReader>();
        builder.Services.AddScoped<AdminHealthService>();
        builder.Services.AddScoped<MonitoringService>();
        builder.Services.AddAdminIdentity(administration);
        builder.AddRequestSecurity();
        builder.Services.AddScoped<AdminCookieEvents>();
        builder.Services.AddSingleton<JobStreamConnections>();
        builder.Services.AddSingleton<JobEventStream>();
        if (requireDatabase) builder.Services.AddHostedService<AdminRuntimeService>();
        builder.Services.AddHostedService<SessionMaintenanceService>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5081");
        string urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5081";
        bool dockerBinding = string.Equals(Environment.GetEnvironmentVariable("PORTKILL_DOCKER_MODE"), "true", StringComparison.OrdinalIgnoreCase);
        if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(address =>
            !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || (!uri.IsLoopback && !(dockerBinding && uri.Host is "0.0.0.0" or "::"))))
            throw new InvalidOperationException("Management API must bind to loopback; expose it through the configured reverse proxy.");
        builder.Services.AddAuthentication("admin")
            .AddCookie("admin", options =>
            {
                options.Cookie.Name = "__Host-PortKill.Admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.EventsType = typeof(AdminCookieEvents);
                options.ExpireTimeSpan = TimeSpan.FromDays(administration.PersistentCookieDays);
                options.SlidingExpiration = true;
            });
        var fullSessionPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .RequireClaim(AdminIdentity.PhaseClaim, SessionPhase.Full.ToString()).Build();
        var authorization = builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(fullSessionPolicy).SetDefaultPolicy(fullSessionPolicy)
            .AddPolicy(AdminIdentity.PendingPolicy, policy => policy.RequireAuthenticatedUser());
        AdminPermissions.AddPolicies(authorization);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AdminExceptionHandler>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
        builder.Services.AddOpenApi("v1", options => options.AddSchemaTransformer((schema, context, _) =>
        {
            // OpenAPI 3.0 cannot represent the nullable numeric/string union produced by the web JSON defaults.
            // Monitoring intentionally uses strict numeric fields, with values bounded to JavaScript's safe range.
            if (context.JsonTypeInfo.Type.Namespace == typeof(MonitoringResponse).Namespace
                && context.JsonTypeInfo.Type.Name.StartsWith("Monitoring", StringComparison.Ordinal))
                foreach (var property in context.JsonTypeInfo.Properties)
                {
                    var underlying = Nullable.GetUnderlyingType(property.PropertyType);
                    var type = underlying ?? property.PropertyType;
                    if (schema.Properties?.TryGetValue(property.Name, out var item) != true || item is not Microsoft.OpenApi.OpenApiSchema numeric) continue;
                    if (type != typeof(long) && type != typeof(int) && type != typeof(double)) continue;
                    numeric.Type = (type == typeof(double) ? Microsoft.OpenApi.JsonSchemaType.Number : Microsoft.OpenApi.JsonSchemaType.Integer)
                        | (underlying == null ? 0 : Microsoft.OpenApi.JsonSchemaType.Null);
                    numeric.Pattern = null;
                }
            return Task.CompletedTask;
        }));
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (RequestSecurity.IsPublicClientRequest(context))
            {
                const int limit = 4096;
                if (context.Request.ContentLength > limit)
                { context.Response.StatusCode = 413; return; }
                var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
                // Bound chunked requests too, including hosts without Kestrel's size feature.
                if (context.Request.ContentLength == null)
                {
                    byte[] buffer = new byte[limit + 1];
                    int length = 0, read;
                    while (length < buffer.Length && (read = await context.Request.Body.ReadAsync(
                        buffer.AsMemory(length), context.RequestAborted)) > 0) length += read;
                    if (length > limit) { context.Response.StatusCode = 413; return; }
                    using var body = new MemoryStream(buffer, 0, length, writable: false);
                    var original = context.Request.Body;
                    context.Request.Body = body;
                    try { await next(context); }
                    finally { context.Request.Body = original; }
                    return;
                }
            }
            await next(context);
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1")) context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseMiddleware<AuditMiddleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseCsrfValidation();
        app.MapGet("/health/live", () => new LiveHealthResponse("ok", "admin-api")).AllowAnonymous().WithName("GetLiveHealth");
        app.MapGet("/health/ready", async (AdminHealthService health, CancellationToken token)
            => await health.GetAsync(token)).AllowAnonymous().WithName("GetReadyHealth").Produces<AdminHealthResponse>();
        app.MapGet("/api/v1/system/status", () => new SystemStatusResponse(
            AdminApplication.Name, AdminApplication.Version, AdminContract.Version))
            .RequireAuthorization(AdminPermissions.Read).WithName("GetSystemStatus").ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/system/capabilities", () => AdminContract.Capabilities)
            .RequireAuthorization(AdminPermissions.Read).WithName("GetCapabilities").ProducesProblem(401).ProducesProblem(403);
        app.MapAdminAuthentication();
        app.MapMonitoring();
        app.MapAdminAccounts();
        app.MapUploadEndpoints();
        app.MapPublishEndpoints();
        app.MapJobEventEndpoints();
        app.MapClientInstallationEndpoints();
        app.MapGet("/api/v1/validation-tasks/{id}", async (string id, ValidationTaskQueue queue, HttpContext context)
            => await queue.GetAsync(context.User, id, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Read).WithName("GetValidationTask").ProducesProblem(404);
        app.MapGet("/api/v1/audit", async (AdminAuditService audit, HttpContext context, int page = 1, int pageSize = 20, string? action = null)
            => await audit.ListAsync(page, pageSize, action, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Audit).WithName("ListAuditEvents").ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        return app;
    }
}
