using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PortKiller.UpdateAdmin.Tests;

namespace PortKiller.Release.Tests;

internal sealed class SshFixture : IAsyncDisposable
{
    private P4ChildProcess _process = null!;
    public int Port { get; private set; }
    public string Config { get; private set; } = "";
    public string Root { get; private set; } = "";
    public static async Task<SshFixture> StartAsync(PreparationFixture local, PublishFixture publication)
    {
        var fixture = new SshFixture { Root = Path.Combine(local.Root, "ssh") }; Directory.CreateDirectory(fixture.Root);
        string settings = Path.Combine(fixture.Root, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new { root = fixture.Root,
            @public = publication.Options.ReleaseRoot, verification = publication.Options.VerificationConfigPath,
            dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            tool = Path.ChangeExtension(PublishFixture.FindTool(), ".dll") }));
        string python = Environment.GetEnvironmentVariable("PORTKILL_RELEASE_SSH_PYTHON") ?? throw new InvalidOperationException("Configure PORTKILL_RELEASE_SSH_PYTHON for the isolated SSH checks.");
        string packages = Environment.GetEnvironmentVariable("PORTKILL_RELEASE_SSH_PACKAGES") ?? throw new InvalidOperationException("Configure PORTKILL_RELEASE_SSH_PACKAGES for the isolated SSH checks.");
        fixture._process = P4ChildProcess.Start(python,
            [Path.Combine(PreparationFixture.Repository, "tests", "PortKiller.Release.Tests", "ssh_fixture.py"), packages, settings],
            new Dictionary<string, string> { ["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
        try
        {
            string ready = Path.Combine(fixture.Root, "ready.json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready)) { await fixture._process.EnsureRunningAsync(); await Task.Delay(50, deadline.Token); }
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(ready)); fixture.Port = state.RootElement.GetProperty("port").GetInt32();
            string identity = Path.Combine(fixture.Root, "client-key");
            if (OperatingSystem.IsWindows())
            {
                using var user = WindowsIdentity.GetCurrent();
                var permissions = new FileSecurity(); permissions.SetOwner(user.User!); permissions.SetAccessRuleProtection(true, false);
                permissions.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(identity).SetAccessControl(permissions);
            }
            fixture.Config = Path.Combine(fixture.Root, "ssh-config");
            await File.WriteAllTextAsync(fixture.Config, "Host *\n  BatchMode yes\n  IdentitiesOnly yes\n  StrictHostKeyChecking yes\n  LogLevel ERROR\n"
                + "  IdentityFile \"" + identity.Replace('\\', '/') + "\"\n  UserKnownHostsFile \""
                + Path.Combine(fixture.Root, "known_hosts").Replace('\\', '/') + "\"\n");
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => _process == null ? ValueTask.CompletedTask : _process.DisposeAsync();
    public async Task<string> DiagnosticsAsync()
    {
        string path = Path.Combine(Root, "ssh-debug.log");
        if (!File.Exists(path)) return "No SSH diagnostics.";
        using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var lines = (await input.ReadToEndAsync()).Split('\n');
        return string.Join('\n', lines.Where(line => line.Contains("Received open") || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failure", StringComparison.OrdinalIgnoreCase) || line.Contains("not found", StringComparison.OrdinalIgnoreCase)).TakeLast(40))
            + "\n" + string.Join('\n', lines.TakeLast(20));
    }
}
