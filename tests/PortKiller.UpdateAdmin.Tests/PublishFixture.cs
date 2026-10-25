using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

internal sealed class PublishFixture : IAsyncDisposable
{
    public TestApplication App { get; private set; } = null!;
    public UpdateToolOptions Options { get; } = new() { Enabled = true };
    public UpdateToolClient Tool => new(Options);
    private readonly RSA _key = RSA.Create(2048);
    public byte[] Package { get; private set; } = [];
    public string Version { get; private set; } = "";
    internal string ExportTestSigningKey() => _key.ExportRSAPrivateKeyPem();

    public static async Task<PublishFixture> StartAsync(string? manifestUrl = null, bool realHttp = false, string? preparedDirectory = null)
    {
        var fixture = new PublishFixture();
        fixture.App = await TestApplication.StartAsync(services => services.AddSingleton(fixture.Options), realHttp);
        fixture.Options.ReleaseRoot = Path.Combine(fixture.App.Root, "public");
        fixture.Options.VerificationConfigPath = Path.Combine(fixture.App.Root, "verification.json");
        fixture.Options.ExecutablePath = FindTool();
        Directory.CreateDirectory(fixture.Options.ReleaseRoot);
        await File.WriteAllTextAsync(fixture.Options.VerificationConfigPath, JsonSerializer.Serialize(new ManifestVerificationOptions
        {
            ManifestUrl = manifestUrl ?? "https://updates.example/stable/manifest.json",
            AllowHttp = manifestUrl?.StartsWith("http:", StringComparison.Ordinal) == true,
            PublicKeyPem = fixture._key.ExportSubjectPublicKeyInfoPem()
        }, ProtocolJsonContext.Default.ManifestVerificationOptions));
        fixture.Version = UpdateManifest.ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(
            preparedDirectory == null ? FindTool() : Path.Combine(preparedDirectory, "PortKiller.exe")).FileVersion!).ToString();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (string name in ReleasePackage.Files)
            {
                using Stream target = zip.CreateEntry(name).Open();
                if (preparedDirectory != null) using (var input = File.OpenRead(Path.Combine(preparedDirectory, name))) input.CopyTo(target);
                else if (name == "PortKiller.exe") using (var input = File.OpenRead(FindTool())) input.CopyTo(target);
                else target.Write([1, 2, 3, 4]);
            }
        fixture.Package = output.ToArray();
        await MfaTests.EnrollAsync(fixture.App);
        return fixture;
    }

    public byte[] Sign(long sequence, byte[]? package = null, DateTimeOffset? expires = null, int schema = 2,
        DateTimeOffset? published = null)
    {
        var verification = JsonSerializer.Deserialize(File.ReadAllText(Options.VerificationConfigPath),
            ProtocolJsonContext.Default.ManifestVerificationOptions)!;
        var manifest = new UpdateManifest
        {
            Schema = schema, Version = Version, Sequence = sequence, PublishedUtc = published ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddDays(30),
            PackageUrl = new Uri(new Uri(verification.ManifestUrl), $"PortKiller-{Version}-win-x64.zip").AbsoluteUri,
            Size = (package ?? Package).Length, Sha256 = Convert.ToHexString(SHA256.HashData(package ?? Package)),
            ReleaseNotes = ["integration release"]
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        }, ProtocolJsonContext.Default.SignedUpdate);
    }

    public async Task<(string DraftId, string JobId)> UploadAsync(byte[] manifest, bool executeValidation = true, byte[]? package = null)
    {
        byte[] uploadPackage = package ?? Package;
        using var created = await App.Client.PostAsJsonAsync("/api/v1/uploads/", new { ManifestBytes = manifest.Length, PackageBytes = uploadPackage.Length });
        created.EnsureSuccessStatusCode();
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using (var content = Binary(manifest)) (await App.Client.PutAsync($"/api/v1/uploads/{id}/manifest", content)).EnsureSuccessStatusCode();
        using (var content = Binary(uploadPackage)) (await App.Client.PutAsync($"/api/v1/uploads/{id}/package", content)).EnsureSuccessStatusCode();
        using var complete = await App.Client.PostAsJsonAsync($"/api/v1/uploads/{id}/complete", new { });
        complete.EnsureSuccessStatusCode();
        var value = await complete.Content.ReadFromJsonAsync<JsonElement>();
        if (executeValidation)
        {
            await using var scope = App.App.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ValidationTaskExecutor>().ExecuteOnceAsync("fixture-validator", default));
        }
        return (value.GetProperty("draftId").GetString()!, value.GetProperty("jobId").GetString()!);
    }

    public async Task<string> ConfirmAsync(string draftId)
    {
        using var challenge = await App.Client.PostAsJsonAsync($"/api/v1/releases/{draftId}/publish/challenge", new { });
        challenge.EnsureSuccessStatusCode();
        string id = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var confirmed = await App.Client.PostAsJsonAsync($"/api/v1/releases/{draftId}/publish",
            new { ChallengeId = id, IdempotencyKey = Guid.NewGuid().ToString("N") });
        confirmed.EnsureSuccessStatusCode();
        return (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    public async Task<bool> ExecuteAsync(CancellationToken token = default)
    {
        await using var scope = App.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublishTaskExecutor>().ExecuteOnceAsync("fixture-publisher", token);
    }

    public async Task<bool> ExecutePublicCheckAsync(CancellationToken token = default)
    {
        await using var scope = App.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublicVerificationTaskExecutor>().ExecuteOnceAsync("fixture-checker", token);
    }

    public async Task<UpdateToolActivationResult> ActivateOutsideApiAsync(byte[] manifest, string expected)
    {
        string manifestPath = Path.Combine(App.Root, "cli-manifest.json"), packagePath = Path.Combine(App.Root, "cli-package.zip");
        await File.WriteAllBytesAsync(manifestPath, manifest); await File.WriteAllBytesAsync(packagePath, Package);
        return await Tool.ActivateAsync(manifestPath, packagePath, expected, default,
            Convert.ToHexString(SHA256.HashData(manifest)), Convert.ToHexString(SHA256.HashData(Package)));
    }

    public static string FindTool()
        => FindExecutable("src/Tools/PortKiller.UpdateTool", "PortKiller.UpdateTool");

    internal static string FindExecutable(string project, string name)
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx")))
                return Path.Combine(current.FullName, project, "bin", "Release", "net10.0", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        throw new DirectoryNotFoundException();
    }

    private static ByteArrayContent Binary(byte[] bytes)
    { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/octet-stream"); return content; }

    public async ValueTask DisposeAsync() { _key.Dispose(); await App.DisposeAsync(); }
}
