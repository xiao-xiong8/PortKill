using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.ServerUpdates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed partial class ServerUpdateTests
{
    [Theory]
    [InlineData(false, false, false, "starting", "failed")]
    [InlineData(false, false, false, "recoveryRequired", "failed")]
    [InlineData(true, false, false, "starting", "recoveryRequired")]
    [InlineData(false, true, false, "starting", "recoveryRequired")]
    [InlineData(false, false, true, "starting", "starting")]
    public async Task ReconciliationOnlyFinishesUnlaunchedTasksWithoutRemovingFences(bool pending, bool switched, bool running, string initial, string expected)
    {
        using var fixture = new RunnerFixture(); fixture.Docker.UpdaterRunning = running;
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(services => { services.AddSingleton(options); services.AddSingleton<IDockerCommand>(fixture.Docker); });
        using var scope = app.App.Services.CreateScope();
        options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>(); store.Ensure();
        var admin = scope.ServiceProvider.GetRequiredService<AdminOptions>();
        string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(store.TaskPath(id));
        store.Save(new(id, id, "1.0.0", "test", initial, "interrupted", 0, "server_runner_interrupted"));
        if (pending) ServerUpdateFiles.SetPending(admin, id);
        if (switched) File.WriteAllText(Path.Combine(store.TaskPath(id), "switch-started"), id);
        await scope.ServiceProvider.GetRequiredService<ServerUpdateService>().SummaryAsync(default);
        Assert.Equal(expected, store.Task(id).State);
        Assert.Equal(pending, ServerUpdateFiles.IsPending(admin));
    }

    [Fact]
    public async Task FourFailedPackagesCanBeDeletedAndCorrectedPackageUploadedWithoutLosingHistory()
    {
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(services => services.AddSingleton(options));
        using var scope = app.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ServerUpdateService>();
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>(); store.Ensure();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        using var key = RSA.Create(2048); options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        File.WriteAllText(options.VerificationConfigPath, JsonSerializer.Serialize(new { publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        for (int index = 0; index < ids.Length; index++)
        {
            string id = ids[index]; Directory.CreateDirectory(store.PackagePath(id)); Directory.CreateDirectory(store.TaskPath(id));
            ServerUpdateFiles.Write(Path.Combine(store.PackagePath(id), "package.json"), new ServerPackage(id, "1.0.0", "app:test", "runner:test", 1, 0));
            store.Save(new(id, id, "1.0.0", "test", index % 2 == 0 ? "failed" : "rolledBack", "finished", 0));
        }
        foreach (string id in ids) service.DeletePackage(id);
        byte[] bytes = Package(key, ServerPackageVerifier.Fingerprint(database.Database.GetMigrations()));
        var uploaded = await service.UploadAsync(new MemoryStream(bytes), bytes.Length, default);
        Assert.Single(store.List<ServerPackage>("packages", "package.json"));
        Assert.All(ids, id => Assert.True(store.Task(id).Terminal));
        Assert.True(Directory.Exists(store.PackagePath(uploaded.Id)));
    }

    [Theory]
    [InlineData("succeeded")][InlineData("running")][InlineData("recoveryRequired")]
    public async Task PackageDeletionStillProtectsSuccessfulAndRecoveryDependencies(string state)
    {
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(services => services.AddSingleton(options));
        using var scope = app.App.Services.CreateScope(); options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>(); store.Ensure();
        string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(store.PackagePath(id)); Directory.CreateDirectory(store.TaskPath(id));
        store.Save(new(id, id, "1.0.0", "test", state, "test", 0));
        var error = Assert.Throws<AdminFailure>(() => scope.ServiceProvider.GetRequiredService<ServerUpdateService>().DeletePackage(id));
        Assert.Equal("server_package_in_use", error.Code); Assert.True(Directory.Exists(store.PackagePath(id)));
    }

    [Fact]
    public async Task LowDockerSpaceStopsStartBeforeDatabaseBackupOrRunnerImport()
    {
        using var fixture = new RunnerFixture(); fixture.Docker.DockerFreeBytes = 1;
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(services => { services.AddSingleton(options); services.AddSingleton<IDockerCommand>(fixture.Docker); });
        using var scope = app.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ServerUpdateService>();
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        using var key = RSA.Create(2048); options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        File.WriteAllText(options.VerificationConfigPath, JsonSerializer.Serialize(new { publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
        byte[] bytes = Package(key, ServerPackageVerifier.Fingerprint(database.Database.GetMigrations()));
        var uploaded = await service.UploadAsync(new MemoryStream(bytes), bytes.Length, default);
        // This preflight concerns the host Docker disk; its existing containers are supplied by the fixture.
        fixture.Docker.InspectedRequest = fixture.Request with { Options = options, DataRoot = scope.ServiceProvider.GetRequiredService<AdminOptions>().DataRoot };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test")]));
        var error = await Assert.ThrowsAsync<AdminFailure>(() => service.StartAsync(uploaded.Id, principal));
        Assert.Equal("server_update_space", error.Code);
        Assert.Empty(store.List<ServerUpdateTask>("tasks", "status.json"));
        Assert.DoesNotContain(fixture.Docker.Commands, args => args[0] == "load");
        Assert.DoesNotContain(Directory.GetDirectories(store.Root, "*", SearchOption.AllDirectories), path => Path.GetFileName(path) == "database-backup");
    }

    [Fact]
    public async Task RevokedSessionCannotAcceptAnUpdateAfterPreflight()
    {
        using var fixture = new RunnerFixture();
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(services => { services.AddSingleton(options); services.AddSingleton<IDockerCommand>(fixture.Docker); });
        await MfaTests.EnrollAsync(app);
        using var scope = app.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ServerUpdateService>();
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
        var principal = await sessions.PrincipalAsync(new SessionTicket(await database.Users.SingleAsync(),
            await database.AdminSessions.SingleAsync(s => s.Phase == SessionPhase.Full && s.RevokedUtcMs == null)));
        using var key = RSA.Create(2048); options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        File.WriteAllText(options.VerificationConfigPath, JsonSerializer.Serialize(new { publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
        byte[] bytes = Package(key, ServerPackageVerifier.Fingerprint(database.Database.GetMigrations()));
        var uploaded = await service.UploadAsync(new MemoryStream(bytes), bytes.Length, default);
        fixture.Docker.InspectedRequest = fixture.Request with { Options = options, DataRoot = scope.ServiceProvider.GetRequiredService<AdminOptions>().DataRoot };
        await sessions.RevokeAllOfflineAsync();
        var error = await Assert.ThrowsAsync<AdminFailure>(() => service.StartAsync(uploaded.Id, principal));
        Assert.Equal("authentication_required", error.Code);
        Assert.Empty(store.List<ServerUpdateTask>("tasks", "status.json"));
        Assert.False(ServerUpdateFiles.IsPending(scope.ServiceProvider.GetRequiredService<AdminOptions>()));
        Assert.DoesNotContain(fixture.Docker.Commands, args => args[0] == "load");
    }

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires Linux Unix permission enforcement."; }
    }

    [LinuxFact]
    public void SharedDirectoryRepairRestoresGroupWriteWithoutChangingPrivateChildren()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string shared = Path.Combine(root, "shared"), backup = Path.Combine(shared, "database-backup");
            Directory.CreateDirectory(backup);
            File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ServerUpdateStore.EnsureSharedDirectory(shared);
            Assert.True(File.GetUnixFileMode(shared).HasFlag(UnixFileMode.GroupWrite | UnixFileMode.SetGroup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backup));
        }
        finally { Directory.Delete(root, true); }
    }
}
