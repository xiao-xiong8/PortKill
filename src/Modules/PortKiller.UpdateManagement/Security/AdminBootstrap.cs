using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class AdminBootstrap(UpdateAdminDbContext database, UserManager<AdminUser> users, RoleManager<IdentityRole> roles, TimeProvider clock, AdminAuditService audit)
{
    public async Task<string> CreateAsync(string userName, string password, CancellationToken token = default)
    {
        if (!AdminIdentity.ValidUserName(userName) || password.Length > 256)
            throw new AdminFailure("invalid_request", 400, "账号或密码格式不符合要求。");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        if (await database.Users.AnyAsync(token)) throw new AdminFailure("bootstrap_exists", 409, "管理员初始化已经完成。");
        foreach (string name in AdminIdentity.Roles)
            if (!await roles.RoleExistsAsync(name)) Ensure(await roles.CreateAsync(new IdentityRole(name)));
        var user = new AdminUser { UserName = userName, LockoutEnabled = true, CreatedUtcMs = clock.GetUtcNow().ToUnixTimeMilliseconds() };
        Ensure(await users.CreateAsync(user, password));
        Ensure(await users.AddToRoleAsync(user, "Administrator"));
        audit.Stage("account.bootstrap", user.Id, actorUserId: user.Id);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return user.Id;
    }

    internal static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new AdminFailure("account_validation_failed", 400, "账号或凭据未通过验证，请检查账号格式和密码强度。");
    }
}
