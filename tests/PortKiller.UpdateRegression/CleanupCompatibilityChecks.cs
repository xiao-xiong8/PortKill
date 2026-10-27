using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class CleanupCompatibilityChecks
{
    internal static async Task Run()
    {
        await LegacySuccessAndRetry();
        await TransientWorkerLock();
        await ConsecutiveVersions();
        await ReceiptBoundaries();
    }

    private static async Task TransientWorkerLock()
    {
        using var fixture = new Fixture();
        var (install, root, cache) = Setup(fixture);
        var previousSink = UpdateLog.Sink;
        try
        {
            // 第一次提示前、提示已读后都需在同一次启动重试短暂的安装器进程占用。
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var worker = Worker(fixture, root, cache, "0.1.2");
                Receipt(root, "更新成功：0.1.2");
                var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                UpdateLog.Sink = (phase, error) => { previousSink?.Invoke(phase, error); if (phase == "UpdateCleanup") attempted.TrySetResult(); };
                Task<UpdateCompletionNotice?> pending;
                using (var lease = new FileStream(Path.Combine(worker.Worker, "PortKiller.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    pending = UpdateMaintenance.RunAsync(install, cache, "0.1.2", default);
                    await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    That(!pending.IsCompleted, "A transient worker lock was not retried");
                }
                var notice = await pending;
                That(!Directory.Exists(worker.Worker) && !File.Exists(worker.Package), "Transient lock needed another application restart");
                if (iteration == 0)
                    That(notice != null && UpdateMaintenance.MarkConsumed(install, cache, notice, "0.1.2"), "Transient cleanup lost its notice");
                else That(notice == null, "Cleanup retry redisplayed an acknowledged notice");
            }
        }
        finally { UpdateLog.Sink = previousSink; }
    }

    private static async Task LegacySuccessAndRetry()
    {
        using var fixture = new Fixture();
        var (install, root, cache) = Setup(fixture);
        var old = Worker(fixture, root, cache, "0.1.2");
        var future = Worker(fixture, root, cache, "0.1.3");
        Directory.SetLastWriteTimeUtc(future.Worker, DateTime.UtcNow.AddDays(-8));
        string recovery = Path.Combine(root, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recovery); File.WriteAllText(Path.Combine(recovery, "binary"), "old recovery");
        Receipt(root, "更新成功：0.1.2");
        using (var lease = new FileStream(Path.Combine(root, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            That(await Maintain() == null && Directory.Exists(old.Worker) && File.Exists(old.Package), "Legacy cleanup ignored installer lock");
        }
        File.WriteAllText(Path.Combine(root, "transaction.json"), "incomplete");
        That(await Maintain() == null && Directory.Exists(old.Worker), "Legacy cleanup deleted an unfinished transaction");
        File.Delete(Path.Combine(root, "transaction.json"));
        UpdateCompletionNotice? notice;
        // ZIP 占用时保留工作清单，提示已读后仍可重试。
        using (var lease = new FileStream(old.Package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            notice = await Maintain();
            That(notice is { IsError: false } && Directory.Exists(old.Worker)
                && File.Exists(Path.Combine(old.Worker, "manifest.json")), "Legacy cache lock lost cleanup metadata");
            That(UpdateMaintenance.MarkConsumed(install, cache, notice!, "0.1.2"), "Legacy result could not be acknowledged");
        }
        // 安装器 EXE 占用时仍保留清单，下次启动完成删除。
        using (var lease = new FileStream(Path.Combine(old.Worker, "PortKiller.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            That(await Maintain() == null && !File.Exists(old.Package) && Directory.Exists(old.Worker)
                && File.Exists(Path.Combine(old.Worker, "manifest.json")), "Legacy worker lock lost manifest or repeated notice");
        }
        That(await Maintain() == null && !Directory.Exists(old.Worker) && !Directory.Exists(recovery)
            && !Directory.Exists(Path.Combine(root, "backup")) && !File.Exists(old.Package + ".manifest")
            && !File.Exists(old.Package + ".partial"), "Legacy leftovers not retried after acknowledgement");
        That(Directory.Exists(future.Worker) && File.Exists(future.Package) && File.Exists(future.Package + ".partial"),
            "A future update candidate was deleted");
        Preserved(install, root);
        Task<UpdateCompletionNotice?> Maintain() => UpdateMaintenance.RunAsync(install, cache, "0.1.2.0", default);
    }

    private static async Task ConsecutiveVersions()
    {
        using var fixture = new Fixture();
        var (install, root, cache) = Setup(fixture);
        // 历史遗留清单过期后仍可清理，但必须验证签名、同源和版本。
        var legacy = Worker(fixture, root, cache, "0.1.2", expired: true);
        Receipt(root, "更新成功：0.1.2");
        var notice = await UpdateMaintenance.RunAsync(install, cache, "0.1.2", default);
        That(notice != null && !Directory.Exists(legacy.Worker) && !File.Exists(legacy.Package), "Expired completed legacy package leaked");

        // 模拟旧残留被占用，直到后续版本成功后才释放；新回执已覆盖旧文本。
        legacy = Worker(fixture, root, cache, "0.1.2");
        for (int revision = 3; revision <= 5; revision++)
        {
            string version = "0.1." + revision;
            var current = Worker(fixture, root, cache, version);
            var future = Worker(fixture, root, cache, "0.2.0");
            Directory.CreateDirectory(Path.Combine(root, "backup"));
            var journal = new UpdateJournal { Version = version, PreviousVersion = "0.1." + (revision - 1),
                Token = Guid.NewGuid().ToString("N"), WorkerName = Path.GetFileName(current.Worker),
                PackageSha256 = Path.GetFileNameWithoutExtension(current.Package) };
            UpdateResult.Write(root, journal, "installed");
            notice = await UpdateMaintenance.RunAsync(install, cache, version, default);
            That(notice is { IsError: false } && notice.Message.Contains(version)
                && !Directory.Exists(legacy.Worker) && !File.Exists(legacy.Package)
                && !Directory.Exists(current.Worker) && !File.Exists(current.Package)
                && !Directory.Exists(Path.Combine(root, "backup")) && File.Exists(future.Package),
                "Consecutive upgrade cleanup failed at " + version);
            That(UpdateMaintenance.MarkConsumed(install, cache, notice!, version)
                && await UpdateMaintenance.RunAsync(install, cache, version, default) == null,
                "Consecutive upgrade notice was lost or repeated at " + version);
        }
        Preserved(install, root);
    }

    private static async Task ReceiptBoundaries()
    {
        foreach (string text in new[] { "更新成功：0.1.1", "更新成功：错误版本", "更新成功但尚未完成", "未知错误" })
        {
            using var fixture = new Fixture();
            var (install, root, cache) = Setup(fixture);
            var worker = Worker(fixture, root, cache, "0.1.3");
            Receipt(root, text);
            var notice = await UpdateMaintenance.RunAsync(install, cache, "0.1.2", default);
            That(Directory.Exists(worker.Worker) && File.Exists(worker.Package) && Directory.Exists(Path.Combine(root, "backup")),
                "Unmatched/unrecognized legacy result authorized cleanup: " + text);
            That(notice == null || notice.IsError, "Invalid legacy result reported success: " + text);
        }
        foreach (string text in new[] { "版本 0.1.3 安装失败，已恢复旧版本。", "检测到中断的更新，已恢复可用版本。" })
        {
            using var fixture = new Fixture();
            var (install, root, cache) = Setup(fixture);
            var worker = Worker(fixture, root, cache, "0.1.3");
            Receipt(root, text);
            var notice = await UpdateMaintenance.RunAsync(install, cache, "0.1.2", default);
            That(notice is { IsError: true } && !Directory.Exists(worker.Worker)
                && !Directory.Exists(Path.Combine(root, "backup")) && File.Exists(worker.Package)
                && File.Exists(worker.Package + ".manifest"), "Legacy recovery did not retain its retry package");
        }
        foreach (string json in new[] { "broken json", "{\"Schema\":999}" })
        {
            using var fixture = new Fixture();
            var (install, root, cache) = Setup(fixture);
            var worker = Worker(fixture, root, cache, "0.1.2");
            Receipt(root, "更新成功：0.1.2"); File.WriteAllText(Path.Combine(root, "result.json"), json);
            That(await UpdateMaintenance.RunAsync(install, cache, "0.1.2", default) == null
                && Directory.Exists(worker.Worker) && File.Exists(worker.Package)
                && Directory.Exists(Path.Combine(root, "backup")), "Broken/future receipt fell back to legacy cleanup");
        }
        using (var fixture = new Fixture())
        {
            var (install, root, cache) = Setup(fixture);
            var worker = Worker(fixture, root, cache, "0.1.2");
            File.WriteAllText(Path.Combine(worker.Worker, "manifest.json"), "untrusted");
            Receipt(root, "更新成功：0.1.2");
            await UpdateMaintenance.RunAsync(install, cache, "0.1.2", default);
            That(File.Exists(worker.Package), "Untrusted worker manifest authorized cache deletion");
        }
    }

    private static (string Install, string Root, string Cache) Setup(Fixture fixture)
    {
        string install = Path.Combine(fixture.Root, "install"), root = Path.Combine(install, ".portkiller-update"),
            cache = Path.Combine(fixture.Root, "cache");
        Directory.CreateDirectory(root); Directory.CreateDirectory(cache); Directory.CreateDirectory(Path.Combine(root, "backup"));
        File.WriteAllText(Path.Combine(root, "lock"), "");
        File.WriteAllText(Path.Combine(root, "backup", "PortKiller.exe"), "old binary");
        File.WriteAllText(Path.Combine(install, "update-settings.json"), JsonSerializer.Serialize(fixture.Options, UpdateJsonContext.Default.UpdateOptions));
        foreach (string file in UpdateInstaller.Files) File.WriteAllText(Path.Combine(install, file), "installed binary");
        File.WriteAllText(Path.Combine(install, "config.json"), "user settings");
        return (install, root, cache);
    }

    private static (string Worker, string Package) Worker(Fixture fixture, string root, string cache, string version, bool expired = false)
    {
        string worker = Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(worker, "stage"));
        byte[] bytes = Encoding.UTF8.GetBytes("package " + version);
        string package = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".zip");
        byte[] envelope = fixture.Envelope(version, 1, bytes);
        if (expired)
        {
            var manifest = UpdateManifest.Verify(envelope, fixture.Options);
            manifest.PublishedUtc = DateTimeOffset.UtcNow.AddDays(-60); manifest.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-30);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
            envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate { Payload = Convert.ToBase64String(payload),
                Signature = Convert.ToBase64String(fixture.Key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
                ProtocolJsonContext.Default.SignedUpdate);
        }
        File.WriteAllBytes(package, bytes); File.WriteAllBytes(package + ".manifest", envelope); File.WriteAllText(package + ".partial", "partial");
        File.WriteAllBytes(Path.Combine(worker, "manifest.json"), envelope);
        File.WriteAllBytes(Path.Combine(worker, "package.zip"), bytes);
        File.WriteAllText(Path.Combine(worker, "PortKiller.exe"), "worker binary");
        File.WriteAllText(Path.Combine(worker, "stage", "PortKiller.exe"), "new binary");
        return (worker, package);
    }

    private static void Receipt(string root, string text)
    {
        File.WriteAllText(Path.Combine(root, "result.txt"), text);
        // 统一模拟终态回执发生在工作目录创建之后，避免低精度文件系统时间影响断言。
        File.SetLastWriteTimeUtc(Path.Combine(root, "result.txt"), DateTime.UtcNow.AddSeconds(1));
    }

    private static void Preserved(string install, string root)
    {
        That(File.ReadAllText(Path.Combine(install, "config.json")) == "user settings"
            && UpdateInstaller.Files.All(file => File.ReadAllText(Path.Combine(install, file)) == "installed binary")
            && File.Exists(Path.Combine(install, "update-settings.json")) && File.Exists(Path.Combine(root, "lock"))
            && File.Exists(Path.Combine(root, "result.txt")), "Cleanup damaged binaries, settings, lock or result record");
    }
}
