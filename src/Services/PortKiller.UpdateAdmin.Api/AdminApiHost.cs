using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Serialization;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Configuration;
using Microsoft.AspNetCore.DataProtection;
using PortKiller.UpdateAdmin.Api.Authentication;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;
using Microsoft.AspNetCore.Diagnostics;

namespace PortKiller.UpdateAdmin.Api;

public static class AdminApiHost
{
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);
        var administration = AdminHostConfiguration.Load(builder.Configuration, args);
        builder.Services.AddSingleton(administration);
        builder.Services.AddAdminIdentity(administration);
        builder.Services.AddScoped<AdminCookieEvents>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://127.0.0.1:5081");
        string urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5081";
        if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(address =>
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || uri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Management API must bind to loopback; expose it through the configured reverse proxy.");
        builder.Services.AddDataProtection().SetApplicationName("PortKill.UpdateAdmin")
            .PersistKeysToFileSystem(new DirectoryInfo(administration.DataProtectionPath));
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
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(fullSessionPolicy).SetDefaultPolicy(fullSessionPolicy)
            .AddPolicy(AdminIdentity.PendingPolicy, policy => policy.RequireAuthenticatedUser());
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
        builder.Services.AddOpenApi("v1");
        var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            if (error is AdminFailure failure)
                await ApiProblems.WriteAsync(context, failure.Status, failure.Code, failure.Message);
            else if (error is BadHttpRequestException)
                await ApiProblems.WriteAsync(context, 400, AdminErrorCodes.InvalidRequest, "请求格式无效。");
            else
                await ApiProblems.WriteAsync(context, 500, AdminErrorCodes.InternalError, "请求暂时无法完成，请稍后重试。");
        }));
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1")) context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.MapGet("/health/live", () => new LiveHealthResponse("ok", "admin-api")).AllowAnonymous().WithName("GetLiveHealth");
        app.MapGet("/api/v1/system/status", () => new SystemStatusResponse(
            AdminApplication.Name, AdminApplication.Version, AdminContract.Version))
            .WithName("GetSystemStatus").ProducesProblem(401).ProducesProblem(403);
        app.MapGet("/api/v1/system/capabilities", () => AdminContract.Capabilities)
            .WithName("GetCapabilities").ProducesProblem(401).ProducesProblem(403);
        app.MapAdminAuthentication();
        return app;
    }
}
