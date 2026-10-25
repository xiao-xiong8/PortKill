using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateAdmin.Tests;

namespace PortKiller.Release.Tests;

[Collection("release")]
public sealed class CompatibilityTests
{
    [Fact]
    public async Task RealSshScpLegacyReleaseRetainsImmutableResumeAndRenewal()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(preparedDirectory: PreparationFixture.Prepared);
        var publication = distribution.Publication;
        await using var local = new PreparationFixture(publication.ExportTestSigningKey());
        await using var ssh = await SshFixture.StartAsync(local, publication);
        string source = new Uri(new Uri((await ReadOptionsAsync(publication)).ManifestUrl), "./").AbsoluteUri;
        string[] arguments = [.. local.PrepareArguments(source).Where(value => value != "-PrepareOnly"), "-AllowHttp", "-SshUser", "release-test", "-ServerIp", "127.0.0.1", "-SshPort", ssh.Port.ToString()];
        distribution.Mode = "missing";
        var failed = await local.RunAsync(arguments, networkGuard: false, ssh.Config); Assert.NotEqual(0, failed.Code);
        string directory = local.LatestDirectory(), id = Path.GetFileName(directory);
        using (var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json"))))
            Assert.True(record.RootElement.GetProperty("Phase").GetString() == "ManifestActivated", failed.Text + await ssh.DiagnosticsAsync());
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"));
        distribution.Mode = "healthy";
        string[] resume = ["-Resume", id, "-SshUser", "release-test", "-ServerIp", "127.0.0.1", "-SshPort", ssh.Port.ToString()];
        var resumed = await local.RunAsync(resume, false, ssh.Config); Assert.True(resumed.Code == 0, resumed.Text);
        var repeated = await local.RunAsync(resume, false, ssh.Config); Assert.True(repeated.Code == 0, repeated.Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        string resumedCommands = await File.ReadAllTextAsync(Path.Combine(ssh.Root, "commands.jsonl"));
        Assert.Single(resumedCommands.Split('\n'), line => line.Contains("\"activate\""));
        Assert.Equal(2, resumedCommands.Split('\n').Count(line => line.Contains("\"recover\"")));
        var renewed = await local.RunAsync([.. arguments, "-ResignRelease", id], false, ssh.Config); Assert.True(renewed.Code == 0, renewed.Text);
        string newer = local.LatestDirectory();
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-win-x64.zip").Single())),
            SHA256.HashData(await File.ReadAllBytesAsync(Directory.GetFiles(newer, "*-win-x64.zip").Single())));
        string commands = await File.ReadAllTextAsync(Path.Combine(ssh.Root, "commands.jsonl"));
        Assert.Contains("inspect", commands); Assert.Contains("activate", commands);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(ssh.Root, "virtual"), "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OldSshResumeCannotReplaceANewerPublicationMadeThroughTheWebsite()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true, preparedDirectory: PreparationFixture.Prepared);
        var publication = distribution.Publication;
        await using var local = new PreparationFixture(publication.ExportTestSigningKey());
        await using var ssh = await SshFixture.StartAsync(local, publication);
        string source = new Uri(new Uri((await ReadOptionsAsync(publication)).ManifestUrl), "./").AbsoluteUri;
        distribution.Mode = "missing";
        var initial = await local.RunAsync([.. local.PrepareArguments(source).Where(value => value != "-PrepareOnly"),
            "-AllowHttp", "-SshUser", "release-test", "-ServerIp", "127.0.0.1", "-SshPort", ssh.Port.ToString()], false, ssh.Config);
        Assert.NotEqual(0, initial.Code);
        string directory = local.LatestDirectory();
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        Assert.True(record.RootElement.GetProperty("Phase").GetString() == "ManifestActivated", initial.Text);
        byte[] package = await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-win-x64.zip").Single());
        byte[] newer = publication.Sign(record.RootElement.GetProperty("Sequence").GetInt64() + 1, package);
        distribution.Mode = "healthy";
        var upload = await publication.UploadAsync(newer, package: package); string id = await publication.ConfirmAsync(upload.DraftId);
        Assert.True(await publication.ExecuteAsync()); Assert.True(await publication.ExecutePublicCheckAsync());
        var resumed = await local.RunAsync(["-Resume", Path.GetFileName(directory), "-SshUser", "release-test",
            "-ServerIp", "127.0.0.1", "-SshPort", ssh.Port.ToString()], false, ssh.Config);
        Assert.NotEqual(0, resumed.Code);
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(publication.Options.ReleaseRoot, "manifest.json")));
        using var superseded = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        Assert.Equal("Superseded", superseded.RootElement.GetProperty("Phase").GetString());
    }

    [Fact]
    public async Task PreparedRenewalReusesThePackageAndKeepsTheSourceUntouched()
    {
        await using var local = new PreparationFixture();
        var initial = await local.RunAsync(local.PrepareArguments()); Assert.True(initial.Code == 0, initial.Text);
        string directory = local.LatestDirectory(), id = Path.GetFileName(directory);
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"));
        var renewed = await local.RunAsync([.. local.PrepareArguments(), "-ResignRelease", id]); Assert.True(renewed.Code == 0, renewed.Text);
        string newer = local.LatestDirectory(); Assert.NotEqual(directory, newer);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        Assert.Equal(await File.ReadAllBytesAsync(Directory.GetFiles(directory, "*-win-x64.zip").Single()),
            await File.ReadAllBytesAsync(Directory.GetFiles(newer, "*-win-x64.zip").Single()));
        using var first = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "release-record.json")));
        using var second = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(newer, "release-record.json")));
        Assert.True(second.RootElement.GetProperty("Sequence").GetInt64() > first.RootElement.GetProperty("Sequence").GetInt64());
        Assert.False(File.Exists(local.NetworkGuard));
    }

    private static async Task<PortKiller.Updates.ManifestVerificationOptions> ReadOptionsAsync(PublishFixture fixture)
        => JsonSerializer.Deserialize(await File.ReadAllTextAsync(fixture.Options.VerificationConfigPath), PortKiller.Updates.ProtocolJsonContext.Default.ManifestVerificationOptions)!;
}
