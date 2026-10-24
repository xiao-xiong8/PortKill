using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public static class AdminIdentity
{
    public static readonly string[] Roles = ["Viewer", "Uploader", "Publisher", "Administrator"];
    public const string Scheme = "admin";
    public const string SessionClaim = "admin_session";
    public const string PhaseClaim = "admin_phase";
    public const string PendingPolicy = "admin-pending";

    public static IServiceCollection AddAdminIdentity(this IServiceCollection services, AdminOptions options)
    {
        services.AddAdminDatabase(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        AdminRuntimeLease.EnsureDataDirectory(options.DataRoot);
        if (!OperatingSystem.IsWindows())
        {
            var permissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(options.DataProtectionPath, permissions);
            File.SetUnixFileMode(options.DataProtectionPath, permissions);
        }
        services.AddDataProtection().SetApplicationName("PortKill.UpdateAdmin")
            .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionPath));
        services.AddIdentityCore<AdminUser>(identity =>
        {
            identity.Password.RequiredLength = 14;
            identity.Password.RequiredUniqueChars = 5;
            identity.User.AllowedUserNameCharacters = string.Empty;
            identity.Lockout.MaxFailedAccessAttempts = 5;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<UpdateAdminDbContext>()
            .AddSignInManager().AddDefaultTokenProviders();
        services.Configure<PasswordHasherOptions>(hasher => hasher.IterationCount = 210_000);
        services.AddSingleton<DummyPasswordVerifier>();
        services.AddScoped<AdminBootstrap>();
        services.AddScoped<AdminSessionService>();
        services.AddScoped<PasswordAuthenticationService>();
        services.AddScoped<AdminMfaService>();
        services.AddScoped<AdminAccountService>();
        services.AddScoped<AdminAuditService>();
        services.AddScoped<AdminBackupService>();
        services.AddScoped<UploadManagementService>();
        services.AddScoped<PublishManagementService>();
        services.AddAdminWorkerServices();
        return services;
    }

    public static bool ValidUserName(string? name) => name is { Length: >= 3 and <= 64 }
        && name.All(character => char.IsLetterOrDigit(character) || "._@+-".Contains(character));
}

public static class AdminWorkerServices
{
    public static IServiceCollection AddAdminWorkerServices(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<ValidationTaskQueue>();
        services.AddScoped<ValidationTaskExecutor>();
        services.AddSingleton<ValidationArchiveService>();
        services.AddScoped<UploadCleanupService>();
        return services;
    }
}

public sealed class DummyPasswordVerifier(Microsoft.Extensions.Options.IOptions<PasswordHasherOptions> options)
{
    private readonly PasswordHasher<AdminUser> _hasher = new(options);
    private readonly string _hash = new PasswordHasher<AdminUser>(options).HashPassword(new AdminUser(), Guid.NewGuid().ToString("N"));
    public void Check(string password) => _hasher.VerifyHashedPassword(new AdminUser(), _hash, password);
}
