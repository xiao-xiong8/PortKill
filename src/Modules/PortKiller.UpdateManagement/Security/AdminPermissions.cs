using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public static class AdminPermissions
{
    public const string Claim = "admin_permission";
    public const string Read = "release.read", Upload = "release.upload", Validate = "release.validate",
        Publish = "release.publish", Audit = "audit.read", Accounts = "accounts.manage", Recent = "recent-strong-authentication";
    public static readonly string[] All = [Read, Upload, Validate, Publish, Audit, Accounts];
    public static string[] ForRoles(IEnumerable<string> roles) => roles.SelectMany(role => role switch
    {
        "Administrator" => All,
        "Viewer" => [Read], "Uploader" => [Read, Upload, Validate], "Publisher" => [Read, Publish], _ => Array.Empty<string>()
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static void AddPolicies(AuthorizationBuilder builder)
    {
        foreach (string permission in All)
            builder.AddPolicy(permission, policy =>
            {
                policy.RequireAuthenticatedUser().RequireClaim(AdminIdentity.PhaseClaim, SessionPhase.Full.ToString())
                    .RequireClaim(Claim, permission);
                if (permission is Publish or Accounts) policy.AddRequirements(new RecentStrongRequirement());
            });
        builder.AddPolicy(Recent, policy => policy.RequireAuthenticatedUser()
            .RequireClaim(AdminIdentity.PhaseClaim, SessionPhase.Full.ToString()).AddRequirements(new RecentStrongRequirement()));
        builder.Services.AddSingleton<IAuthorizationHandler, RecentStrongHandler>();
    }
}

public sealed class RecentStrongRequirement : IAuthorizationRequirement;
public sealed class RecentStrongHandler(TimeProvider clock, AdminOptions options) : AuthorizationHandler<RecentStrongRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RecentStrongRequirement requirement)
    {
        if (long.TryParse(context.User.FindFirstValue("strong_auth_at"), out long at))
        {
            long age = clock.GetUtcNow().ToUnixTimeMilliseconds() - at;
            if (age >= 0 && age < TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds)
                context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
