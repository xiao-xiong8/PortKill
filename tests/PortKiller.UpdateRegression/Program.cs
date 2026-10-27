using System.Diagnostics;
using PortKiller.UpdateRegression;

try
{
    int production = Array.IndexOf(args, "--production-check");
    if (production >= 0) { await ProductionFeedChecks.Online(Path.GetFullPath(args[production + 1])); return 0; }
    int prepared = Array.IndexOf(args, "--prepared-release");
    if (prepared >= 0) { await ProductionFeedChecks.PreparedRelease(Path.GetFullPath(args[prepared + 1]), Path.GetFullPath(args[prepared + 2])); return 0; }
    if (args.Contains("--key-lifecycle")) { await KeyLifecycleChecks.Run(); return 0; }
    if (args.Contains("--manual-only")) { await ServerChecks.ManualLatency(); return 0; }
    if (args.Contains("--desktop-update-only")) { await ClientChecks.StorageAndReadyDuringOutage(); Console.WriteLine("PASS 桌面空间预检查与就绪更新断网保留"); return 0; }
    if (args.Contains("--cleanup-only")) { await CleanupCompatibilityChecks.Run(); Console.WriteLine("PASS 历史回执清理、文件占用重试与连续版本升级"); return 0; }
    var checks = new (string Name, Func<Task> Run)[] {
        ("线上默认源、公钥加载与显式配置覆盖", ProductionFeedChecks.DefaultsAndOverrides),
        ("手动无本地冷却与 30 秒静默后台检查", ClientChecks.ManualChecksAndSilentPolling),
        ("空间不足下载前拒绝、同盘预算与后台断网保留就绪目标", ClientChecks.StorageAndReadyDuringOutage),
        ("历史回执清理、文件占用重试与连续版本升级", CleanupCompatibilityChecks.Run),
        ("未发布状态、实时首次发布、真实说明与确认下载", ProductionFeedChecks.EmptyFeedThenRealtimePublication),
        ("清单撤回清理旧就绪状态和 ETag，恢复后复验缓存", ProductionFeedChecks.MissingFeedClearsStaleReadyAndEtag),
        ("系统代理默认兼容、显式直连清单/下载/SSE", TransportChecks.ProxySelection),
        ("未发布或无有效清单时 SSE 首包立即刷新并持续接收发布", ServerChecks.EmptyFeedStartsImmediately),
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
    if (!args.Contains("--perf-only") && !args.Contains("--publisher-only") && !args.Contains("--aot-only") && !args.Contains("--historical-aot-only"))
        foreach (var check in checks) { var watch = Stopwatch.StartNew(); await check.Run().WaitAsync(TimeSpan.FromMinutes(3)); Console.WriteLine($"PASS {check.Name} ({watch.Elapsed.TotalSeconds:F2}s)"); }
    int aot = Array.IndexOf(args, "--aot");
    if (aot >= 0) await AotChecks.Install(Path.GetFullPath(args[aot + 1]), Path.GetFullPath(args[aot + 2]));
    for (int historical = 0; historical < args.Length; historical++)
        if (args[historical] == "--historical-aot")
            await AotChecks.HistoricalThenCurrent(Path.GetFullPath(args[historical + 1]),
                Path.GetFullPath(args[historical + 2]), Path.GetFullPath(args[historical + 3]), args[historical + 4]);
    int publisher = Array.IndexOf(args, "--publisher");
    if (publisher >= 0) await PublisherChecks.Run(Directory.GetCurrentDirectory(), Path.GetFullPath(args[publisher + 1]));
    if (args.Contains("--perf") || args.Contains("--perf-only")) await ServerChecks.DiscoveryLatency(100);
    Console.WriteLine("全部所选更新回归通过。"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
