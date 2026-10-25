using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortKiller.Release.Tests;

[Collection("release")]
public sealed class ArtifactTests
{
    [Fact]
    public async Task RecordMatchesSignedMetadataExactArchiveAndNativeExecutable()
    {
        await using var fixture = new PreparationFixture();
        var prepared = await fixture.RunAsync(fixture.PrepareArguments()); Assert.True(prepared.Code == 0, prepared.Text);
        string directory = fixture.LatestDirectory();
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        using var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        using var payload = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("Payload").GetString()!));
        var data = record.RootElement;
        Assert.Equal(2, data.GetProperty("Schema").GetInt32()); Assert.True(data.GetProperty("NativeAot").GetBoolean());
        Assert.False(data.GetProperty("BaselineKnown").GetBoolean());
        Assert.Equal(fixture.Fingerprint, data.GetProperty("KeyFingerprint").GetString());
        Assert.Equal(payload.RootElement.GetProperty("Version").GetString(), data.GetProperty("Version").GetString());
        Assert.Equal(payload.RootElement.GetProperty("Sequence").GetInt64(), data.GetProperty("Sequence").GetInt64());
        Assert.Equal(DateTimeOffset.Parse(payload.RootElement.GetProperty("ExpiresUtc").GetString()!), DateTimeOffset.Parse(data.GetProperty("ExpiresUtc").GetString()!));
        string packagePath = Directory.GetFiles(directory, "*-win-x64.zip").Single();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))), data.GetProperty("PackageSha256").GetString());
        using var zip = ZipFile.OpenRead(packagePath); await using var exe = zip.GetEntry("PortKiller.exe")!.Open();
        Assert.Equal(Convert.ToHexString(await SHA256.HashDataAsync(exe)), data.GetProperty("ExeSha256").GetString());
        Assert.DoesNotContain("PRIVATE KEY", record.RootElement.GetRawText());
        Assert.DoesNotContain(fixture.PrivateKey, record.RootElement.GetRawText());
    }

    [Fact]
    public async Task MismatchedKeysAreRejectedBeforeReleaseAndSequenceAllocation()
    {
        await using var fixture = new PreparationFixture(); using var wrong = RSA.Create(2048);
        string wrongPath = Path.Combine(fixture.Root, "wrong-public.pem"); await File.WriteAllTextAsync(wrongPath, wrong.ExportSubjectPublicKeyInfoPem());
        var arguments = fixture.PrepareArguments(); arguments[Array.IndexOf(arguments, fixture.PublicKey)] = wrongPath;
        var result = await fixture.RunAsync(arguments);
        Assert.NotEqual(0, result.Code); Assert.Contains("SigningKeyMismatch", result.Text);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "update-sequence.txt")));
        Assert.Throws<InvalidOperationException>(() => fixture.LatestDirectory());
    }

    [Fact]
    public async Task ManagedAppHostCannotMasqueradeAsNativeAotWhenCompanionDllIsMissing()
    {
        await using var fixture = new PreparationFixture();
        string fake = Path.Combine(fixture.Root, "ordinary-apphost"); Directory.CreateDirectory(fake);
        File.Copy(Path.Combine(PreparationFixture.Repository, "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0", "PortKiller.UpdateTool.exe"), Path.Combine(fake, "PortKiller.exe"));
        foreach (string name in new[] { "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll" })
            File.Copy(Path.Combine(PreparationFixture.Prepared, name), Path.Combine(fake, name));
        var arguments = fixture.PrepareArguments(); arguments[Array.IndexOf(arguments, PreparationFixture.Prepared)] = fake;
        var result = await fixture.RunAsync(arguments);
        Assert.NotEqual(0, result.Code); Assert.Contains("Native AOT", result.Text);
        Assert.False(File.Exists(Path.Combine(fixture.LatestDirectory(), "manifest.json")));
        Assert.False(File.Exists(fixture.NetworkGuard));
    }

    [Fact]
    public async Task PersistedFutureSequenceIsRespectedAndPreparedResumeRejectsTampering()
    {
        await using var fixture = new PreparationFixture();
        long previous = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100_000;
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "update-sequence.txt"), previous.ToString());
        var result = await fixture.RunAsync(fixture.PrepareArguments()); Assert.True(result.Code == 0, result.Text);
        string directory = fixture.LatestDirectory();
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        Assert.Equal(previous + 1, record.RootElement.GetProperty("Sequence").GetInt64());
        await File.AppendAllTextAsync(Path.Combine(directory, "manifest.json"), " ");
        var resumed = await fixture.RunAsync(["-PrepareOnly", "-Resume", Path.GetFileName(directory)]);
        Assert.NotEqual(0, resumed.Code); Assert.False(File.Exists(fixture.NetworkGuard));
    }
}
