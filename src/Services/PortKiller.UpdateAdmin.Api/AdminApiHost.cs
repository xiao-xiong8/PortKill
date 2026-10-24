using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Serialization;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Contracts;

namespace PortKiller.UpdateAdmin.Api;

public static class AdminApiHost
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5081");
        builder.Services.AddAuthentication("admin")
            .AddCookie("admin", options =>
            {
                options.Cookie.Name = "__Host-PortKill.Admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Events.OnRedirectToLogin = context =>
                    ApiProblems.WriteAsync(context.HttpContext, 401, AdminErrorCodes.AuthenticationRequired, "需要有效的管理员会话。");
                options.Events.OnRedirectToAccessDenied = context =>
                    ApiProblems.WriteAsync(context.HttpContext, 403, AdminErrorCodes.PermissionDenied, "当前账号无权执行此操作。");
            });
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
        builder.Services.AddOpenApi("v1");
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => new LiveHealthResponse("ok", "admin-api")).AllowAnonymous().WithName("GetLiveHealth");
        app.MapGet("/api/v1/system/status", () => new SystemStatusResponse(
            AdminApplication.Name, AdminApplication.Version, AdminContract.Version))
            .WithName("GetSystemStatus").ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/system/capabilities", () => AdminContract.Capabilities)
            .WithName("GetCapabilities").ProducesProblem(401).ProducesProblem(403);
        return app;
    }
}
