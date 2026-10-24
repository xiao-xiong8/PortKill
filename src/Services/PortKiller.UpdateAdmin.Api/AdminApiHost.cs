using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Serialization;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateAdmin.Api.Authentication;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Persistence;

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
        builder.Services.AddAdminIdentity(administration);
        builder.AddRequestSecurity();
        builder.Services.AddScoped<AdminCookieEvents>();
        if (requireDatabase) builder.Services.AddHostedService<AdminRuntimeService>();
        builder.Services.AddHostedService<SessionMaintenanceService>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5081");
        string urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5081";
        if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(address =>
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Management API must bind to loopback; expose it through the configured reverse proxy.");
        builder.Services.AddAuthentication("admin")
            .AddCookie("admin", options =>
            {
                options.Cookie.Name = "__Host-PortKill.Admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.EventsType = typeof(AdminCookieEvents);
                options.SlidingExpiration = false;
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
        builder.Services.AddOpenApi("v1");
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseExceptionHandler();
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
        app.MapGet("/api/v1/system/status", () => new SystemStatusResponse(
            AdminApplication.Name, AdminApplication.Version, AdminContract.Version))
            .RequireAuthorization(AdminPermissions.Read).WithName("GetSystemStatus").ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/system/capabilities", () => AdminContract.Capabilities)
            .RequireAuthorization(AdminPermissions.Read).WithName("GetCapabilities").ProducesProblem(401).ProducesProblem(403);
        app.MapAdminAuthentication();
        app.MapAdminAccounts();
        app.MapGet("/api/v1/audit", async (AdminAuditService audit, HttpContext context, int page = 1, int pageSize = 20, string? action = null)
            => await audit.ListAsync(page, pageSize, action, context.RequestAborted))
            .RequireAuthorization(AdminPermissions.Audit).WithName("ListAuditEvents").ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        return app;
    }
}
