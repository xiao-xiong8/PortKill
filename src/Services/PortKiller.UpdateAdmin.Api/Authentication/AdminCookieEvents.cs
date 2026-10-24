using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed class AdminCookieEvents(AdminSessionService sessions) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var ticket = context.Principal == null ? null : await sessions.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);
        if (ticket == null)
        { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(AdminIdentity.Scheme); return; }
        context.ReplacePrincipal(await sessions.PrincipalAsync(ticket));
    }
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
        => ApiProblems.WriteAsync(context.HttpContext, 401, AdminErrorCodes.AuthenticationRequired, "需要有效的管理员会话。");
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
        => ApiProblems.WriteAsync(context.HttpContext, 403, AdminErrorCodes.PermissionDenied, "当前会话未完成认证或无权执行此操作。");
}
