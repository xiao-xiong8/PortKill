using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class ValidationArchiveTests
{
    [Fact]
    public async Task WorkerArchivesAndVerifiesAConcreteReleaseBeforeMarkingItReady()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var key = RSA.Create(2048);
        (byte[] envelope, byte[] package, string version) = CreateRelease(key);
        var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new { ManifestBytes = envelope.Length, PackageBytes = package.Length });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using (var manifest = Binary(envelope)) Assert.Equal(HttpStatusCode.OK,
            (await app.Client.PutAsync($"/api/v1/uploads/{uploadId}/manifest", manifest)).StatusCode);
        using (var zip = Binary(package)) Assert.Equal(HttpStatusCode.OK,
            (await app.Client.PutAsync($"/api/v1/uploads/{uploadId}/package", zip)).StatusCode);
        using var complete = await app.Client.PostAsJsonAsync($"/api/v1/uploads/{uploadId}/complete", new { });
        var completion = await complete.Content.ReadFromJsonAsync<JsonElement>();
        string taskId = completion.GetProperty("jobId").GetString()!;
        string draftId = completion.GetProperty("draftId").GetString()!;

        string configPath = Path.Combine(app.Root, "verification.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ManifestVerificationOptions
        {
            ManifestUrl = "https://updates.example/stable/manifest.json", PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        }, ProtocolJsonContext.Default.ManifestVerificationOptions));
        string executable = Path.Combine(FindRepositoryRoot(), "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0",
            "PortKiller.UpdateTool" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var options = new UpdateToolOptions { Enabled = true, ExecutablePath = executable, VerificationConfigPath = configPath };
        await using var scope = app.App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var executor = new ValidationTaskExecutor(database,
            scope.ServiceProvider.GetRequiredService<ValidationTaskQueue>(),
            scope.ServiceProvider.GetRequiredService<ValidationArchiveService>(),
            new UpdateToolClient(options), app.Clock, NullLogger<ValidationTaskExecutor>.Instance);
        Assert.True(await executor.ExecuteOnceAsync("archive-worker", CancellationToken.None));

        var draft = await database.ReleaseDrafts.SingleAsync(item => item.Id == draftId);
        var job = await database.ReleaseJobs.SingleAsync(item => item.Id == taskId);
        Assert.Equal(ValidationState.ReadyForApproval, draft.ValidationState);
        Assert.Equal(PublishJobState.Completed, job.State);
        Assert.Equal(version, draft.Version);
        Assert.NotNull(draft.ManifestExpiresUtcMs);
        Assert.True(draft.ManifestExpiresUtcMs > app.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal(package.Length, new FileInfo(Path.Combine(app.Root, "archive", draft.ArchiveId, "package.zip")).Length);

        string archivedPackage = Path.Combine(app.Root, "archive", draft.ArchiveId, "package.zip");
        string stagedPackage = Path.Combine(app.Root, "staging", (await database.UploadSessions.SingleAsync(item => item.Id == uploadId)).StorageId, "package.zip");
        byte[] archivedHash = SHA256.HashData(await File.ReadAllBytesAsync(archivedPackage));
        await File.WriteAllBytesAsync(stagedPackage, [9, 8, 7]);
        Assert.Equal(archivedHash, SHA256.HashData(await File.ReadAllBytesAsync(archivedPackage)));

        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/validation-tasks/{taskId}");
        Assert.Equal("readyForApproval", status.GetProperty("validationState").GetString());
        Assert.Equal(version, status.GetProperty("version").GetString());
        Assert.Equal(draft.ManifestExpiresUtcMs, status.GetProperty("manifestExpiresUtcMs").GetInt64());
    }

    [Fact]
    public async Task ChangedStagingBytesAreRejectedBeforeAnyReadyResult()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var key = RSA.Create(2048);
        (byte[] envelope, byte[] package, _) = CreateRelease(key);
        var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new { ManifestBytes = envelope.Length, PackageBytes = package.Length });
        string uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using (var manifest = Binary(envelope)) await app.Client.PutAsync($"/api/v1/uploads/{uploadId}/manifest", manifest);
        using (var zip = Binary(package)) await app.Client.PutAsync($"/api/v1/uploads/{uploadId}/package", zip);
        var complete = await app.Client.PostAsJsonAsync($"/api/v1/uploads/{uploadId}/complete", new { });
        string taskId = (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        using (var scope = app.App.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var upload = await database.UploadSessions.SingleAsync(item => item.Id == uploadId);
            await File.WriteAllBytesAsync(Path.Combine(app.Root, "staging", upload.StorageId, "package.zip"), [9, 8, 7]);
        }
        string configPath = Path.Combine(app.Root, "verification.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ManifestVerificationOptions
        {
            ManifestUrl = "https://updates.example/stable/manifest.json", PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        }, ProtocolJsonContext.Default.ManifestVerificationOptions));
        string executable = Path.Combine(FindRepositoryRoot(), "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0",
            "PortKiller.UpdateTool" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await using var workerScope = app.App.Services.CreateAsyncScope();
        var executor = new ValidationTaskExecutor(workerScope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>(),
            workerScope.ServiceProvider.GetRequiredService<ValidationTaskQueue>(), workerScope.ServiceProvider.GetRequiredService<ValidationArchiveService>(),
            new UpdateToolClient(new UpdateToolOptions { Enabled = true, ExecutablePath = executable, VerificationConfigPath = configPath }),
            app.Clock, NullLogger<ValidationTaskExecutor>.Instance);
        Assert.True(await executor.ExecuteOnceAsync("changed-source-worker", CancellationToken.None));
        var status = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/validation-tasks/{taskId}");
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.Equal("archive_source_changed", status.GetProperty("errorCode").GetString());
        Assert.Equal("rejected", status.GetProperty("validationState").GetString());
    }

    private static (byte[] Envelope, byte[] Package, string Version) CreateRelease(RSA key)
    {
        string root = FindRepositoryRoot();
        string executable = Path.Combine(root, "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0",
            "PortKiller.UpdateTool" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string version = UpdateManifest.ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion!).ToString();
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in ReleasePackage.Files)
            {
                using Stream target = archive.CreateEntry(name).Open();
                if (name == "PortKiller.exe") using (FileStream source = File.OpenRead(executable)) source.CopyTo(target);
                else target.Write([1, 2, 3, 4]);
            }
        }
        byte[] package = output.ToArray();
        var manifest = new UpdateManifest { Version = version, PackageUrl = $"https://updates.example/stable/PortKiller-{version}-win-x64.zip",
            Size = package.Length, Sha256 = Convert.ToHexString(SHA256.HashData(package)), Sequence = 1,
            PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30), ReleaseNotes = ["test release"] };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
        return (JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate { Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }, ProtocolJsonContext.Default.SignedUpdate), package, version);
    }

    private static ByteArrayContent Binary(byte[] value)
    {
        var content = new ByteArrayContent(value); content.Headers.ContentType = new("application/octet-stream"); return content;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root could not be located from test output.");
    }
}
