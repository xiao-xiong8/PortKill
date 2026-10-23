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

/// <summary>MainViewModel 分部类：内核诊断视图。</summary>
public partial class MainViewModel
{
    // ── 内核诊断视图 ────────────────────────────────────────────────────
    private Task? _serviceStatesTask;

    /// <summary>服务查询只在进入诊断页时执行，不阻塞首屏。</summary>
    public Task LoadServiceStatesAsync()
    {
        if (_shuttingDown) return Task.CompletedTask;
        if (_serviceStatesTask is { IsCompleted: false }) return _serviceStatesTask;
        return _serviceStatesTask = LoadServiceStatesCoreAsync();
    }

    private async Task LoadServiceStatesCoreAsync()
    {
        try
        {
            var services = await Task.Run(HttpSysDiagnosticService.GetServiceStates, _lifetime.Token);
            if (_shuttingDown) return;
            DependentServices.Clear();
            foreach (var service in services) DependentServices.Add(service);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("服务状态读取失败", ex); }
    }

    /// <summary>内核诊断视图不在前台时只记脏标记，切到该视图再重算（省掉每 tick 的全表 LINQ + 集合重建）。</summary>
    private bool _kernelViewDirty;

    private void MaybeRefreshKernelView()
    {
        if (CurrentView == 1)
        {
            RefreshKernelView();
            _kernelViewDirty = false;
        }
        else
        {
            _kernelViewDirty = true;
        }
    }

    private void RefreshKernelView()
    {
        // 只关心「被内核/特权服务持有且正在监听」的端口 —— 这类端口无法靠终止进程释放
        var kernel = _allItems
            .Where(x => x.IsKernelHeld && (x.IsListening || x.IsHttpSys))
            .OrderBy(x => x.LocalPort)
            .ToList();

        KernelItems.Clear();
        foreach (var item in kernel) KernelItems.Add(PortConnectionItem.FromSnapshot(item));

        KernelEmpty = kernel.Count == 0;
        if (KernelEmpty)
        {
            KernelHeading = "未检测到内核级端口占用";
            KernelSubheading = "当前快照未发现受保护进程的 TCP 监听记录。";
            DiagnosticReport = string.Empty;
        }
        else
        {
            int runningSvc = DependentServices.Count(s => s.IsRunning);
            KernelHeading = $"检测到 {kernel.Count} 个内核级端口占用";
            KernelSubheading = $"{string.Join(" / ", kernel.Select(k => k.LocalPort).Distinct())} 由内核或受保护进程持有，具体组件需进一步诊断"
                               + (runningSvc > 0 ? $" · 当前有 {runningSvc} 个 Web 依赖服务运行中" : "");
        }
    }

    [RelayCommand]
    public async Task RunKernelDiagnosticAsync()
    {
        var item = KernelItems.FirstOrDefault(x => ReferenceEquals(x, _focusedItem)) ?? KernelItems.FirstOrDefault();
        if (item == null) { ShowToast("无需诊断", "当前没有内核监听记录。"); return; }
        await DiagnosePortAsync(item.LocalPort);
    }

    private CancellationTokenSource? _diagnosticCts;

    /// <summary>只接受最新端口的诊断；切换目标时结束上次创建的诊断命令。</summary>
    private async Task DiagnosePortAsync(int port)
    {
        if (_shuttingDown) return;
        _diagnosticCts?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnosticCts = request;
        DiagnosticReport = $"正在诊断端口 {port}，读取实际 HTTP 注册状态和权限规则…";
        try
        {
            var result = await _diagnosticService.DiagnosePortAsync(port, request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_diagnosticCts, request)) return;
            DiagnosticReport = result.Summary;
            await LoadServiceStatesAsync();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested || !ReferenceEquals(_diagnosticCts, request)) return;
            if (!_shuttingDown) DiagnosticReport = "诊断失败：" + ex.Message;
            ReportFailure("诊断失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_diagnosticCts, request)) _diagnosticCts = null;
        }
    }
}
