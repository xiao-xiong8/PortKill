using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateAdmin.Tests;
using PortKiller.Updates;
using Xunit.Abstractions;

namespace PortKiller.Release.Tests;

[Collection("release")]
public sealed class PublicationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GenuineDesktopAotPassesHttpsAuthenticationWorkerPublicationAndExistingClientVerification()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true, preparedDirectory: PreparationFixture.Prepared);
        var publication = distribution.Publication;
        await using var local = new PreparationFixture(publication.ExportTestSigningKey());
        var prepared = await PrepareAsync(local, publication);
        await using var worker = P4ChildProcess.Worker(publication);
        var upload = await publication.UploadAsync(prepared.Manifest, false, prepared.Package);
        await WaitAsync(async () => (await SnapshotAsync(publication, upload.JobId)).GetProperty("validation").GetProperty("validationState").GetString() == "readyForApproval", worker);
        string id = await publication.ConfirmAsync(upload.DraftId);
        await WaitAsync(async () => (await SnapshotAsync(publication, id)).GetProperty("publication").GetProperty("state").GetString() == "completed", worker);
        Assert.Equal(prepared.Manifest, await File.ReadAllBytesAsync(Path.Combine(publication.Options.ReleaseRoot, "manifest.json")));
        var verification = JsonSerializer.Deserialize(await File.ReadAllTextAsync(publication.Options.VerificationConfigPath), ProtocolJsonContext.Default.ManifestVerificationOptions)!;
        var signed = UpdateManifest.Verify(prepared.Manifest, verification);
        var service = new UpdateService(new UpdateOptions { Enabled = true, AllowHttp = true, RealtimeEnabled = false,
            ManifestUrl = verification.ManifestUrl, PublicKeyPem = verification.PublicKeyPem }, Path.Combine(local.Root, "client-cache"),
            current: "8.2.0", trustPath: Path.Combine(local.Root, "client-trust.json"));
        await service.CheckAsync(default, download: false);
        Assert.Equal(UpdateStage.Available, service.Snapshot.Stage); Assert.Null(service.Ready);
        await service.DownloadAvailableAsync(default);
        Assert.Equal(UpdateStage.Ready, service.Snapshot.Stage); Assert.NotNull(service.Prepared);
        Assert.Equal(signed.Version, service.Ready!.Version);
        Assert.Equal(signed.Sequence, service.Ready.Sequence);
        Assert.Equal(signed.PayloadSha256, service.Ready.PayloadSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(prepared.Package)), service.Ready!.Sha256);
        Assert.Equal(prepared.Package, await File.ReadAllBytesAsync(service.ReadyPackage!));
        using var lease = service.TryAcquireInstallation(); Assert.NotNull(lease);
        Assert.Equal(ReleaseIdentity.From(signed), lease.Update.Identity);
        output.WriteLine(JsonSerializer.Serialize(new { id, service.Ready.Version, service.Ready.Sequence,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(prepared.Manifest)), PackageSha256 = service.Ready.Sha256,
            GenuineAotDirectory = Path.GetFileName(PreparationFixture.Prepared), ClientCheck = "available-before-download; verified-ready-after-explicit-download" }));
    }

    [Fact]
    public async Task TamperedDesktopAotPackageIsRejectedBeforeApprovalOrActivation()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true, preparedDirectory: PreparationFixture.Prepared);
        var publication = distribution.Publication;
        await using var local = new PreparationFixture(publication.ExportTestSigningKey());
        var prepared = await PrepareAsync(local, publication); prepared.Package[^1] ^= 1;
        await using var worker = P4ChildProcess.Worker(publication);
        var upload = await publication.UploadAsync(prepared.Manifest, false, prepared.Package);
        await WaitAsync(async () => (await SnapshotAsync(publication, upload.JobId)).GetProperty("validation").GetProperty("validationState").GetString() == "rejected", worker);
        using var response = await publication.App.Client.PostAsJsonAsync($"/api/v1/releases/{upload.DraftId}/publish/challenge", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(publication.Options.ReleaseRoot, "manifest.json")));
    }

    [Fact]
    public async Task StalePreparedSequenceCannotReplaceANewerSignedRelease()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true, preparedDirectory: PreparationFixture.Prepared);
        var publication = distribution.Publication;
        await using var local = new PreparationFixture(publication.ExportTestSigningKey());
        var prepared = await PrepareAsync(local, publication);
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(local.LatestDirectory(), "release-record.json")));
        byte[] newer = publication.Sign(record.RootElement.GetProperty("Sequence").GetInt64() + 1, prepared.Package);
        string manifestPath = Path.Combine(local.Root, "newer.json"), packagePath = Path.Combine(local.Root, "newer.zip");
        await File.WriteAllBytesAsync(manifestPath, newer); await File.WriteAllBytesAsync(packagePath, prepared.Package);
        var activated = await publication.Tool.ActivateAsync(manifestPath, packagePath, "none", default,
            Convert.ToHexString(SHA256.HashData(newer)), Convert.ToHexString(SHA256.HashData(prepared.Package)));
        Assert.True(activated.Succeeded);
        await using var worker = P4ChildProcess.Worker(publication);
        var upload = await publication.UploadAsync(prepared.Manifest, false, prepared.Package);
        await WaitAsync(async () => (await SnapshotAsync(publication, upload.JobId)).GetProperty("validation").GetProperty("validationState").GetString() == "readyForApproval", worker);
        string id = await publication.ConfirmAsync(upload.DraftId);
        await WaitAsync(async () => (await SnapshotAsync(publication, id)).GetProperty("publication").GetProperty("state").GetString() == "superseded", worker);
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(publication.Options.ReleaseRoot, "manifest.json")));
    }

    private static async Task<(byte[] Manifest, byte[] Package)> PrepareAsync(PreparationFixture local, PublishFixture publication)
    {
        var verification = JsonSerializer.Deserialize(await File.ReadAllTextAsync(publication.Options.VerificationConfigPath), ProtocolJsonContext.Default.ManifestVerificationOptions)!;
        string source = new Uri(new Uri(verification.ManifestUrl), "./").AbsoluteUri;
        var result = await local.RunAsync([.. local.PrepareArguments(source), "-AllowHttp"]);
        Assert.True(result.Code == 0, result.Text);
        string directory = local.LatestDirectory();
        return (await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")), await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-win-x64.zip").Single()));
    }
    private static Task<JsonElement> SnapshotAsync(PublishFixture publication, string id)
        => publication.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}");
    private static async Task WaitAsync(Func<Task<bool>> condition, P4ChildProcess worker)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            await worker.EnsureRunningAsync();
            if (watch.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Release pipeline did not reach the expected durable state.");
            await Task.Delay(250);
        }
    }
}
