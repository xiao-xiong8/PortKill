using PortKiller.Updates;
using System.Security.Cryptography.X509Certificates;

// This executable is never distributed. Each process, including copied workers, uses a dedicated sandbox.
string sandbox = Path.GetFullPath(Environment.GetEnvironmentVariable("PORTKILLER_PROBE_ROOT") ?? throw new InvalidOperationException("Probe sandbox required"));
if (!File.Exists(Path.Combine(sandbox, "probe-sandbox.marker"))) throw new InvalidOperationException("Not a probe sandbox");
UpdateTrustStore.ProbePath = Path.Combine(sandbox, "trust.json");
UpdateLog.Sink = (phase, ex) => File.AppendAllText(Path.Combine(sandbox, "probe.log"), $"{phase}: {ex}\n");
string mode = File.Exists(Path.Combine(sandbox, "mode")) ? File.ReadAllText(Path.Combine(sandbox, "mode")) : "success";
UpdateInstaller.AfterReplaceForTesting = index =>
{
    if (mode == "interrupted" && index == 0) Environment.Exit(91);
    if (mode == "storage-failure" && index == 0) throw new IOException("Simulated storage failure after first replacement");
};
if (args.Length == 3 && args[0] == "--apply-update") return UpdateInstaller.RunWorker(args[1], int.Parse(args[2]));
if (args.Length == 3 && args[0] == "--recover-update") return UpdateInstaller.RunRecoveryWorker(args[1], int.Parse(args[2]));
if (!UpdateInstaller.RecoverBeforeStartup(AppContext.BaseDirectory, args)) return 0;
if (args.Length == 1 && args[0] == "--check-and-install")
{
    await ReportAsync("started");
    var updates = new UpdateService(UpdateOptions.Load(AppContext.BaseDirectory), Path.Combine(sandbox, "cache"),
        installDirectory: AppContext.BaseDirectory);
    await updates.CheckAsync(CancellationToken.None, download: false);
    if (updates.Snapshot.Stage != UpdateStage.Available) throw new InvalidOperationException("Expected a newer published version.");
    await updates.DownloadAvailableAsync(CancellationToken.None);
    using var installation = updates.TryAcquireInstallation() ?? throw new InvalidOperationException("Verified download required.");
    await UpdateInstaller.ArmAsync(AppContext.BaseDirectory, updates.ReadyPackage!, CancellationToken.None);
    return 0;
}
if (args.Length == 2 && args[0] == "--install")
{
    try { await UpdateInstaller.ArmAsync(AppContext.BaseDirectory, args[1], CancellationToken.None); return 0; }
    catch (UnauthorizedAccessException ex) { Console.Error.WriteLine(ex.GetType().Name); return 5; }
}
if (args.Length == 2 && args[0] == "--update-health")
{
    if (mode == "unhealthy") return 3;
    if (mode == "health-timeout") await Task.Delay(TimeSpan.FromSeconds(60));
    UpdateInstaller.ConfirmHealth();
}
File.WriteAllText(Path.Combine(sandbox, "launched-version"), UpdateService.CurrentVersion);
string cache = Path.Combine(sandbox, "cache");
var notice = await UpdateMaintenance.RunAsync(AppContext.BaseDirectory, cache,
    UpdateService.CurrentVersion, CancellationToken.None, args.Length == 2 && args[0] == "--update-health");
if (notice != null)
{
    File.AppendAllText(Path.Combine(sandbox, "notices.txt"), notice.Message + "\n");
    UpdateMaintenance.MarkConsumed(AppContext.BaseDirectory, cache, notice, UpdateService.CurrentVersion);
}
await ReportAsync("started");
if (notice?.Outcome is { } outcome) await ReportAsync(outcome);
File.WriteAllText(Path.Combine(sandbox, "maintenance-complete"), "done");
return 0;

async Task ReportAsync(string result)
{
    // Test-only certificate pinning and identity paths never affect the real desktop application.
    if (Environment.GetEnvironmentVariable("PORTKILLER_PROBE_REPORT_CERTIFICATE") is not { } certificatePath) return;
    var options = UpdateOptions.Load(AppContext.BaseDirectory);
    if (options.GetClientReportUri() is not { } reportUri) throw new InvalidOperationException("Report endpoint required.");
    using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
    using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, received, _, _) => received?.GetCertHashString() == certificate.GetCertHashString() };
    var errors = new List<string>();
    using var reports = new ClientReportService(reportUri, store: new ClientIdentityStore(Path.Combine(sandbox, "identity.json"),
        AppContext.BaseDirectory), handler: handler, log: errors.Add);
    await reports.ReportAsync(UpdateService.CurrentVersion, result);
    if (errors.Count != 0) throw new InvalidOperationException("Client report failed: " + string.Join(",", errors));
}
