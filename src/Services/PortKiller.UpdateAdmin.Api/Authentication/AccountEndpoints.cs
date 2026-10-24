using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public static class AccountEndpoints
{
    public static void MapAdminAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/v1/accounts").RequireAuthorization(AdminPermissions.Accounts).RequireRateLimiting("security-change");
        accounts.MapGet("/", async (AdminAccountService service, HttpContext context, int page = 1, int pageSize = 20)
            => await service.ListAsync(page, pageSize, context.RequestAborted)).WithName("ListAccounts");
        accounts.MapPost("/", async (CreateAccountRequest request, AdminAccountService service, HttpContext context)
            => new CreatedAccountResponse(await service.CreateAsync(context.User, request, context.RequestAborted))).WithName("CreateAccount");
        accounts.MapPut("/{id}/roles", async (string id, ChangeRolesRequest request, AdminAccountService service, HttpContext context) =>
        { await service.ChangeRolesAsync(context.User, id, request.Roles, context.RequestAborted); return Results.NoContent(); }).WithName("ChangeAccountRoles");
        accounts.MapPut("/{id}/state", async (string id, AccountStateRequest request, AdminAccountService service, HttpContext context) =>
        { await service.SetDisabledAsync(context.User, id, request.Disabled, context.RequestAborted); return Results.NoContent(); }).WithName("SetAccountState");
        accounts.MapPost("/{id}/reset-mfa", async (string id, AdminAccountService service, HttpContext context) =>
        { await service.ResetMfaAsync(context.User, id, context.RequestAborted); return Results.NoContent(); }).WithName("ResetAccountMfa");
        app.MapPost("/api/v1/auth/password", async (ChangePasswordRequest request, AdminAccountService service, HttpContext context) =>
        {
            await service.ChangePasswordAsync(context.User, request, context.RequestAborted);
            await context.SignOutAsync(AdminIdentity.Scheme); return Results.NoContent();
        }).RequireAuthorization(AdminPermissions.Recent).RequireRateLimiting("security-change").WithName("ChangeOwnPassword");
        app.MapPost("/api/v1/auth/recovery-codes", async (AdminAccountService service, HttpContext context)
            => new RecoveryCodesResponse(await service.RegenerateRecoveryCodesAsync(context.User, context.RequestAborted)))
            .RequireAuthorization(AdminPermissions.Recent).RequireRateLimiting("security-change").WithName("RegenerateRecoveryCodes");
    }
}
