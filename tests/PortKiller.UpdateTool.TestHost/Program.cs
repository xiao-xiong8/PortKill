using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateTool;
using PortKiller.Updates;

// Fault controls exist only in this test executable; the production tool exposes no fault switches.
if (args is ["publish-worker", var workerConfig])
{
    var settings = JsonSerializer.Deserialize<TestPublisherConfiguration>(await File.ReadAllTextAsync(workerConfig))!;
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(new AdminOptions { DataRoot = settings.DataRoot });
    services.AddSingleton(settings.Tool);
    services.AddSingleton<UpdateToolClient>();
    services.AddAdminDatabase(new AdminOptions { DataRoot = settings.DataRoot });
    services.AddAdminWorkerServices();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PublishTaskExecutor>().ExecuteOnceAsync("process-test-worker", default);
    return;
}

if (args.Contains("--portkill-test-child-hang", StringComparer.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

int configIndex = Array.IndexOf(args, "--config");
if (configIndex < 0 || configIndex + 1 >= args.Length) return;
using var config = JsonDocument.Parse(await File.ReadAllTextAsync(args[configIndex + 1]));
string mode = config.RootElement.TryGetProperty("testMode", out var testMode) ? testMode.GetString() ?? "" : "";
if (mode is "publish-fault" or "publish-output")
{
    try
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index + 1 < args.Length; index += 2) flags.Add(args[index], args[index + 1]);
        using var owner = ToolProcessGuard.FromArguments(flags, args[0] == "public-check" ? 605 : 60);
        Action? ensureOwner = owner is null ? null : owner.Check;
        var verification = JsonSerializer.Deserialize(await File.ReadAllTextAsync(flags["--config"]),
            ProtocolJsonContext.Default.ManifestVerificationOptions)!;
        if (args[0] == "public-check")
        {
            Console.WriteLine(JsonSerializer.Serialize(await PublicReleaseChecker.CheckAsync(verification,
                flags.GetValueOrDefault("--expected"), default)));
            return;
        }
        if (mode == "publish-output" && args[0] == "activate" && !config.RootElement.GetProperty("commitBeforeOutput").GetBoolean())
        { Console.Write(new string('x', 2_000_000)); return; }
        var store = new ReleaseStore(flags["--root"], verification);
        object? result = args[0] switch
        {
            "activate" => store.Activate(flags["--manifest"], flags["--package"], flags["--expected"], phase =>
            {
                if (mode != "publish-fault") return;
                if (phase != config.RootElement.GetProperty("faultPhase").GetString()) return;
                ReleaseStore.DurableWrite(config.RootElement.GetProperty("signalPath").GetString()!,
                    JsonSerializer.SerializeToUtf8Bytes(new { pid = Environment.ProcessId, phase }));
                if (config.RootElement.TryGetProperty("continuePath", out var gate))
                    while (!File.Exists(gate.GetString()!)) Thread.Sleep(20);
                else Thread.Sleep(Timeout.Infinite);
            }, flags["--manifest-sha256"], flags["--package-sha256"], ensureOwner),
            "inspect" => store.Inspect(false, ensureOwner),
            "recover" => store.Inspect(true, ensureOwner),
            _ => throw new ArgumentException("Unsupported test command.")
        };
        if (mode == "publish-output" && args[0] == "activate") Console.Write(new string('x', 2_000_000));
        else Console.WriteLine(JsonSerializer.Serialize(result));
    }
    catch (Exception exception)
    {
        if (config.RootElement.TryGetProperty("diagnosticPath", out var diagnostic))
            await File.WriteAllTextAsync(diagnostic.GetString()!, exception.ToString());
        string code = exception is IOException ? "io_failure" : "internal_error";
        Console.WriteLine(JsonSerializer.Serialize(new { schema = 1, code, message = "Injected tool failure." }));
        Environment.ExitCode = 1;
    }
    return;
}
if (mode == "output")
{
    Console.Write(new string('x', 2_000_000));
    return;
}
if (mode == "spawn")
{
    string pidPath = config.RootElement.GetProperty("pidPath").GetString()!;
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--portkill-test-child-hang" }
    })!;
    await File.WriteAllTextAsync(pidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

internal sealed record TestPublisherConfiguration(string DataRoot, UpdateToolOptions Tool);
