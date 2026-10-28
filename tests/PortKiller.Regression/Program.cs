using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.Services;
using PortKiller.ViewModels;

namespace PortKiller.Regression;

/// <summary>独立回归程序；只创建自有网络端点，不终止用户进程、不修改用户配置。</summary>
internal static class Program
{
    /// <summary>记录通过的行为检查数量。</summary>
    private static int _passed;

    /// <summary>任一断言失败即返回非零，不再无条件宣称全部通过。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--owned-child")) { Thread.Sleep(30000); return 0; }
        int holdIndex = Array.IndexOf(args, "--hold-single-instance");
        if (holdIndex >= 0)
        {
            string mutexName = holdIndex + 1 < args.Length ? args[holdIndex + 1] : string.Empty;
            if (string.IsNullOrWhiteSpace(mutexName) || !SingleInstanceGuard.TryAcquireForTest(mutexName)) return 2;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Thread.Sleep(600);
            SingleInstanceGuard.Release();
            return 0;
        }
        try
        {
            int dpiProbe = Array.IndexOf(args, "--update-dpi-probe");
            if (dpiProbe >= 0) { UpdateDpiProbe.Run(args[dpiProbe + 1], dpiProbe + 2 < args.Length ? args[dpiProbe + 2] : "badge"); return 0; }
            int probeIndex = Array.IndexOf(args, "--application-name-probe");
            if (probeIndex >= 0)
            { ApplicationNameProbe.Run(int.Parse(args[probeIndex + 1]), args[probeIndex + 2]).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--application-name-ui"))
            {
                ApplicationNameChecks.WindowsIntegration().GetAwaiter().GetResult();
                Console.WriteLine("PASS 应用名称宿主的 Win32 身份、路径与文件版本信息");
                UiChecks.Run(true); return 0;
            }
            if (args.Contains("--ui-only")) { UiChecks.Run(); Console.WriteLine("窗口验证通过。"); return 0; }
            if (args.Contains("--update-ui-only")) { UiChecks.Run(updatesOnly: true); return 0; }
            if (args.Contains("--screen-fit-ui-only")) { ScreenFitChecks.Placement(); UiChecks.Run(screenFitOnly: true); return 0; }
            Run("小屏/DPI 窗口工作区边界", ScreenFitChecks.Placement);
            Run("应用名称列配置迁移、幂等与异常修复", ApplicationNameChecks.ColumnMigration);
            Run("应用名称搜索、独立排序、字段与进程实例", ApplicationNameChecks.SearchAndSorting);
            Run("同端口连接排序不受无关增删和采集顺序影响", ApplicationNameChecks.SortingRemainsStableAcrossSnapshots);
            Run("筛选往返复用行且连接退出清理缓存", ApplicationNameChecks.RowsSurviveFilteringButNotExit);
            RunAsync("共享名称缓存、退避、PID 复用、溢出与释放", ApplicationNameChecks.CacheAndLifetime).GetAwaiter().GetResult();
            RunAsync("后台搜索只发布最新请求", ApplicationNameChecks.LatestQueryWins).GetAwaiter().GetResult();
            RunAsync("Win32 应用名称身份与路径集成", ApplicationNameChecks.WindowsIntegration).GetAwaiter().GetResult();
            Run("317 明确提示管理员权限", ResetFailure_NonElevated_ExplainsPermission);
            Run("精确匹配 URL 端口", MatchPort_SimilarPorts_DoesNotCrossMatch);
            Run("连接释放能力边界", CanRelease_UnsupportedRecords_ReturnsFalse);
            Run("绑定复检区分地址族与地址", PortStillOccupied_DifferentBinding_ReturnsFalse);
            Run("家族收集排除无关同名进程", Family_UnrelatedSameName_Excluded);
            Run("异常进程树可以结束", Family_CyclicSnapshot_Terminates);
            Run("配置原子保存与非有限值", Settings_RoundTrip_PreservesPreference);
            Run("第二实例被单实例锁拒绝", SingleInstance_SecondAcquire_IsRejected);
            Run("提权接管等待互斥体释放", SingleInstance_Handoff_WaitsForOwner);
            Run("关键进程在执行边界被保护", KillFamily_ProtectedPid_IsRejected);
            Run("自有子进程退出得到确认", Kill_OwnedChild_ConfirmsExit);
            RunAsync("TCP/UDP 真实动态端口采集", Scan_OwnListeners_Captured).GetAwaiter().GetResult();
            RunAsync("已取消扫描不返回快照", Scan_Cancelled_ThrowsCancellation).GetAwaiter().GetResult();
            RunAsync("陈旧连接与不支持连接分别统计", Release_StaleAndUnsupported_Reported).GetAwaiter().GetResult();
            Run("纯快照隔离、显示行复用及模块依赖", ArchitectureChecks.Snapshot_IsIndependentAndRowsAreReused);
            Run("刷新复用行并通知状态和保护标志", RefreshChecks.StateAndFlags_KeepRowsAndNotify);
            Run("高变化、重复行、筛选和排序准确", RefreshChecks.ChurnDuplicatesAndOrdering_AreAccurate);
            Run("交互和 HUD 收敛到最新快照", RefreshChecks.InteractionAndHud_ConvergeToLatestSnapshot);
            Run("批量选择只发布一次统计且保持准确", RefreshChecks.BulkSelection_PublishesOneSummaryAndStaysAccurate);
            Run("空快照在交互期间清空 HUD", RefreshChecks.EmptySnapshot_ClearsHudDuringInteraction);
            Run("端口胶囊监听优先且不依赖表顺序", RefreshChecks.ListeningChips_PreferListenerRegardlessOfTableOrder);
            Run("IPv6 地址保留作用域并明确端口边界", Ipv6_ScopeAndEndpoint_AreUnambiguous);
            RunAsync("更新签名下载、缓存、ETag 与并发去重", UpdateChecks.NormalAndRepeated).GetAwaiter().GetResult();
            RunAsync("同版本不下载更新", UpdateChecks.NoUpdate).GetAwaiter().GetResult();
            RunAsync("断网有限重试与恢复", UpdateChecks.NetworkAndRetry).GetAwaiter().GetResult();
            RunAsync("更新校验失败与退出取消", UpdateChecks.InvalidAndCancelled).GetAwaiter().GetResult();
            RunAsync("更新超时、503 与 404 策略", UpdateChecks.TimeoutAndServerFailure).GetAwaiter().GetResult();
            Run("更新安装失败、中断恢复与安装锁", UpdateChecks.TransactionAndRecovery);
            RunAsync("安装回执等待、版本关联、一次性消费和旧格式兼容", UpdateMaintenanceChecks.Results).GetAwaiter().GetResult();
            RunAsync("更新收尾清理、锁定重试、缓存期限与未完成事务保护", UpdateCleanupChecks.Run).GetAwaiter().GetResult();
            RunAsync("HTTP 签名清单有效期、同源及回放保护", UpdateChecks.HttpAndFreshness).GetAwaiter().GetResult();
            Run("更新压缩包路径与 HTTP 显式配置", UpdateChecks.ArchiveAndConfiguration);
            RunAsync("更新先检测后下载、进度与签名说明", UpdateChecks.DiscoveryAndProgress).GetAwaiter().GetResult();
            RunAsync("更新暂停续传、Range 回退与并发下载", UpdateChecks.PauseResumeAndRangeFallback).GetAwaiter().GetResult();
            if (args.Contains("--ui")) UiChecks.Run();
            Console.WriteLine($"通过 {_passed} 项后端回归验证。{(args.Contains("--ui") ? "窗口验证通过。" : "未执行窗口验证。")}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    /// <summary>执行同步检查并在断言成功后计数。</summary>
    private static void Run(string name, Action action)
    {
        action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    /// <summary>异步检查同样必须完成断言才记为成功。</summary>
    private static async Task RunAsync(string name, Func<Task> action)
    {
        await action();
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    /// <summary>失败直接终止本轮验证。</summary>
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Ipv6_ScopeAndEndpoint_AreUnambiguous()
    {
        var address = IPAddress.Parse("fe80::1234").GetAddressBytes();
        var first = new PortConnectionItem { IpVersion = "IPv6", LocalPort = 8080,
            LocalAddress = NetworkScannerService.FormatIpv6Address(address, 3),
            RemoteAddress = "fe80::abcd%3", RemotePort = 12345 };
        var second = new PortConnectionItem { IpVersion = "IPv6", LocalPort = 8080,
            LocalAddress = NetworkScannerService.FormatIpv6Address(address, 4), State = "LISTENING" };
        Assert(first.LocalAddress == "fe80::1234%3" && second.LocalAddress == "fe80::1234%4" &&
            first.Identity != second.Identity && !MainViewModel.PortStillOccupied(first, second),
            "IPv6 不同网卡作用域被混淆。");
        Assert(first.LocalEndpoint == "[fe80::1234%3]:8080" && first.RemoteEndpoint == "[fe80::abcd%3]:12345",
            "IPv6 端点未用方括号分隔端口。");
    }

    /// <summary>317 的解释依据该接口的语义，而非通用系统消息表。</summary>
    private static void ResetFailure_NonElevated_ExplainsPermission()
    {
        uint error = 317;
        string result = ProcessManagerService.DescribeResetFailure(error);
        Assert(result.Contains("管理员") && result.Contains("317"), "丢失权限原因或原始错误码。");
    }

    /// <summary>覆盖相似数字、默认端口、通配主机与 IPv6。</summary>
    private static void MatchPort_SimilarPorts_DoesNotCrossMatch()
    {
        string output = "http://+:80/\nhttp://*:8080/\nhttp://[::1]:80/a\nhttps://host/\n";
        var result = HttpSysDiagnosticService.MatchPortLines(output, 80);
        Assert(result.Count == 2 && result.All(x => !x.Contains("8080")), "80 误匹配了 8080。");
        Assert(HttpSysDiagnosticService.MatchPortLines(output, 443).Count == 1, "默认 HTTPS 端口匹配失败。");
        Assert(HttpSysDiagnosticService.MatchPortLines("", 80).Count == 0, "空输出必须为空。");
    }

    /// <summary>只允许 IPv4 TIME_WAIT，防止一键释放中断活跃连接。</summary>
    private static void CanRelease_UnsupportedRecords_ReturnsFalse()
    {
        var item = new PortConnectionItem { RawState = MibTcpState.TimeWait };
        Assert(PortReleaseService.CanRelease(item), "IPv4 TIME_WAIT 应可尝试释放。");
        item.IsSystemProtected = true;
        Assert(!item.IsKernelHeld && item.StatusBadge == "TIME_WAIT" && !item.ShowDiagnoseAction,
            "PID 0 的 TIME_WAIT 被错误识别为内核占用。");
        item.IpVersion = "IPv6";
        Assert(!PortReleaseService.CanRelease(item), "IPv6 不支持 SetTcpEntry。");
        item.IpVersion = "IPv4";
        item.RawState = MibTcpState.Established;
        Assert(!PortReleaseService.CanRelease(item), "活跃连接不应进入 TIME_WAIT 清理。");
    }

    /// <summary>同端口不同地址不误报，通配监听仍报告冲突。</summary>
    private static void PortStillOccupied_DifferentBinding_ReturnsFalse()
    {
        var origin = new PortConnectionItem { LocalAddress = "127.0.0.1", LocalPort = 8000 };
        var current = new PortConnectionItem { LocalAddress = "127.0.0.2", LocalPort = 8000, State = "LISTENING" };
        Assert(!MainViewModel.PortStillOccupied(origin, current), "不同地址被当作相同绑定。");
        current.LocalAddress = "0.0.0.0";
        Assert(MainViewModel.PortStillOccupied(origin, current), "通配监听未被识别。");
        current.IpVersion = "IPv6";
        Assert(!MainViewModel.PortStillOccupied(origin, current), "IPv4/IPv6 被混淆。");
    }

    /// <summary>worker 能关联 master，但独立同名实例不会被误杀。</summary>
    private static void Family_UnrelatedSameName_Excluded()
    {
        var snapshot = new Dictionary<int, ProcessSnapshotItem>
        {
            [100] = new() { Pid = 100, ParentPid = 50, ProcessName = "server.exe" },
            [101] = new() { Pid = 101, ParentPid = 100, ProcessName = "server.exe" },
            [102] = new() { Pid = 102, ParentPid = 100, ProcessName = "worker.exe" },
            [200] = new() { Pid = 200, ParentPid = 60, ProcessName = "server.exe" }
        };
        var result = new ProcessManagerService().CollectFamilyTargets(101, "server.exe", snapshot);
        Assert(result.Select(x => x.Pid).SequenceEqual(new[] { 100, 101, 102 }), "家族范围或父先子后顺序不正确。");
        Assert(new ProcessManagerService().CollectFamilyTargets(999, "server.exe", snapshot).Count == 0, "不存在的根进程不能扩大为同名进程。");
    }

    /// <summary>环形父子数据不引起无限递归。</summary>
    private static void Family_CyclicSnapshot_Terminates()
    {
        var snapshot = new Dictionary<int, ProcessSnapshotItem>
        {
            [100] = new() { Pid = 100, ParentPid = 101, ProcessName = "test.exe" },
            [101] = new() { Pid = 101, ParentPid = 100, ProcessName = "test.exe" }
        };
        var result = new ProcessManagerService().CollectFamilyTargets(100, "test.exe", snapshot);
        Assert(result.Count == 2 && result.Select(x => x.Pid).Distinct().Count() == 2, "环形快照结果未去重。");
    }

    /// <summary>独立目录验证真实文件落盘，不接触当前用户的 PortKiller 配置。</summary>
    private static void Settings_RoundTrip_PreservesPreference()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PortKiller-Regression", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "config.json");
        var settings = new AppSettings { AlwaysRunAsAdmin = true, HudX = double.NaN, HudY = double.PositiveInfinity };
        Assert(SettingsService.SaveToPath(settings, path), "第一次保存失败。");
        var restored = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings);
        Assert(restored is { AlwaysRunAsAdmin: true, HudX: null, HudY: null }, "管理员偏好或非有限值清理失败。");
        settings.AlwaysRunAsAdmin = false;
        Assert(SettingsService.SaveToPath(settings, path), "原子替换失败。");
        Assert(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "原子替换未保留备份或留下临时文件。");
    }

    /// <summary>旧实例尚未释放时，接管模式必须等待而不是激活旧窗口后退出。</summary>
    private static void SingleInstance_Handoff_WaitsForOwner()
    {
        string mutexName = $"Local\\PortKiller_Regression_{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--hold-single-instance");
        startInfo.ArgumentList.Add(mutexName);
        using var holder = Process.Start(startInfo);
        if (holder is null) throw new InvalidOperationException("无法启动互斥体持有进程。");
        try
        {
            var ready = holder.StandardOutput.ReadLineAsync();
            string? readyText = ready.Wait(TimeSpan.FromSeconds(2)) ? ready.Result : null;
            Assert(readyText == "READY", "互斥体持有进程未就绪。");
            Assert(SingleInstanceGuard.TryAcquireForTest(mutexName, handoff: true), "接管模式未等待旧实例释放互斥体。");
            Assert(SingleInstanceGuard.IsOwned, "接管模式返回成功但未持有互斥体。");
            SingleInstanceGuard.Release();
            Assert(holder.WaitForExit(2000), "互斥体持有进程未正常退出。");
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            SingleInstanceGuard.Release();
        }
    }

    /// <summary>已有实例持有锁时，普通启动不能取得第二份实例权。</summary>
    private static void SingleInstance_SecondAcquire_IsRejected()
    {
        string mutexName = $"Global\\PortKiller_Regression_{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--hold-single-instance");
        startInfo.ArgumentList.Add(mutexName);
        using var holder = Process.Start(startInfo);
        if (holder is null) throw new InvalidOperationException("无法启动单实例持有进程。");
        try
        {
            var ready = holder.StandardOutput.ReadLineAsync();
            string? readyText = ready.Wait(TimeSpan.FromSeconds(2)) ? ready.Result : null;
            Assert(readyText == "READY", "单实例持有进程未就绪。");
            Assert(!SingleInstanceGuard.TryAcquireForTest(mutexName), "已有实例时第二次获取竟然成功。");
            Assert(holder.WaitForExit(2000), "单实例持有进程未正常退出。");
            Assert(SingleInstanceGuard.TryAcquireForTest(mutexName), "持有者退出后仍无法取得实例权。");
            Assert(SingleInstanceGuard.IsOwned, "释放后重新取得锁却没有保留所有权。");
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            SingleInstanceGuard.Release();
        }
    }

    /// <summary>执行边界直接拒绝 PID 4，不触发任何原生终止。</summary>
    private static void KillFamily_ProtectedPid_IsRejected()
    {
        var targets = new List<(int, string)> { (4, "System") };
        var result = new ProcessManagerService().KillFamilyTargets(targets);
        Assert(!result.Success && result.AffectedCount == 0 && result.Failures.Count == 1, "关键进程保护未生效。");
    }

    /// <summary>只终止测试自身创建的等待进程，校验退出确认和取消边界。</summary>
    private static void Kill_OwnedChild_ConfirmsExit()
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--owned-child")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            var service = new ProcessManagerService();
            var cancelled = service.KillProcess(child.Id, child.ProcessName + ".exe", new CancellationToken(true));
            Assert(!cancelled.Success && !child.HasExited, "已取消的操作仍终止了子进程。");
            var mismatched = service.KillProcess(child.Id, "not-the-owned-child.exe");
            Assert(!mismatched.Success && mismatched.AffectedCount == 0 && !child.HasExited &&
                mismatched.Failures.Any(x => x.Contains("与目标")), "进程名不匹配时仍执行了终止。");
            var result = service.KillProcess(child.Id, child.ProcessName + ".exe");
            Assert(result.Success && result.AffectedCount == 1 && child.WaitForExit(2000), "自有子进程退出未得到确认。");
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    /// <summary>只监听本进程创建的随机端口，验证 TCP/UDP 与 PID 对应关系。</summary>
    private static async Task Scan_OwnListeners_Captured()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var scan = await new ScanCoordinator(new NetworkScannerService()).ScanAsync(0, CancellationToken.None);
        Assert(scan.Items.Any(x => x.Protocol == "TCP" && x.LocalPort == tcpPort && x.Pid == Environment.ProcessId), "未捕获自有 TCP 监听。");
        Assert(scan.Items.Any(x => x.Protocol == "UDP" && x.LocalPort == udpPort && x.Pid == Environment.ProcessId), "未捕获自有 UDP 监听。");
        Assert(scan.Items.Where(x => x.Pid == 4).All(x => !x.IsHttpSys), "PID 4 被直接断言为 HTTP.sys。");
    }

    /// <summary>退出取消不能返回可发布的旧快照。</summary>
    private static async Task Scan_Cancelled_ThrowsCancellation()
    {
        var scans = new ScanCoordinator(new NetworkScannerService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await scans.ScanAsync(0, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "已取消的扫描仍返回结果。");
    }

    /// <summary>无效地址保证不对应系统连接，验证不下发原生释放时的分类。</summary>
    private static async Task Release_StaleAndUnsupported_Reported()
    {
        var requests = new[]
        {
            new PortConnectionItem { LocalAddress = "192.0.2.123", LocalPort = 1, RawState = MibTcpState.TimeWait },
            new PortConnectionItem { IpVersion = "IPv6", RawState = MibTcpState.TimeWait }
        };
        var report = await new PortReleaseService(new ProcessManagerService(), new(new NetworkScannerService())).ReleaseAsync(requests, CancellationToken.None);
        Assert(report.AlreadyGone == 1 && report.Unsupported == 1 && report.Released == 0 && report.Failures.Count == 0,
            "陈旧与不支持连接被错误统计为成功或失败。");
    }
}
