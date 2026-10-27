using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.Regression;

internal static class UpdateMaintenanceChecks
{
    internal static async Task Results()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "PortKillMaintenance-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(sandbox, ".portkiller-update"), cache = Path.Combine(sandbox, "cache");
        Directory.CreateDirectory(root);
        try
        {
            string lockPath = Path.Combine(root, "lock");
            File.WriteAllText(lockPath, "");
            var journal = new UpdateJournal { Version = "1.0.2", PreviousVersion = "1.0.1", Token = Guid.NewGuid().ToString("N") };
            Task<UpdateCompletionNotice?> pending;
            using (var lease = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                pending = UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None, true);
                await Task.Delay(150);
                Program.Assert(!pending.IsCompleted, "安装器持锁期间读取了旧回执。");
                UpdateResult.Write(root, journal, "installed");
            }
            var result = await pending;
            Program.Assert(result is { IsError: false } && result.Message.Contains("1.0.2"), "未等待本次安装结果。");
            Program.Assert(await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None) == result,
                "尚未交给 UI 的结果没有保留供下次启动重试。");
            Program.Assert(await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.3", CancellationToken.None) == null,
                "新版本显示了旧版本的结果。");
            Program.Assert(UpdateMaintenance.MarkConsumed(sandbox, cache, result!, "1.0.2"), "UI 显示后无法确认回执。");
            Program.Assert(await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None) == null,
                "已确认显示的结果被重复提示。");
            journal.Token = Guid.NewGuid().ToString("N");
            UpdateResult.Write(root, journal, "rolled_back", "测试失败");
            result = await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.1", CancellationToken.None);
            Program.Assert(result is { IsError: true } && result.Message.Contains("恢复"), "回滚结果未提示。");
            Program.Assert(UpdateMaintenance.MarkConsumed(sandbox, cache, result!, "1.0.1"), "回滚回执无法标记已显示。");
            File.Delete(Path.Combine(root, "result.json"));
            File.WriteAllText(Path.Combine(root, "result.txt"), "更新成功：1.0.1");
            var legacy = await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.1", CancellationToken.None);
            Program.Assert(legacy != null && await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.1", CancellationToken.None) == legacy
                && UpdateMaintenance.MarkConsumed(sandbox, cache, legacy!, "1.0.1")
                && await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.1", CancellationToken.None) == null,
                "旧回执兼容、显示重试或一次性确认失败。");
            Program.Assert(await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None) == null
                && !UpdateMaintenance.MarkConsumed(sandbox, cache, legacy!, "1.0.2"), "旧版本成功回执被新版本接受。");
            File.WriteAllText(Path.Combine(root, "result.txt"), "已更新至 v1.0.2");
            var next = await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.2", CancellationToken.None);
            Program.Assert(next is { IsError: false } && next.OperationId != legacy!.OperationId,
                "后续版本复用了旧提示的已读标记。");
            File.WriteAllText(Path.Combine(root, "result.json"), "broken json");
            Program.Assert(await UpdateMaintenance.RunAsync(sandbox, cache, "1.0.1", CancellationToken.None) == null,
                "损坏回执阻止启动或回退到旧回执。");
        }
        finally { Directory.Delete(sandbox, true); }
    }
}
