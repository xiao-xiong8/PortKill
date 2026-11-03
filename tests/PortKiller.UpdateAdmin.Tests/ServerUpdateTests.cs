using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.ServerUpdates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed partial class ServerUpdateTests
{
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static byte[] DockerArchive(string image)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, true))
        {
            using var data = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new[] { new { RepoTags = new[] { image }, Config = "config.json", Layers = Array.Empty<string>() } }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = data });
        }
        return output.ToArray();
    }
    private static byte[] Package(RSA key, string fingerprint = Fingerprint, string variant = "")
    {
        var files = new Dictionary<string, byte[]> { ["images/application.tar.gz"] = DockerArchive("portkill-update-server:test"),
            ["images/runner.tar.gz"] = DockerArchive("portkill-server-update-runner:test"), ["web/index.html"] = "<html>new</html>"u8.ToArray() };
        var manifest = new ServerPackageManifest(1, "portkill-server-update", "1.2.3", variant == "platform" ? "linux-arm64" : "linux-x64", variant == "migration",
            fingerprint, "portkill-update-server:test", "portkill-server-update-runner:test",
            files.Select(f => new ServerPackageFile(f.Key, f.Value.Length, Convert.ToHexStringLower(SHA256.HashData(f.Value)))).ToArray(), 1000);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ServerUpdateFiles.Json);
        if (variant == "missingFlag")
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!; node.AsObject().Remove("databaseChanges"); payload = JsonSerializer.SerializeToUtf8Bytes(node);
        }
        byte[] sig = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (variant == "signature") sig[0] ^= 1;
        if (variant == "digest") files["web/index.html"][0] ^= 1;
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes, int attributes = 0) { var entry = zip.CreateEntry(name); entry.ExternalAttributes = attributes; using var stream = entry.Open(); stream.Write(bytes); }
            Add("server-manifest.json", payload); Add("server-manifest.sig", sig);
            foreach (var file in files) Add(file.Key, file.Value);
            if (variant == "traversal") Add("../escape.txt", [1]);
            if (variant == "duplicate") Add("web/INDEX.html", [1]);
            if (variant == "symlink") Add("web/link", [1], 0xA000 << 16);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData("signature")][InlineData("digest")][InlineData("platform")][InlineData("migration")]
    [InlineData("missingFlag")][InlineData("traversal")][InlineData("duplicate")][InlineData("symlink")]
    public async Task RejectsUnsafeOrIncompatiblePackagesBeforeDocker(string variant)
    {
        using var key = RSA.Create(2048); string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "package.zip"); File.WriteAllBytes(zip, Package(key, variant: variant));
            await Assert.ThrowsAnyAsync<Exception>(() => ServerPackageVerifier.ExtractAsync(zip, Path.Combine(root, "out"), key.ExportSubjectPublicKeyInfoPem(), new(), Fingerprint, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ValidPackageIsVerifiedAndMutationIsRejectedBeforeUse()
    {
        using var key = RSA.Create(2048); string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "package.zip"), content = Path.Combine(root, "out"); File.WriteAllBytes(zip, Package(key));
            var result = await ServerPackageVerifier.ExtractAsync(zip, content, key.ExportSubjectPublicKeyInfoPem(), new(), Fingerprint, CancellationToken.None);
            Assert.Equal("1.2.3", result.Version);
            await ServerPackageContent.VerifyAsync(content, key.ExportSubjectPublicKeyInfoPem(), new(), Fingerprint);
            File.WriteAllText(Path.Combine(content, "web/index.html"), "changed");
            await Assert.ThrowsAsync<InvalidDataException>(() => ServerPackageContent.VerifyAsync(content, key.ExportSubjectPublicKeyInfoPem(), new(), Fingerprint));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RejectsOversizedExpansionAndUnexpectedImageTags()
    {
        using var key = RSA.Create(2048); string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "package.zip"); File.WriteAllBytes(zip, Package(key));
            await Assert.ThrowsAsync<InvalidDataException>(() => ServerPackageVerifier.ExtractAsync(zip, Path.Combine(root, "out"), key.ExportSubjectPublicKeyInfoPem(), new() { MaximumExpandedBytes = 100 }, Fingerprint, CancellationToken.None));
            string tar = Path.Combine(root, "image.tar.gz"); File.WriteAllBytes(tar, DockerArchive("unexpected:tag"));
            await Assert.ThrowsAsync<InvalidDataException>(() => DockerArchiveVerifier.VerifyAsync(tar, "expected:tag", 100000));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ServerPermissionsRequireAdministratorAndFreshMfaButReadSurvivesExpiry()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/server-updates/")).StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.PostAsJsonAsync("/api/v1/server-updates/packages/" + new string('a', 32) + "/apply", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/server-updates/")).StatusCode);
        Assert.DoesNotContain(AdminPermissions.ServerUpdate, AdminPermissions.ForRoles(["Publisher", "Uploader", "Viewer"]));
    }

    [Fact]
    public async Task ApiUploadPersistsVerifiedPackageAndKeepsDesktopLimit()
    {
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = "/srv/portkill", Project = "portkill" };
        await using var app = await TestApplication.StartAsync(s => s.AddSingleton(options)); await MfaTests.EnrollAsync(app);
        using var key = RSA.Create(2048); options.VerificationConfigPath = Path.Combine(app.Root, "verification.json");
        File.WriteAllText(options.VerificationConfigPath, JsonSerializer.Serialize(new { publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
        using var scope = app.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ServerUpdateStore>(); store.Ensure();
        var interrupted = Enumerable.Range(0, 4).Select(_ => store.PackagePath(Guid.NewGuid().ToString("N"))).ToArray();
        foreach (string directory in interrupted) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "package.zip"), "interrupted upload"); }
        byte[] bytes = Package(key, ServerPackageVerifier.Fingerprint(db.Database.GetMigrations()));
        using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("application/octet-stream");
        var response = await app.Client.PostAsync("/api/v1/server-updates/packages", content);
        response.EnsureSuccessStatusCode(); var package = (await response.Content.ReadFromJsonAsync<ServerPackage>())!;
        Assert.True(File.Exists(Path.Combine(app.Root, "server-updates/packages", package.Id, "content/web/index.html")));
        Assert.All(interrupted, directory => Assert.False(Directory.Exists(directory)));
        Assert.Equal(268435456, scope.ServiceProvider.GetRequiredService<AdminOptions>().MaxUploadPackageBytes);
        await app.RestartAsync();
        var summary = await app.Client.GetFromJsonAsync<ServerUpdateSummary>("/api/v1/server-updates/");
        Assert.Contains(summary!.Packages, p => p.Id == package.Id);
    }

    [Fact]
    public void TaskLogCursorAndExclusiveStartLockSurviveNewStoreInstance()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); var options = new AdminOptions { DataRoot = root };
        try
        {
            var store = new ServerUpdateStore(options); store.Ensure(); string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(store.TaskPath(id));
            store.Save(new(id, id, "1.0.0", "test", "running", "test", 0)); store.Event(id, "first"); store.Event(id, "second");
            var restored = new ServerUpdateStore(options); Assert.Equal("second", Assert.Single(restored.Logs(id, 1)).Message);
            using (store.Lock()) Assert.Throws<AdminFailure>(() => restored.Lock());
            File.WriteAllText(ServerUpdateFiles.Pending(options), id); Assert.True(ServerUpdateFiles.IsPending(options));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void WebCopyRetainsOldHashesAndReplacesIndexWithoutReplacingMountDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), source = Path.Combine(root, "source"), target = Path.Combine(root, "web");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(source, "index.html"), "new"); File.WriteAllText(Path.Combine(target, "old-hash.js"), "old");
            ServerUpdateRunner.CopyWeb(source, target);
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "index.html"))); Assert.True(File.Exists(Path.Combine(target, "old-hash.js")));
        }
        finally { Directory.Delete(root, true); }
    }
}
