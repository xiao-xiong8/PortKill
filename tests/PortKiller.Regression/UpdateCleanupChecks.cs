using System.Text.Json;
using System.Diagnostics;
using PortKiller.Updates;

namespace PortKiller.Regression;

internal static class UpdateCleanupChecks
{
    internal static async Task Run()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "PortKillCleanup-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, ".portkiller-update"), cache = Path.Combine(sandbox, "cache");
        Directory.CreateDirectory(root); Directory.CreateDirectory(cache);
        try
        {
            File.WriteAllText(Path.Combine(root, "lock"), "");
            string worker = "worker-" + Guid.NewGuid().ToString("N");
            string recovery = "recovery-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(root, recovery));
            File.WriteAllText(Path.Combine(root, recovery, "binary"), "recovery");
            Directory.CreateDirectory(Path.Combine(root, worker, "stage"));
            File.WriteAllText(Path.Combine(root, worker, "stage", "binary"), "new");
            Directory.CreateDirectory(Path.Combine(root, "backup"));
            File.WriteAllText(Path.Combine(root, "backup", "binary"), "old");
            string digest = new('a', 64), ready = Path.Combine(cache, new string('b', 64) + ".zip");
            string installed = Path.Combine(cache, digest + ".zip");
            File.WriteAllText(installed, "zip"); File.WriteAllText(installed + ".manifest", "manifest");
            File.WriteAllText(ready, "pending update");
            string settings = Path.Combine(sandbox, "config.json"); File.WriteAllText(settings, "preserved");
            var journal = new UpdateJournal { Version = "1.0.2", PreviousVersion = "1.0.1",
                Token = Guid.NewGuid().ToString("N"), WorkerName = worker, PackageSha256 = digest };
            UpdateResult.Write(root, journal, "installed", recoveryWorkerName: recovery);
            File.WriteAllText(Path.Combine(root, "transaction.json"), JsonSerializer.Serialize(journal, UpdateJsonContext.Default.UpdateJournal));
            await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None);
            Program.Assert(Directory.Exists(Path.Combine(root, worker)) && File.Exists(installed), "未完成事务的文件被清理。");
            File.Delete(Path.Combine(root, "transaction.json"));
            using (var locked = new FileStream(Path.Combine(root, worker, "stage", "binary"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None);
                Program.Assert(Directory.Exists(Path.Combine(root, worker)), "锁定目录清理行为不正确。");
            }
            await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None);
            Program.Assert(!Directory.Exists(Path.Combine(root, worker)) && !Directory.Exists(Path.Combine(root, recovery))
                && !Directory.Exists(Path.Combine(root, "backup"))
                && !File.Exists(installed) && !File.Exists(installed + ".manifest") && File.Exists(ready)
                && File.ReadAllText(settings) == "preserved", "完成回执后的清理、重试或文件保护失败。");

            string old = Path.Combine(cache, new string('c', 64) + ".zip");
            File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            string partial = old + ".partial"; File.WriteAllText(partial, "partial"); File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(ready, DateTime.UtcNow.AddDays(-8));
            UpdateCleanup.Cache(cache, ready);
            Program.Assert(File.Exists(ready) && !File.Exists(old) && !File.Exists(partial), "缓存期限清理误删当前候选或未删除过期文件。");

            // 本地损坏回执不能指定任意目录或文件作为清理路径。
            var result = new UpdateResult { OperationId = Guid.NewGuid().ToString("N"), WorkerName = "../cache",
                PackageSha256 = "../config.json", Outcome = "installed" };
            UpdateCleanup.Installation(sandbox, cache, result);
            Program.Assert(File.Exists(ready) && File.Exists(settings), "清理接受了任意路径。");

            if (OperatingSystem.IsWindows())
            {
                string outside = Path.Combine(sandbox, "protected"), linkedWorker = Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "keep.txt"), "protected");
                Directory.CreateDirectory(linkedWorker);
                string link = Path.Combine(linkedWorker, "junction");
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Program.Assert(process.ExitCode == 0, "无法创建用于验证清理边界的 NTFS junction。");
                try
                {
                    UpdateCleanup.Installation(sandbox, cache, new UpdateResult { WorkerName = Path.GetFileName(linkedWorker) });
                    Program.Assert(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "protected"
                        && Directory.Exists(link), "清理跟随了重解析点。");
                }
                finally { Directory.Delete(link); }
            }
        }
        finally { Directory.Delete(sandbox, true); }
    }
}
