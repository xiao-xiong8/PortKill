using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.Services;

namespace PortKiller.ViewModels;

/// <summary>MainViewModel 分部类：智能家族杀与终止确认。</summary>
public partial class MainViewModel
{
    /// <summary>
    /// 行级「终止」= 智能家族杀：先收集真实父子树与守护包装器。
    /// nginx 类 master/worker 守护软件只杀单 PID 会被立即重生（或孤儿 worker
    /// 继续持有继承的监听套接字），操作前列出真实关联进程，结果以退出确认和端点复检为准。
    /// 目标唯一时保持旧的无感直杀；多目标弹确认框防 chrome 等多实例软件误杀。
    /// </summary>
    [RelayCommand]
    public async Task KillFocusedAsync()
    {
        var item = _focusedItem;
        if (IsOperationBlocked) return;
        if (item == null) { ShowToast("未选中目标", "请先选择需要处理的记录。", true); return; }
        if (item.IsKernelHeld) { OpenIntercept(new[] { item }, "single"); return; }
        _operationBusy = true;
        try
        {
            var targets = await Task.Run(() => _processService.CollectFamilyTargets(item.Pid,
                item.ProcessName, _scannerService.GetProcessSnapshotMap()), _lifetime.Token);
            if (_shuttingDown) return;
            if (targets.Count == 0) { ShowToast("没有可终止的目标", "目标已退出或属于保护范围。", true); return; }
            if (targets.Count > 1)
                OpenFamilyConfirm(targets, item.Pid, $"根据父子关系找到 {targets.Count} 个关联进程；请确认本次终止范围。",
                    origins: new List<PortConnectionItem> { item });
            else await ExecuteBulkCoreAsync(targets, new(), 0, new() { item });
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("终止失败", ex); }
        finally { _operationBusy = false; }
    }

    /// <summary>相同绑定地址与通配地址存在冲突；IPv4 和 IPv6 不混为同一个端点。</summary>
    public static bool PortStillOccupied(PortConnection origin, PortConnection current)
        => current.LocalPort == origin.LocalPort && current.Protocol == origin.Protocol
            && current.IpVersion == origin.IpVersion
            && (current.LocalAddress == origin.LocalAddress || current.LocalAddress is "0.0.0.0" or "::"
                || origin.LocalAddress is "0.0.0.0" or "::")
            && (current.Protocol == "UDP" || current.IsListening || current.IsEstablished);

    /// <summary>只陈述复检证据；端口易主不能直接断言套接字继承或自动杀新进程。</summary>
    private static string DescribeRemainingOwner(PortConnection origin, PortConnection current)
        => $"{origin.Protocol}/{origin.IpVersion} {origin.LocalEndpoint} 仍存在占用："
            + $"{current.ProcessName} (PID {current.Pid})。"
            + (current.Pid == origin.Pid ? "原进程的端口尚未释放。" : "占用者已变化，可能是服务重启或其他实例绑定；请重新确认目标。");

    // ══════════════════════════════════════════════════════════════════
    //  家族终止确认弹窗（多目标守护软件：列清单确认后一网打尽）
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty] private bool _isFamilyConfirmVisible;
    [ObservableProperty] private string _familyConfirmDesc = string.Empty;

    /// <summary>将被终止的目标 chips（主目标高亮，复用拦截弹窗的 InterceptChip）。</summary>
    public ObservableCollection<InterceptChip> FamilyConfirmChips { get; } = new();

    private List<(int Pid, string Name)> _familyPending = new();
    private List<PortConnectionItem> _pendingBulkTimeWaits = new();
    private int _pendingBulkSkipped;
    private List<PortConnectionItem> _familyPendingOrigins = new();

    private void OpenFamilyConfirm(
        List<(int Pid, string Name)> targets, int primaryPid, string desc,
        List<PortConnectionItem>? pendingTimeWaits = null, int skippedProtected = 0,
        List<PortConnectionItem>? origins = null)
    {
        _familyPending = targets;
        _pendingBulkTimeWaits = pendingTimeWaits ?? new List<PortConnectionItem>();
        _pendingBulkSkipped = skippedProtected;
        _familyPendingOrigins = origins ?? new List<PortConnectionItem>();

        if (_pendingBulkTimeWaits.Count > 0)
            desc += $" 另有 {_pendingBulkTimeWaits.Count} 个 TIME_WAIT 孤儿连接将同时注入 RST 释放。";
        FamilyConfirmDesc = desc;

        FamilyConfirmChips.Clear();
        foreach (var t in targets.Take(8))
            FamilyConfirmChips.Add(new InterceptChip($"PID {t.Pid} · {t.Name}", t.Pid == primaryPid));
        if (targets.Count > 8)
            FamilyConfirmChips.Add(new InterceptChip($"… 共 {targets.Count} 个目标", false));

        StatusMessage = $"[家族终止] 待确认：{targets.Count} 个关联进程将被一并终止。";
        IsFamilyConfirmVisible = true;
    }

    [RelayCommand]
    public void CancelFamilyKill()
    {
        IsFamilyConfirmVisible = false;
        _familyPending = new List<(int, string)>();
        _pendingBulkTimeWaits = new List<PortConnectionItem>();
        _pendingBulkSkipped = 0;
        _familyPendingOrigins = new List<PortConnectionItem>();
    }

    [RelayCommand]
    public async Task ConfirmFamilyKillAsync()
    {
        if (_operationBusy || _shuttingDown) return;
        IsFamilyConfirmVisible = false;
        var targets = _familyPending;
        var timeWaits = _pendingBulkTimeWaits;
        int skipped = _pendingBulkSkipped;
        var origins = _familyPendingOrigins;
        _familyPending = new List<(int, string)>();
        _pendingBulkTimeWaits = new List<PortConnectionItem>();
        _pendingBulkSkipped = 0;
        _familyPendingOrigins = new List<PortConnectionItem>();

        if (targets.Count == 0 && timeWaits.Count == 0) return;

        await ExecuteBulkAsync(targets, timeWaits, skipped, origins);
    }

    [RelayCommand]
    public async Task KillFocusedTreeAsync()
    {
        var item = _focusedItem;
        if (IsOperationBlocked) return;
        if (item == null) { ShowToast("未选中目标", "请选择需要终止的进程树。", true); return; }
        if (item.IsKernelHeld) { OpenIntercept(new[] { item }, "tree"); return; }
        _operationBusy = true;
        try
        {
            var result = await Task.Run(() => _processService.KillProcessTree(item.Pid, item.ProcessName, _scannerService, _lifetime.Token), _lifetime.Token);
            var failures = new List<string>(result.Failures);
            if (result.AffectedCount > 0) await VerifyOriginsAsync(new() { item }, failures);
            await RefreshCoreAsync(true);
            ShowToast(failures.Count > 0 || !result.Success ? "进程树终止未全部成功" : "进程树终止完成",
                failures.FirstOrDefault() ?? result.Message, failures.Count > 0 || !result.Success);
            _lastOperationReport = result.Message + "\n" + string.Join("\n", failures);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("进程树终止失败", ex); }
        finally { _operationBusy = false; }
    }

    [RelayCommand]
    public async Task ReleaseFocusedTcpAsync()
    {
        if (_focusedItem == null) { ShowToast("未选中连接", "请选择 IPv4 TCP TIME_WAIT 记录。", true); return; }
        await ReleaseConnectionsAsync(new[] { _focusedItem });
    }

    [RelayCommand]
    public async Task ReleaseAllTimeWaitAsync()
    {
        if (IsOperationBlocked || !EnsureReleasePermission()) return;
        _operationBusy = true;
        try
        {
            await RefreshCoreAsync(true);
            if (_shuttingDown || HasScanError) return;
            var targets = _allItems.Where(x => x.IsTimeWait).ToList();
            if (targets.Count == 0)
            {
                ShowToast("没有 TIME_WAIT 连接", "一键释放仅清理关闭后的 IPv4 TCP 连接；监听端口请使用终止或诊断。");
                return;
            }
            await ReleaseConnectionsCoreAsync(targets);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("释放失败", ex); }
        finally { _operationBusy = false; }
    }

    /// <summary>权限不足在操作前说明原因，不发送数百次必定失败的调用。</summary>
    private bool EnsureReleasePermission()
    {
        if (_shuttingDown) return false;
        IsAdmin = SecurityHelper.IsRunningAsAdministrator();
        if (IsAdmin) return true;
        ShowToast("释放需要管理员权限", "请使用「以管理员身份重启」后重试；尚未执行释放操作 [Win32 317]。", true);
        return false;
    }

    /// <summary>行级、工具栏和托盘入口共享释放流程及失败展示。</summary>
    private async Task ReleaseConnectionsAsync(IReadOnlyList<PortConnection> targets)
    {
        if (IsOperationBlocked || !EnsureReleasePermission()) return;
        _operationBusy = true;
        try
        {
            await ReleaseConnectionsCoreAsync(targets);
        }
        finally { _operationBusy = false; }
    }

    /// <summary>入口已持有操作状态，包括一键释放前的最新快照采集阶段。</summary>
    private async Task ReleaseConnectionsCoreAsync(IReadOnlyList<PortConnection> targets)
    {
        try
        {
            var report = await _releaseService.ReleaseAsync(targets, _lifetime.Token);
            if (_shuttingDown) return;
            await RefreshCoreAsync(true);
            ShowReleaseReport(report);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("释放失败", ex); }
    }

    /// <summary>简短原因留在现有提示区，完整明细交给复制报告。</summary>
    private void ShowReleaseReport(ReleaseReport report)
    {
        string title = report.Failures.Count > 0 ? "释放未全部成功" : report.Unsupported > 0 ? "部分连接不支持释放" : $"已确认释放 {report.Released} 条连接";
        string reason = report.Failures.FirstOrDefault() ?? report.Summary;
        ShowToast(title, reason, report.Failures.Count > 0 || report.Unsupported > 0);
        _lastOperationReport = report.Detail;
        StatusMessage = report.Summary;
    }



    /// <summary>
    /// 批量终止 = 智能分流：
    /// · TIME_WAIT 孤儿连接 → RST 释放（没有存活进程可杀，PID 常为 0/4，
    ///   旧实现把它们当「受保护进程」弹拦截窗，完全误导）；
    /// · 内核占用 / 系统保护 → 唯一动作对象时才弹拦截窗，否则跳过并如实汇报；
    /// · 存活进程 → 家族杀（守护模型展开需确认）。
    /// </summary>
    [RelayCommand]
    public async Task KillCheckedAsync()
    {
        if (IsOperationBlocked) return;
        var checkedRows = FilteredItems.Where(x => x.IsChecked).ToList();
        if (checkedRows.Count == 0) { ShowToast("未勾选记录", "请先勾选需要处理的端口。", true); return; }
        var timeWaits = checkedRows.Where(x => x.IsTimeWait).ToList();
        var protectedRows = checkedRows.Where(x => !x.IsTimeWait && IsProtectedProcess(x)).DistinctBy(x => x.Pid).ToList();
        var origins = checkedRows.Where(x => !x.IsTimeWait && !IsProtectedProcess(x) && x.Pid > 0).ToList();
        var roots = origins.DistinctBy(x => x.Pid).ToList();
        if (roots.Count == 0 && timeWaits.Count == 0)
        {
            if (protectedRows.Count > 0) OpenIntercept(protectedRows, "batch");
            else ShowToast("没有可执行的目标", "目标已退出或不支持此操作。", true);
            return;
        }
        _operationBusy = true;
        try
        {
            var targets = await Task.Run(() => CollectBulkTargets(roots), _lifetime.Token);
            if (_shuttingDown) return;
            if (targets.Count > roots.Count)
                OpenFamilyConfirm(targets, roots.Count == 1 ? roots[0].Pid : -1,
                    $"根据父子关系展开为 {targets.Count} 个目标，请确认关联进程的终止范围。", timeWaits, protectedRows.Count, origins);
            else await ExecuteBulkCoreAsync(targets, timeWaits, protectedRows.Count, origins);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("批量目标查询失败", ex); }
        finally { _operationBusy = false; }
    }

    /// <summary>一份进程快照展开全部选中根，跨家族去重。</summary>
    private List<(int Pid, string Name)> CollectBulkTargets(List<PortConnectionItem> roots)
    {
        if (roots.Count == 0) return new();
        var snapshot = _scannerService.GetProcessSnapshotMap();
        return roots.SelectMany(x => _processService.CollectFamilyTargets(x.Pid, x.ProcessName, snapshot))
            .DistinctBy(x => x.Pid).ToList();
    }

    /// <summary>批量操作保留全部失败明细，复检每个绑定端点，失败绝不显示成功。</summary>
    private async Task ExecuteBulkAsync(List<(int Pid, string Name)> familyTargets,
        List<PortConnectionItem> timeWaits, int skippedProtected, List<PortConnectionItem>? killOrigins = null)
    {
        if (_operationBusy || _shuttingDown) return;
        _operationBusy = true;
        try
        {
            await ExecuteBulkCoreAsync(familyTargets, timeWaits, skippedProtected, killOrigins);
        }
        finally { _operationBusy = false; }
    }

    /// <summary>调用方已占用操作入口；不能再次用 busy 拦掉自己的执行阶段。</summary>
    private async Task ExecuteBulkCoreAsync(List<(int Pid, string Name)> familyTargets,
        List<PortConnectionItem> timeWaits, int skippedProtected, List<PortConnectionItem>? killOrigins)
    {
        try
        {
            var messages = new List<string>();
            var failures = new List<string>();
            if (familyTargets.Count > 0)
            {
                var result = await Task.Run(() => _processService.KillFamilyTargets(familyTargets, _lifetime.Token), _lifetime.Token);
                messages.Add(result.Message);
                failures.AddRange(result.Failures);
                if (result.AffectedCount > 0) await VerifyOriginsAsync(killOrigins, failures);
            }
            if (timeWaits.Count > 0)
            {
                var release = await _releaseService.ReleaseAsync(timeWaits, _lifetime.Token);
                messages.Add(release.Detail);
                failures.AddRange(release.Failures);
            }
            if (skippedProtected > 0) messages.Add($"跳过 {skippedProtected} 个受保护目标，请改用诊断。");
            await RefreshCoreAsync(true);
            if (_shuttingDown) return;
            string detail = string.Join("\n", messages.Concat(failures).Distinct());
            ShowToast(failures.Count > 0 ? "批量操作未全部成功" : "批量处理完成", failures.FirstOrDefault() ?? messages.FirstOrDefault() ?? "没有目标", failures.Count > 0);
            _lastOperationReport = detail;
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("批量操作失败", ex); }
    }

    /// <summary>逐绑定端点复检，不再只验证前三个端口。</summary>
    private async Task VerifyOriginsAsync(List<PortConnectionItem>? origins, List<string> failures)
    {
        if (origins == null || origins.Count == 0) return;
        var snapshot = await _scans.ScanAsync(0, _lifetime.Token);
        foreach (var origin in origins.DistinctBy(x => (x.Protocol, x.IpVersion, x.LocalAddress, x.LocalPort)))
        {
            var remaining = snapshot.Items.FirstOrDefault(x => PortStillOccupied(origin, x));
            if (remaining != null) failures.Add(DescribeRemainingOwner(origin, remaining));
        }
    }

    [RelayCommand]
    public async Task DiagnoseFocusedAsync()
    {
        if (_focusedItem == null) { ShowToast("未选中目标", "请选择需要诊断的端口。", true); return; }
        int port = _focusedItem.LocalPort;
        CurrentView = 1;
        await DiagnosePortAsync(port);
    }

    // ── 复制与系统操作 ──────────────────────────────────────────────────
    [RelayCommand]
    public async Task CopyFocusedRowAsync()
    {
        var item = _focusedItem ?? FilteredItems.FirstOrDefault(x => x.IsChecked);
        if (item == null)
        {
            ShowToast("没有可复制的内容", "请先选中一行或勾选任意记录。", isError: true);
            return;
        }

        string text = $"{item.Protocol}\t{item.LocalEndpoint}\t{item.RemoteEndpoint}\t{item.State}\tPID:{item.Pid}\t{item.ProcessName}";
        await SetClipboardTextAsync(text);
        ShowToast("已复制当前行数据", "制表符分隔，可直接粘贴到 Excel");
    }

    [RelayCommand]
    public async Task CopyEndpointAsync()
    {
        if (_focusedItem == null) return;
        await SetClipboardTextAsync(_focusedItem.LocalEndpoint);
        ShowToast("已复制端点", _focusedItem.LocalEndpoint);
    }

    [RelayCommand]
    public async Task CopyInspectorAsync()
    {
        var item = _focusedItem;
        if (item == null) return;

        string text = $"进程: {InspName}\nPID: {item.Pid}\n可执行文件: {InspExePath}\n启动时间: {InspStartTime}\n父进程: {InspParent}\n端口: {item.LocalEndpoint}\n状态: {item.State}";
        await SetClipboardTextAsync(text);
        ShowToast("已复制进程详情", "可直接粘贴到工单或排查报告中");
    }

    /// <summary>一键复制详情面板中的可执行文件路径；占位/错误提示串不复制。</summary>
    [RelayCommand]
    public async Task CopyExePathAsync()
    {
        var path = InspExePath;
        if (string.IsNullOrWhiteSpace(path) || path is "—" or "正在读取…"
            || path.StartsWith("连接已关闭", StringComparison.Ordinal)
            || path.StartsWith("进程已退出", StringComparison.Ordinal))
        {
            ShowToast("没有可复制的路径", "路径尚未加载完成，或该进程无读取权限。", isError: true);
            return;
        }

        await SetClipboardTextAsync(path);
        ShowToast("已复制可执行文件路径", path);
    }

    /// <summary>复制内核诊断报告全文到剪贴板。</summary>
    public async Task CopyDiagnosticReportAsync()
    {
        if (string.IsNullOrWhiteSpace(DiagnosticReport))
        {
            ShowToast("暂无诊断报告", "请先运行一次内核诊断。", isError: true);
            return;
        }

        await SetClipboardTextAsync(DiagnosticReport);
        ShowToast("已复制诊断报告", "包含 URLACL 匹配结果与处置建议");
    }

    [RelayCommand]
    public void OpenProcessLocation()
    {
        if (_focusedItem == null)
        {
            // 原静默 return：连接已消失（TIME_WAIT 等短生命周期行）时点了像「没反应」
            ShowToast("无法定位文件", "该连接已不存在，请刷新后重试。", isError: true);
            return;
        }

        bool ok = _processService.OpenProcessLocation(_focusedItem.Pid);
        if (!ok)
        {
            ShowToast("无法定位文件", $"PID {_focusedItem.Pid} 无权限访问或进程已退出。", isError: true);
        }
    }

    [RelayCommand]
    public void OpenServicesManager()
    {
        HttpSysDiagnosticService.OpenServicesManager();
    }

    [RelayCommand]
    public void RestartAsAdmin()
    {
        if (IsAdmin || _shuttingDown) return;
        // 明确保存下次启动偏好；UAC 令牌由 Windows 决定，不能序列化。
        _suppressSettingsSave = true;
        AlwaysRunAsAdmin = true;
        _suppressSettingsSave = false;
        if (!SaveSettings()) return;
        if (SecurityHelper.RestartAsAdministrator()) RequestShutdown?.Invoke();
        else ShowToast("未完成管理员授权", "授权已取消或启动失败；当前窗口继续运行，管理员启动偏好已保存。", true);
    }

    /// <summary>复制操作报告，不依赖当前选中行，也不覆盖原报告。</summary>
    [RelayCommand]
    public async Task CopyOperationReportAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastOperationReport)) return;
        await SetClipboardTextAsync(_lastOperationReport);
        StatusMessage = "已复制完整操作报告。";
    }

    // ── Toast 控制 ──────────────────────────────────────────────────────
    public void ShowToast(string title, string description, bool isError = false)
    {
        if (_shuttingDown) return;
        _lastOperationReport = $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{title}\n{description}";
        ToastTitle = title;
        ToastDescription = description;
        ToastIsError = isError;
        IsUpdateToast = false;
        ToastHasDetailAction = true;
        IsToastVisible = true;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) =>
        {
            IsToastVisible = false;
            _toastTimer?.Stop();
        };
        _toastTimer.Start();
    }

    [RelayCommand]
    public void DismissToast()
    {
        IsToastVisible = false;
        _toastTimer?.Stop();
    }

    // ── 工具 ────────────────────────────────────────────────────────────
    private static async Task SetClipboardTextAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(text);
        }
    }
}
