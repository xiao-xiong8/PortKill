using System.IO.Compression;
using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.Release.Tests;

// Publisher's repository-wide lock deliberately serializes all release operations in this collection.
[CollectionDefinition("release", DisableParallelization = true)]
public sealed class ReleaseCollection;

[Collection("release")]
public sealed class PreparationTests
{
    [Fact]
    public async Task PrepareOnlyProducesVerifiedUploadArtifactsWithoutAnyDeliveryCommands()
    {
        await using var fixture = new PreparationFixture();
        var result = await fixture.RunAsync(fixture.PrepareArguments()); Assert.True(result.Code == 0, result.Text);
        string directory = fixture.LatestDirectory();
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        Assert.Equal("PrepareOnly", record.RootElement.GetProperty("Mode").GetString());
        Assert.Equal("Verified", record.RootElement.GetProperty("Phase").GetString());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("Expected").ValueKind);
        var options = new ManifestVerificationOptions { ManifestUrl = "https://updates.example/stable/manifest.json", PublicKeyPem = await File.ReadAllTextAsync(fixture.PublicKey) };
        var manifest = UpdateManifest.Verify(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")), options);
        using var package = ZipFile.OpenRead(Path.Combine(directory, "PortKiller-" + fixture.Version + "-win-x64.zip"));
        Assert.Equal(ReleasePackage.Files.Order(), package.Entries.Select(item => item.FullName).Order());
        Assert.Equal(fixture.Version, manifest.Version);
        Assert.Empty(Directory.GetFiles(directory, "*-setup.zip"));
        Assert.False(File.Exists(Path.Combine(directory, "server-receipt.json")));
        Assert.False(File.Exists(fixture.NetworkGuard));
    }

    [Fact]
    public async Task PreparedResumeOnlyRevalidatesExistingBytesAndCannotImplicitlyDeliver()
    {
        await using var fixture = new PreparationFixture();
        var prepared = await fixture.RunAsync(fixture.PrepareArguments()); Assert.True(prepared.Code == 0, prepared.Text);
        string directory = fixture.LatestDirectory(), id = Path.GetFileName(directory);
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"));
        var resumed = await fixture.RunAsync(["-PrepareOnly", "-Resume", id]); Assert.True(resumed.Code == 0, resumed.Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        var delivered = await fixture.RunAsync(["-Resume", id]); Assert.NotEqual(0, delivered.Code);
        Assert.Contains("PrepareOnly", delivered.Text); Assert.False(File.Exists(fixture.NetworkGuard));
    }

    [Theory]
    [InlineData("-SshUser", "operator")]
    [InlineData("-ServerIp", "updates.example")]
    [InlineData("-LocalRoot", "local-public")]
    public async Task PrepareRejectsAmbiguousDeliveryParametersBeforeAllocatingARelease(string name, string value)
    {
        await using var fixture = new PreparationFixture();
        var result = await fixture.RunAsync([.. fixture.PrepareArguments(), name, value]);
        Assert.NotEqual(0, result.Code); Assert.Contains("PrepareOnly", result.Text);
        Assert.False(File.Exists(fixture.NetworkGuard));
        Assert.Throws<InvalidOperationException>(() => fixture.LatestDirectory());
    }

    [Fact]
    public async Task PrepareRequiresAnExplicitSourceInsteadOfReadingSshDefaults()
    {
        await using var fixture = new PreparationFixture();
        var result = await fixture.RunAsync(["-PrepareOnly"]);
        Assert.NotEqual(0, result.Code); Assert.Contains("PublicBaseUrl", result.Text);
        Assert.False(File.Exists(fixture.NetworkGuard));
    }
}
