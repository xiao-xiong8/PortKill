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

/// <summary>MainViewModel 分部类：终止动作与系统弹窗（拦截 / 退出确认 / 关停）。</summary>
public partial class MainViewModel
{
    // ── 终止 / 释放动作 ─────────────────────────────────────────────────

    // ══════════════════════════════════════════════════════════════════
    //  系统保护拦截弹窗（设计稿「弹窗 · 系统保护拦截警告」720×432）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>内核绝对保护由执行服务统一定义，界面额外提示特权服务风险。</summary>
    public static bool IsProtectedProcess(PortConnectionItem item)
        => item.IsKernelHeld || ProcessManagerService.IsAbsolutelyProtected(item.Pid, item.ProcessName);

    [ObservableProperty] private bool _isInterceptVisible;
    [ObservableProperty] private bool _interceptRiskChecked;

    /// <summary>
    /// 拦截目标中是否存在「警告级」（可真实强制终止）的进程。
    /// 全部目标都是内核绝对保护级（PID≤4 / csrss 等蓝屏级）时为 false ——
    /// 此时隐藏风险勾选与「仍要终止」按钮：Windows 不允许任何权限终止它们，
    /// 给一个永远失败的确认选项是不诚实的交互。
    /// </summary>
    [ObservableProperty] private bool _interceptHasForceable;

    private PortConnectionItem? _interceptSingle;
    private List<PortConnectionItem> _interceptBatch = new();
    private string _interceptMode = "single"; // single | tree | batch

    /// <summary>「本次被拦截的目标」chips（wrap 流式布局，主目标高亮玫瑰红）。</summary>
    public ObservableCollection<InterceptChip> InterceptChips { get; } = new();

    [ObservableProperty] private string _interceptDesc =
        "PID 4 (System) 属 Windows 内核保护进程，强制终止会立即触发蓝屏 (BSOD) 并可能导致未保存数据丢失。该指令已被 PortKiller 安全策略拦截，系统运行不受影响。";

    private void OpenIntercept(IEnumerable<PortConnectionItem> targets, string mode)
    {
        var items = targets.ToList();
        _interceptBatch = items;
        _interceptSingle = items.FirstOrDefault();
        _interceptMode = mode;
        InterceptRiskChecked = false;

        // 区分「内核绝对保护级」（PID≤4 / csrss 等，强杀=蓝屏，任何权限都不可终止）
        // 与「警告级」（svchost 等特权服务，确认后允许真实强制终止）
        InterceptHasForceable = items.Any(t =>
            !ProcessManagerService.IsAbsolutelyProtected(t.Pid, t.ProcessName));

        InterceptChips.Clear();
        foreach (var t in items.Take(6))
            InterceptChips.Add(new InterceptChip($"PID {t.Pid} · {t.ProcessName}", IsProtectedProcess(t)));
        if (items.Count > 6) InterceptChips.Add(new InterceptChip($"… 等 {items.Count} 项", false));

        var first = _interceptSingle;
        string desc;
        if (!InterceptHasForceable)
        {
            desc = first is null
                ? "目标属 Windows 内核绝对保护级，任何权限都无法终止。"
                : $"PID {first.Pid} ({first.ProcessName}) 属 Windows 内核绝对保护级进程，强制终止会立即触发蓝屏 (BSOD)。Windows 在内核层禁止任何用户（含 SYSTEM）终止它们，因此不提供强制选项。请改用「内核诊断」定位真实占用来源。";
        }
        else
        {
            desc = first is null
                ? "目标进程属系统保护范围，指令已被安全策略拦截。"
                : $"PID {first.Pid} ({first.ProcessName}) 属 Windows 特权服务进程，强制终止可能导致依赖它的功能异常甚至会话崩溃。该指令已被 PortKiller 安全策略拦截；确认风险后可真实强制执行（绝对保护级目标会被自动跳过）。";
        }
        InterceptDesc = desc;

        StatusMessage = $"[拦截] 已阻止对 {items.Count} 个受保护目标的终止指令。";
        IsInterceptVisible = true;
    }

    [RelayCommand]
    public void CancelIntercept() => IsInterceptVisible = false;

    [RelayCommand]
    public async Task ConfirmInterceptAsync()
    {
        if (_operationBusy || _shuttingDown) return;
        if (!InterceptRiskChecked || !InterceptHasForceable)
        {
            ShowToast("请先确认风险", "勾选风险确认后才能继续。", true);
            return;
        }
        IsInterceptVisible = false;
        var batch = _interceptBatch;
        string mode = _interceptMode;
        _operationBusy = true;
        try
        {
            var results = await Task.Run(() => ExecuteConfirmedTargets(batch, mode), _lifetime.Token);
            var failures = results.SelectMany(x => x.Failures).ToList();
            await VerifyOriginsAsync(batch, failures);
            await RefreshCoreAsync(true);
            ShowToast(failures.Count > 0 ? "强制终止未全部成功" : "强制终止完成",
                failures.FirstOrDefault() ?? $"已确认退出 {results.Sum(x => x.AffectedCount)} 个进程。", failures.Count > 0);
            _lastOperationReport = string.Join("\n", results.Select(x => x.Message).Concat(failures));
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("强制终止失败", ex); }
        finally { _operationBusy = false; }
    }

    /// <summary>已确认风险的目标逐个执行，保留原始错误并检查退出取消信号。</summary>
    private List<OperationResult> ExecuteConfirmedTargets(List<PortConnectionItem> batch, string mode)
    {
        var results = new List<OperationResult>();
        foreach (var item in batch.DistinctBy(x => x.Pid))
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            results.Add(mode == "tree"
                ? _processService.KillProcessTree(item.Pid, item.ProcessName, _scannerService, _lifetime.Token)
                : _processService.KillProcess(item.Pid, item.ProcessName, _lifetime.Token));
        }
        return results;
    }

    // ══════════════════════════════════════════════════════════════════
    //  退出二次确认（设计稿「窗口行为规则」：退出是唯一真正结束进程的入口）
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty] private bool _isExitConfirmVisible;
    [ObservableProperty] private string _exitConfirmText =
        "PortKiller 仍在后台运行实时监听。退出后端口变化将不再被追踪，确定要结束程序吗？";

    [RelayCommand] public void CancelExit() => IsExitConfirmVisible = false;

    [RelayCommand]
    public void ConfirmExit()
    {
        IsExitConfirmVisible = false;
        RequestShutdown?.Invoke();
    }

    /// <summary>视图层订阅：真正结束进程（Application.Shutdown）。</summary>
    public event Action? RequestShutdown;

    /// <summary>退出进行中：在途扫描回到 UI 线程后直接丢弃，不再触碰可视化树。</summary>
    private bool _shuttingDown;

    /// <summary>视图层在真正退出前调用：停实时监听 Timer，封闭在途扫描的 UI 延续。</summary>
    public void PrepareShutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        _lifetime.Cancel();
        DisposeProcessMetadata();
        Shell.SelectionChanged -= OnToolSelected;
        Shell.Dispose();
        if (_updates != null) _updates.Status -= OnUpdateStatus;
        UpdateUi.Dispose();
        _liveTimer?.Stop();
        _toastTimer?.Stop();
        _searchDebounceCts?.Cancel();
        _fontSearchDebounceCts?.Cancel();
        TypographyService.Applied -= OnTypographyApplied;
        TypographyService.FontListReady -= FinishFontList;
        foreach (var item in FilteredItems) item.PropertyChanged -= OnItemPropertyChanged;
    }
}
