using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PortKiller.UpdateAdmin.Tests;

internal sealed class P4ChildProcess : IAsyncDisposable
{
    public Process Process { get; }
    private readonly Task<string> _output, _error;
    private P4ChildProcess(Process process)
    { Process = process; _output = CaptureAsync(process.StandardOutput); _error = CaptureAsync(process.StandardError); }

    public static P4ChildProcess Worker(PublishFixture fixture)
    {
        var tool = fixture.Options;
        return Start(PublishFixture.FindExecutable("src/Services/PortKiller.UpdatePublisher.Worker", "PortKiller.UpdatePublisher.Worker"),
        ["--Administration:DataRoot", fixture.App.Root, "--UpdateTool:Enabled", "true",
            "--UpdateTool:ExecutablePath", tool.ExecutablePath, "--UpdateTool:VerificationConfigPath", tool.VerificationConfigPath,
            "--UpdateTool:ReleaseRoot", tool.ReleaseRoot, "--UpdateTool:TimeoutSeconds", tool.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--UpdateTool:PublicCheckTimeoutSeconds", tool.PublicCheckTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--UpdateTool:MaximumOutputBytes", tool.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
            "--Logging:LogLevel:Default", "Warning"]);
    }

    public static P4ChildProcess Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment.Clear();
        foreach (string name in OperatingSystem.IsWindows() ? new[] { "SYSTEMROOT", "WINDIR", "TEMP", "TMP" } : new[] { "TMPDIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (string value in arguments) start.ArgumentList.Add(value);
        return new(Process.Start(start) ?? throw new InvalidOperationException("Test child did not start."));
    }

    public async Task EnsureRunningAsync()
    {
        if (Process.HasExited)
            throw new InvalidOperationException("Worker exited before completing the task: " + await ExitAsync());
    }
    public async Task<string> ExitAsync()
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return await _output + await _error;
    }
    public async Task KillAsync(bool entireTree)
    {
        if (!Process.HasExited) Process.Kill(entireTree);
        await ExitAsync();
    }
    public async ValueTask DisposeAsync() { await KillAsync(true); Process.Dispose(); }

    private static async Task<string> CaptureAsync(StreamReader reader)
    {
        var captured = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (captured.Length < 65_536) captured.Append(buffer, 0, Math.Min(count, 65_536 - captured.Length));
        return captured.ToString();
    }
}
