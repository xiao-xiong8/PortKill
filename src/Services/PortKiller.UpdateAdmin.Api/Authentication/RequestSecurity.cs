using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed record CsrfResponse(string RequestToken);
public static class RequestSecurity
{
    public const string CsrfHeader = "X-PortKill-CSRF";
    public static void AddRequestSecurity(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.Name = "__Host-PortKill.Admin.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback); options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/v1")
                    ? Window(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 240)
                    : RateLimitPartition.GetNoLimiter<string>("health"));
            options.AddPolicy("login", context => Window(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 20));
            // IP remains part of the key even when an attacker keeps obtaining new pending sessions.
            options.AddPolicy("mfa", context => Window(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 20));
            options.AddPolicy("security-change", context => Window(context.User.FindFirst(AdminIdentity.SessionClaim)?.Value ?? "anonymous", 10));
            options.OnRejected = async (rejection, _) =>
            {
                rejection.HttpContext.Response.Headers.RetryAfter = "60";
                await ApiProblems.WriteAsync(rejection.HttpContext, 429, "rate_limited", "请求过于频繁，请稍后重试。");
            };
        });
    }

    private static RateLimitPartition<string> Window(string key, int permits) => RateLimitPartition.GetFixedWindowLimiter(key,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    public static void UseCsrfValidation(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1")
                && context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
            {
                var csrf = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await csrf.IsRequestValidAsync(context))
                { await ApiProblems.WriteAsync(context, 400, "csrf_invalid", "请求验证失效，请刷新页面后重试。"); return; }
            }
            await next(context);
        });
        app.MapGet("/api/v1/auth/csrf", (IAntiforgery csrf, HttpContext context) =>
            new CsrfResponse(csrf.GetAndStoreTokens(context).RequestToken!)).AllowAnonymous().WithName("GetCsrfToken");
    }
}
