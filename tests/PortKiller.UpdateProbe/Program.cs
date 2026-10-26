using PortKiller.Updates;

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
File.WriteAllText(Path.Combine(sandbox, "maintenance-complete"), "done");
return 0;
