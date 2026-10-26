using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublisherProcessIdentityTests
{
    [Theory]
    [InlineData("worker")]
    [InlineData("worker with spaces")]
    [InlineData("worker (nested) ) name")]
    public void LinuxStartIdentityReadsField22AfterTheEntireProcessName(string name)
    {
        string fields = "S " + string.Join(' ', Enumerable.Range(4, 18)) + " 987654321 23 24";
        Assert.Equal(987654321, PublisherProcessIdentity.ParseLinuxStartTime("123 (" + name + ") " + fields));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123 (worker) S 1 2")]
    [InlineData("123 (worker) S 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 invalid")]
    public void IncompleteIdentityIsRejected(string stat)
        => Assert.Throws<InvalidDataException>(() => PublisherProcessIdentity.ParseLinuxStartTime(stat));

    [Fact]
    public void LiveOwnerTokenIsStableAcrossIndependentProcessObjects()
    {
        using var first = Process.GetCurrentProcess();
        using var second = Process.GetProcessById(first.Id);
        long identity = PublisherProcessIdentity.ReadStartToken(first);
        Assert.True(identity > 0);
        Assert.Equal(identity, PublisherProcessIdentity.ReadStartToken(second));
        if (OperatingSystem.IsLinux())
            Assert.Equal(PublisherProcessIdentity.ParseLinuxStartTime(File.ReadAllText("/proc/" + first.Id.ToString(CultureInfo.InvariantCulture) + "/stat")), identity);
        else
            Assert.Equal(first.StartTime.ToUniversalTime().Ticks, identity);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerSelfCheckRunsTheProductionToolWithoutTouchingPublicationData(bool missingConfig)
    {
        await using var fixture = await PublishFixture.StartAsync();
        string worker = PublishFixture.FindExecutable("src/Services/PortKiller.UpdatePublisher.Worker", "PortKiller.UpdatePublisher.Worker");
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--self-check-tool-owner");
        // A self-check must run without the administration configuration or its writable data mount.
        start.Environment["PORTKILL_ADMIN_CONFIG"] = Path.Combine(fixture.App.Root, "unmounted-admin.json");
        start.Environment["Administration__DataRoot"] = "relative-and-invalid-for-hosting";
        start.Environment["UpdateTool__ExecutablePath"] = fixture.Options.ExecutablePath;
        start.Environment["UpdateTool__VerificationConfigPath"] = missingConfig
            ? Path.Combine(fixture.App.Root, "missing.json") : fixture.Options.VerificationConfigPath;
        start.Environment["UpdateTool__ReleaseRoot"] = fixture.Options.ReleaseRoot;
        string state = Path.Combine(fixture.App.Root, ".public-update-state");
        Directory.CreateDirectory(state);
        using var locked = new FileStream(Path.Combine(state, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        string json = await output;
        Assert.Equal(missingConfig ? 1 : 0, child.ExitCode);
        Assert.True(string.IsNullOrEmpty(await error), await error);
        using var result = JsonDocument.Parse(json);
        Assert.Equal(2, result.RootElement.GetProperty("SelfCheckSchema").GetInt32());
        Assert.Equal(!missingConfig, result.RootElement.GetProperty("Succeeded").GetBoolean());
        Assert.False(result.RootElement.GetProperty("HasCurrent").GetBoolean());
        Assert.False(File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(state, "receipt.json")));
    }
}
