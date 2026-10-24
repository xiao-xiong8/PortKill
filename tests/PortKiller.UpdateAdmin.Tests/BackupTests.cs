using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateAdmin.Api;
using PortKiller.UpdateAdmin.Api.Commands;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class BackupTests
{
    private static async Task<byte[]> DatabaseHashAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await SHA256.HashDataAsync(input);
    }
    [Fact]
    public async Task LiveWalSnapshotRestoresAccountsMetadataAndKeysWhileRevokingOldSessions()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        string backup = Path.Combine(app.Root, "backups", Guid.NewGuid().ToString("N"));
        string draftId;
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>(); string userId = await db.Users.Select(x => x.Id).SingleAsync();
            var upload = new UploadSession { OwnerId = userId }; db.UploadSessions.Add(upload);
            var draft = new ReleaseDraft { OwnerId = userId, UploadId = upload.Id, Version = "1.2.3", CreatedUtcMs = 12345 };
            draftId = draft.Id; db.ReleaseDrafts.Add(draft); await db.SaveChangesAsync();
            await using var writer = new SqliteConnection(AdminDatabase.ConnectionString(scope.ServiceProvider.GetRequiredService<AdminOptions>()));
            await writer.OpenAsync(); using var command = writer.CreateCommand(); command.CommandText = "PRAGMA wal_autocheckpoint=0;"; await command.ExecuteNonQueryAsync();
            Assert.True(new FileInfo(Path.Combine(app.Root, "admin.sqlite-wal")).Length > 0);
            await scope.ServiceProvider.GetRequiredService<AdminBackupService>().BackupAsync(backup);
            draft.Version = "9.9.9"; await db.SaveChangesAsync();
        }
        Assert.True(File.Exists(Path.Combine(backup, "backup.json")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(backup, "auth-keys"), "*.xml"));
        await app.StopAsync();
        using (var scope = app.App.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<AdminBackupService>().RestoreAsync(backup);
            Assert.True(result.RevokedSessions > 0); Assert.True(File.Exists(result.PreviousDatabasePath));
        }
        await app.RestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            Assert.Equal("1.2.3", (await db.ReleaseDrafts.SingleAsync(x => x.Id == draftId)).Version);
            Assert.True(await db.Users.AllAsync(x => x.TwoFactorEnabled && x.RecoveryCodesAcknowledged));
            Assert.False(await db.AdminSessions.AnyAsync(x => x.RevokedUtcMs == null));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "database.restored"));
        }
        await MfaTests.LoginAsync(app.Client);
        (await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 1) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task RuntimeSharedLeasesBlockOfflineMaintenanceAndExclusiveLeaseBlocksRuntime()
    {
        await using var app = await TestApplication.StartAsync();
        var options = app.App.Services.GetRequiredService<AdminOptions>();
        using (AdminRuntimeLease.Shared(options))
            Assert.Equal("administration_in_use", Assert.Throws<AdminFailure>(() => AdminRuntimeLease.Exclusive(options)).Code);
        Assert.Equal(1, await AdminCli.RunAsync(["db", "migrate", "--data-root", app.Root]));
        await app.StopAsync();
        using (AdminRuntimeLease.Exclusive(options))
            Assert.Throws<AdminFailure>(() => AdminRuntimeLease.Shared(options));
        Assert.Equal(0, await AdminCli.RunAsync(["db", "migrate", "--data-root", app.Root]));
    }

    [Fact]
    public async Task RestoreRejectsTamperedSnapshotBeforeChangingCurrentDatabase()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var scope = app.App.Services.CreateScope(); var backupService = scope.ServiceProvider.GetRequiredService<AdminBackupService>();
        string backup = Path.Combine(app.Root, "backup"); await backupService.BackupAsync(backup);
        await app.StopAsync();
        byte[] previous = await DatabaseHashAsync(Path.Combine(app.Root, "admin.sqlite"));
        await File.AppendAllTextAsync(Path.Combine(backup, "admin.sqlite"), "tampered");
        var failure = await Assert.ThrowsAsync<AdminFailure>(() => backupService.RestoreAsync(backup));
        Assert.Equal("backup_invalid", failure.Code);
        Assert.Equal(previous, await DatabaseHashAsync(Path.Combine(app.Root, "admin.sqlite")));
    }

    [Fact]
    public async Task RestoreRejectsTamperedKeyAndMismatchedSchema()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var scope = app.App.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<AdminBackupService>();
        string backup = Path.Combine(app.Root, "backup"); await service.BackupAsync(backup); await app.StopAsync();
        string path = Path.Combine(backup, "backup.json"); var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var keys = manifest["authenticationKeys"]!.AsObject(); string first = keys.First().Key;
        string original = keys[first]!.GetValue<string>(); keys[first] = new string('0', 64); await File.WriteAllTextAsync(path, manifest.ToJsonString());
        Assert.Equal("backup_invalid", (await Assert.ThrowsAsync<AdminFailure>(() => service.RestoreAsync(backup))).Code);
        keys[first] = original; manifest["migrations"] = new JsonArray("unknown-migration"); await File.WriteAllTextAsync(path, manifest.ToJsonString());
        Assert.Equal("backup_schema_mismatch", (await Assert.ThrowsAsync<AdminFailure>(() => service.RestoreAsync(backup))).Code);
    }

    [Fact]
    public async Task ColdRestoreRecoversDataProtectionKeysAndInvalidatesRestoredSessions()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        string protectedValue = app.App.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("backup-test").Protect("restorable-value");
        var manager = app.App.Services.GetRequiredService<IKeyManager>();
        var revokedKey = manager.CreateNewKey(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        manager.RevokeKey(revokedKey.KeyId);
        manager.RevokeAllKeys(DateTimeOffset.Parse("2000-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var scope = app.App.Services.CreateScope(); string backup = Path.Combine(app.Root, "backup");
        await scope.ServiceProvider.GetRequiredService<AdminBackupService>().BackupAsync(backup);
        var target = new AdminOptions { DataRoot = Path.Combine(app.Root, "cold-target") };
        var restored = await new AdminBackupService(target, app.Clock).RestoreAsync(backup);
        Assert.Null(restored.PreviousDatabasePath);
        await using var cold = AdminApiHost.Create(["--Administration:DataRoot", target.DataRoot, "--urls", "https://localhost:0"], builder => builder.WebHost.UseTestServer());
        await cold.StartAsync();
        Assert.Equal("restorable-value", cold.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("backup-test").Unprotect(protectedValue));
        Assert.True(cold.Services.GetRequiredService<IKeyManager>().GetAllKeys().Single(item => item.KeyId == revokedKey.KeyId).IsRevoked);
        using var client = cold.GetTestClient(); client.BaseAddress = new Uri("https://localhost"); client.DefaultRequestHeaders.Add("Cookie", app.DefaultCookieHeader);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        await cold.StopAsync();
        using var connection = new SqliteConnection(AdminDatabase.ConnectionString(target)); SqliteConnection.ClearPool(connection);
    }

    [Fact]
    public async Task SoleAdministratorCanRecoverMfaOnlyWithOfflineCli()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        Assert.Equal(1, await AdminCli.RunAsync(["admin", "reset-mfa", "--user", "operator", "--data-root", app.Root]));
        await app.StopAsync();
        Assert.Equal(0, await AdminCli.RunAsync(["admin", "reset-mfa", "--user", "operator", "--data-root", app.Root]));
        await app.RestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        await MfaTests.LoginAsync(app.Client);
        var current = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("enrollmentRequired", current.GetProperty("phase").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task ApiCannotStartWithoutExplicitMigration()
    {
        await using var app = await TestApplication.StartAsync();
        string emptyRoot = Path.Combine(app.Root, "uninitialized");
        await using var empty = AdminApiHost.Create(["--Administration:DataRoot", emptyRoot, "--urls", "https://localhost:0"], builder => builder.WebHost.UseTestServer());
        Assert.Equal("database_not_initialized", (await Assert.ThrowsAsync<AdminFailure>(() => empty.StartAsync())).Code);
        Assert.False(File.Exists(Path.Combine(emptyRoot, "admin.sqlite")));
        var options = new AdminOptions { DataRoot = emptyRoot };
        using var unlocked = AdminRuntimeLease.Exclusive(options);
    }
}
