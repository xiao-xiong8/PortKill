using System.Diagnostics;
using PortKiller.UpdateRegression;

try
{
    if (args.Contains("--key-lifecycle")) { await KeyLifecycleChecks.Run(); return 0; }
    if (args.Contains("--manual-only")) { await ServerChecks.ManualLatency(); return 0; }
    var checks = new (string Name, Func<Task> Run)[] {
        ("A/B/304、失败恢复、续签缓存与安装租约", ClientChecks.IdentityAndLease),
        ("忙时合并、自动开关与有界检查意图", ClientChecks.PendingAndSwitch),
        ("后台执行边界与 Retry-After", ClientChecks.BackgroundAndRetryAfter),
        ("最后一次短 Retry-After 保留并在到期后自动补查", ClientChecks.FinalRetryAfterIsRetained),
        ("SSE CRLF、多行和解析上限", ClientChecks.EventBounds),
        ("暂停不被通知恢复，继续后处理待检查版本", ClientChecks.PauseRetainsNotification),
        ("SSE 畸形/断开重连及 404 轮询降级", ClientChecks.ReconnectAndFallback),
        ("发布各提交点故障恢复、CAS 和防倒退", StoreChecks.RecoveryAndConflicts),
        ("服务端并发发布排他锁", StoreChecks.ConcurrentLock),
        ("真实 SSE、无效清单、重启恢复与 1000 连接阶梯", ServerChecks.NotificationsAndLoad)
    };
    if (!args.Contains("--perf-only") && !args.Contains("--publisher-only"))
        foreach (var check in checks) { var watch = Stopwatch.StartNew(); await check.Run().WaitAsync(TimeSpan.FromMinutes(3)); Console.WriteLine($"PASS {check.Name} ({watch.Elapsed.TotalSeconds:F2}s)"); }
    int aot = Array.IndexOf(args, "--aot");
    if (aot >= 0) await AotChecks.Install(Path.GetFullPath(args[aot + 1]), Path.GetFullPath(args[aot + 2]));
    int publisher = Array.IndexOf(args, "--publisher");
    if (publisher >= 0) await PublisherChecks.Run(Directory.GetCurrentDirectory(), Path.GetFullPath(args[publisher + 1]));
    if (args.Contains("--perf") || args.Contains("--perf-only")) await ServerChecks.DiscoveryLatency(100);
    Console.WriteLine("全部所选更新回归通过。"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
