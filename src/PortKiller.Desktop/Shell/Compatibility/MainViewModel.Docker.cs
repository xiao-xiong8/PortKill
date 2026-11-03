using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortKiller.Docker;
using PortKiller.Models;

namespace PortKiller.ViewModels;

public sealed record DockerPolicyChoice(RestartPolicyChange Change)
{
    public string Label => $"{Change.ContainerName} · {Change.ContainerId[..Math.Min(12, Change.ContainerId.Length)]} · {Change.OriginalName}";
}

public partial class MainViewModel
{
    private ScanResult? _nativeDockerSource;
    private readonly DockerEnrichmentCoordinator _dockerEnrichment = new();
    private bool _dockerEnrichmentPending;
    private int _dockerPostPending;
    [ObservableProperty] private string _dockerStatusText = "Docker：尚未查询";
    [ObservableProperty] private string _inspectorDockerText = "";
    public string InspectorActionText => _focusedItem?.RequiresDockerResolution == true ? "核验并停止整个容器" : "终止进程树（含全部子进程）";
    [ObservableProperty] private string _exactLocalPort = "";
    [ObservableProperty] private string _exactLocalAddress = "";
    [ObservableProperty] private string _exactProtocol = "全部协议";
    [ObservableProperty] private string _exactFamily = "全部地址族";
    [ObservableProperty] private string _exactOwner = "全部占用者";
    public string[] ExactProtocolOptions { get; } = { "全部协议", "TCP", "UDP" };
    public string[] ExactFamilyOptions { get; } = { "全部地址族", "IPv4", "IPv6" };
    public string[] ExactOwnerOptions { get; } = { "全部占用者", "Docker 容器", "Windows 进程", "待确认" };
    public string ExactFilterError => ExactLocalPort.Trim().Length > 0 && (!int.TryParse(ExactLocalPort, out int port) || port is < 1 or > 65535)
        ? "本地端口请输入 1—65535。" : ExactLocalAddress.Trim().Length > 0 && !IPAddress.TryParse(ExactLocalAddress.Trim(), out _)
        ? "宿主地址必须是完整 IP 地址；IPv6 链路本地地址需保留作用域。" : "";
    partial void OnExactLocalPortChanged(string value) => OnExactFilterChanged();
    partial void OnExactLocalAddressChanged(string value) => OnExactFilterChanged();
    partial void OnExactProtocolChanged(string value) => OnExactFilterChanged();
    partial void OnExactFamilyChanged(string value) => OnExactFilterChanged();
    partial void OnExactOwnerChanged(string value) => OnExactFilterChanged();
    private void OnExactFilterChanged() { OnPropertyChanged(nameof(ExactFilterError)); _connectionQueries.Invalidate(); ApplyFilter(); }
    [RelayCommand] private void ClearExactFilters()
    { ExactLocalPort = ExactLocalAddress = ""; ExactProtocol = "全部协议"; ExactFamily = "全部地址族"; ExactOwner = "全部占用者"; }

    private void InitializeDocker()
    {
        _portServices.DockerInventory.Changed += OnDockerInventoryChanged;
        _dockerEnrichment.Completed += OnDockerEnrichmentCompleted;
    }
    private void OnDockerInventoryChanged()
    {
        if (Interlocked.Exchange(ref _dockerPostPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dockerPostPending, 0);
            if (_shuttingDown) return;
            var inventory = _portServices.DockerInventory.Snapshot;
            DockerStatusText = inventory.Error == null
                ? $"Docker：{inventory.Containers.Count} 个容器 · {inventory.Containers.Sum(c => c.Bindings.Count)} 项发布 · {inventory.ObservedAt.ToLocalTime():HH:mm:ss}"
                : "Docker：" + inventory.Error;
            RequestDockerEnrichment(respectHold: true, refreshInspector: true);
        }, DispatcherPriority.Background);
    }

    private void RequestDockerEnrichment(bool respectHold, bool refreshInspector)
    {
        if (_shuttingDown || _nativeDockerSource == null) return;
        _queryBypassesHold |= !respectHold;
        _refreshInspectorPending |= refreshInspector;
        _connectionQueries.Invalidate();
        if (_backgroundQueriesEnabled)
        {
            _dockerEnrichmentPending = true;
            _dockerEnrichment.Enqueue(_nativeDockerSource.Items, _portServices.DockerInventory.Snapshot);
        }
        else ApplyDockerEnrichment(DockerInventoryCoordinator.Enrich(_nativeDockerSource.Items, _portServices.DockerInventory.Snapshot));
    }

    private void OnDockerEnrichmentCompleted(DockerEnrichmentResult result) => Dispatcher.UIThread.Post(() =>
    {
        if (_shuttingDown || result.Version != _dockerEnrichment.Version) return;
        if (result.Error != null) DockerStatusText = result.Error;
        _dockerEnrichmentPending = false;
        ApplyDockerEnrichment(result.Items);
    }, DispatcherPriority.Background);

    private void ApplyDockerEnrichment(IReadOnlyList<PortConnection> items)
    {
        _allItems = items;
        _connectionRowCache.UpdateSnapshot(items);
        _connectionQueries.Invalidate();
        UpdateHudRows();
        RequestConnectionFilter(respectHold: true);
    }

    [ObservableProperty] private bool _isDockerConfirmVisible;
    [ObservableProperty] private string _dockerDialogTitle = "停止 Docker 容器";
    [ObservableProperty] private string _dockerConfirmDetails = "";
    [ObservableProperty] private bool _dockerDisableRestart;
    [ObservableProperty] private bool _dockerCanForce;
    [ObservableProperty] private bool _dockerCanContinue;
    [ObservableProperty] private bool _hasPendingDockerStop;
    private readonly List<ContainerStopPlan> _dockerPlans = new();
    private readonly List<string> _dockerReports = new();
    private List<PortConnectionItem> _dockerNativeOrigins = new();
    private int _dockerPlanIndex;
    private ContainerStopSession? _dockerSession;
    private RestartPolicyChange? _dockerPolicyRestore;
    public ObservableCollection<DockerPolicyChoice> DockerPolicyChoices { get; } = new();
    [ObservableProperty] private DockerPolicyChoice? _selectedDockerPolicy;
    public bool IsDockerPolicyReview => _dockerPolicyRestore != null;
    partial void OnSelectedDockerPolicyChanged(DockerPolicyChoice? value)
    {
        _dockerPolicyRestore = value?.Change;
        if (value?.Change is { } record)
            DockerConfirmDetails = $"容器：{record.ContainerName}\n完整 ID：{record.ContainerId}\nEngine：{record.EngineId}"
                + $"\n恢复为：{record.OriginalName} ({record.OriginalMaximumRetryCount})\n记录时间：{record.ChangedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                + "\n仅在同一 Engine、同一容器且当前策略仍为 no 时执行；不会启动容器。";
        NotifyDockerDialog();
    }
    public bool DockerDialogEnabled => !_operationBusy;
    public bool DockerCanSelectPolicy => _dockerSession == null && _dockerPolicyRestore == null;
    public bool DockerCanConfirm => _dockerPolicyRestore != null || _dockerSession == null || DockerCanForce;
    public string DockerConfirmActionText => _dockerPolicyRestore != null ? "确认恢复策略"
        : _dockerSession == null ? "确认停止容器" : "确认强制终止";
    private void NotifyDockerDialog()
    {
        OnPropertyChanged(nameof(DockerDialogEnabled)); OnPropertyChanged(nameof(DockerCanSelectPolicy));
        OnPropertyChanged(nameof(DockerCanConfirm)); OnPropertyChanged(nameof(DockerConfirmActionText));
        OnPropertyChanged(nameof(IsDockerPolicyReview));
    }

    private async Task<bool> TryHandleDockerRowsAsync(IReadOnlyList<PortConnectionItem> rows)
    {
        var inventory = _portServices.DockerInventory.Snapshot;
        var index = EndpointOwnershipResolver.CreateIndex(inventory, _nativeDockerSource?.Items ?? rows);
        var dockerRows = rows.Where(r => !r.IsTimeWait && (r.RequiresDockerResolution
            || index.Resolve(r).Container != null)).ToArray();
        if (dockerRows.Length == 0) return false;
        if (_dockerSession != null)
        { IsDockerConfirmVisible = true; ShowToast("已有容器操作", "请先继续观察现有操作；尚未扩大目标范围。", true); return true; }
        _operationBusy = true;
        try
        {
            var plans = await _portServices.ContainerStops.PrepareAsync(dockerRows, _lifetime.Token);
            if (_shuttingDown) return true;
            _dockerPlans.Clear(); _dockerPlans.AddRange(plans); _dockerPlanIndex = 0; _dockerReports.Clear();
            _dockerNativeOrigins = rows.Except(dockerRows).ToList();
            SelectedDockerPolicy = null; _dockerPolicyRestore = null; DockerDisableRestart = false; DockerCanForce = DockerCanContinue = false;
            DockerDialogTitle = $"停止 {plans.Count} 个 Docker 容器";
            DockerConfirmDetails = string.Join("\n\n", plans.Select(p => p.Container.Description))
                + "\n\n将停止整个容器，容器内所有服务及其他发布端口都会同时停止。"
                + "\n默认保留重启策略；always 容器可能在 Docker daemon 重启后再次启动。"
                + (_dockerNativeOrigins.Count > 0 ? $"\n另有 {_dockerNativeOrigins.Count} 条原生记录，容器处理结束后将单独确认其范围。" : "");
            IsDockerConfirmVisible = true;
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception e) { ShowToast("无法确认容器归属", e.Message, true); }
        finally { _operationBusy = false; NotifyDockerDialog(); }
        return true;
    }

    [RelayCommand]
    private async Task ConfirmDockerStopAsync()
    {
        if (_operationBusy || _shuttingDown || !IsDockerConfirmVisible || !DockerCanConfirm) return;
        _operationBusy = true; NotifyDockerDialog();
        try
        {
            if (_dockerPolicyRestore is { } restore)
            {
                await _portServices.ContainerStops.RestorePolicyAsync(restore, _lifetime.Token);
                SelectedDockerPolicy = null; _dockerPolicyRestore = null; IsDockerConfirmVisible = false;
                ShowToast("已恢复重启策略", $"{restore.ContainerName}：{restore.OriginalName}");
                return;
            }
            if (_dockerSession != null)
            {
                if (!DockerCanForce) return;
                var result = await _portServices.ContainerStops.ForceAsync(_dockerSession, userConfirmed: true, _lifetime.Token);
                if (!AcceptDockerResult(result)) return;
            }
            await RunDockerPlansAsync();
        }
        catch (OperationCanceledException) when (_shuttingDown) { SaveDockerBatchReport("Unknown", "应用已关闭，停止结果尚未确认。"); }
        catch (Exception e) { HandleDockerOperationFailure(e); }
        finally { _operationBusy = false; NotifyDockerDialog(); }
    }

    private async Task RunDockerPlansAsync()
    {
        while (_dockerPlanIndex < _dockerPlans.Count)
        {
            var plan = _dockerPlans[_dockerPlanIndex];
            DockerDialogTitle = "正在优雅停止容器";
            DockerConfirmDetails = plan.Container.Description + "\n\n正在等待 Engine 停止；应用超时不会自动强制终止。";
            SaveDockerBatchReport("InProgress", "正在核验目标，尚未确认停止结果。");
            _dockerSession = await _portServices.ContainerStops.BeginAsync(plan, DockerDisableRestart, _lifetime.Token);
            HasPendingDockerStop = true;
            SaveDockerBatchReport("Stopping", "停止操作已建立，正在观察结果。");
            var result = await _portServices.ContainerStops.ObserveAsync(_dockerSession, _lifetime.Token);
            if (!AcceptDockerResult(result)) return;
        }
        IsDockerConfirmVisible = false; HasPendingDockerStop = false;
        ShowToast("容器处理结果", _dockerReports.LastOrDefault() ?? "没有执行目标。", _dockerReports.Any(r => !r.Contains("[Released]", StringComparison.Ordinal)));
        SaveDockerBatchReport();
        await RefreshCoreAsync(true);
        if (_dockerNativeOrigins.Count > 0)
        {
            var origins = _dockerNativeOrigins.Where(r => !r.IsTimeWait && !IsProtectedProcess(r)).ToList();
            var timeWaits = _dockerNativeOrigins.Where(r => r.IsTimeWait).ToList();
            var targets = await Task.Run(() => CollectBulkTargets(origins), _lifetime.Token);
            int skipped = _dockerNativeOrigins.Count - origins.Count - timeWaits.Count;
            _dockerNativeOrigins.Clear();
            if (targets.Count > 0 || timeWaits.Count > 0)
                OpenFamilyConfirm(targets, -1, "Docker 部分已逐项报告；请单独确认本次 Windows 原生进程/连接范围。", timeWaits, skipped, origins);
        }
        _dockerPlans.Clear(); _dockerPlanIndex = 0;
    }
    private bool AcceptDockerResult(ContainerStopResult result)
    {
        if (_dockerSession == null) return false;
        var plan = _dockerSession.Plan;
        if (result.State == ContainerStopState.AwaitingForceConfirmation || result.State == ContainerStopState.Unknown && result.Pending)
        {
            DockerDialogTitle = result.State == ContainerStopState.AwaitingForceConfirmation ? "容器尚未停止：请选择后续操作" : "容器结果尚未确认";
            DockerConfirmDetails = plan.Container.Description + "\n\n" + result.Detail + "\n关闭面板仅结束前台观察，不撤回已发出的停止请求。";
            DockerCanForce = result.State == ContainerStopState.AwaitingForceConfirmation;
            DockerCanContinue = true; HasPendingDockerStop = true; NotifyDockerDialog();
            SaveDockerBatchReport(result.State.ToString(), result.Detail);
            return false;
        }
        _dockerReports.Add($"{plan.Container.Name} ({plan.Container.ShortId}) [{result.State}]\n{plan.Container.Description}\n"
            + (_dockerSession.DisabledRestart ? "本次已禁用重启策略，恢复记录已保存；未自动恢复。\n" : "本次保留重启策略。\n") + result.Detail);
        _dockerSession = null; _dockerPlanIndex++;
        DockerCanForce = DockerCanContinue = false; HasPendingDockerStop = false;
        SaveDockerBatchReport();
        return true;
    }

    private static string DescribeDockerPlan(ContainerStopPlan plan, string state, string detail)
        => $"{plan.Container.Name} ({plan.Container.ShortId}) [{state}]\n{plan.Container.Description}\n{detail}";

    private void SaveDockerBatchReport(string? currentState = null, string? currentDetail = null)
    {
        var entries = new List<string>(_dockerReports);
        for (int i = _dockerPlanIndex; i < _dockerPlans.Count; i++)
            entries.Add(DescribeDockerPlan(_dockerPlans[i], i == _dockerPlanIndex ? currentState ?? "NotExecuted" : "NotExecuted",
                i == _dockerPlanIndex && currentDetail != null
                    ? (_dockerSession?.DisabledRestart == true ? "本次已禁用重启策略，恢复记录已保存；未自动恢复。\n" : "") + currentDetail
                    : "尚未执行，未向该目标发送停止或强制请求。"));
        foreach (var row in _dockerNativeOrigins)
            entries.Add($"Windows [NotExecuted]\n{row.Protocol}/{row.IpVersion} {row.LocalEndpoint} · {row.ProcessName} (PID {row.Pid})\n尚未执行，需单独确认原生进程/连接范围。");
        if (entries.Count > 0) _lastOperationReport = string.Join("\n\n", entries);
    }

    private void HandleDockerOperationFailure(Exception error)
    {
        DockerConfirmDetails = "操作未完成：" + error.Message;
        ShowToast("容器操作未完成", error.Message + (_dockerPolicyRestore == null && _dockerSession == null
            ? "\n本批次已结束，其余目标未执行；请刷新后重新选择并确认。完整结果可复制操作报告。" : ""), true);
        // 策略恢复有独立的重试流程；已发出停止请求的会话必须保留供继续观察。
        if (_dockerPolicyRestore != null) return;
        if (_dockerSession != null)
        {
            DockerCanContinue = true; HasPendingDockerStop = true;
            SaveDockerBatchReport("Unknown", "操作或观察失败，已发出的请求仍可能生效：" + error.Message);
            return;
        }
        if (_dockerPlanIndex < _dockerPlans.Count)
        {
            _dockerReports.Add(DescribeDockerPlan(_dockerPlans[_dockerPlanIndex], "Failed",
                "未能建立停止会话：" + error.Message + "\n若策略已修改，其恢复记录会保留，请通过策略恢复入口核查。"));
            _dockerPlanIndex++;
        }
        else _dockerReports.Add("批次后续处理失败：" + error.Message);
        SaveDockerBatchReport();
        DockerConfirmDetails = _lastOperationReport;
        _dockerPlans.Clear(); _dockerNativeOrigins.Clear(); _dockerPlanIndex = 0;
        DockerCanForce = DockerCanContinue = HasPendingDockerStop = IsDockerConfirmVisible = false;
    }

    [RelayCommand]
    private async Task ContinueDockerStopAsync()
    {
        if (_operationBusy || _shuttingDown || _dockerSession == null) return;
        IsDockerConfirmVisible = true; _operationBusy = true; NotifyDockerDialog();
        try
        {
            var result = await _portServices.ContainerStops.ObserveAsync(_dockerSession, _lifetime.Token);
            if (AcceptDockerResult(result)) await RunDockerPlansAsync();
        }
        catch (OperationCanceledException) when (_shuttingDown) { SaveDockerBatchReport("Unknown", "应用已关闭，停止结果尚未确认。"); }
        catch (Exception e) { HandleDockerOperationFailure(e); }
        finally { _operationBusy = false; NotifyDockerDialog(); }
    }
    [RelayCommand]
    private void CloseDockerDialog()
    {
        if (_operationBusy) return;
        IsDockerConfirmVisible = false;
        if (_dockerSession == null)
        {
            if (_dockerPolicyRestore == null && _dockerPlans.Count > 0) SaveDockerBatchReport();
            _dockerPlans.Clear(); _dockerNativeOrigins.Clear(); SelectedDockerPolicy = null; _dockerPolicyRestore = null;
        }
    }

    [RelayCommand]
    private async Task ReviewDockerPolicyAsync()
    {
        if (IsOperationBlocked || _dockerSession != null) return;
        _operationBusy = true;
        try
        {
            var records = await _portServices.DockerPolicies.ReadAsync(_lifetime.Token);
            if (records.Count == 0) { ShowToast("没有策略恢复记录", "本工具尚无待恢复的重启策略。"); return; }
            DockerPolicyChoices.Clear();
            foreach (var record in records) DockerPolicyChoices.Add(new(record));
            DockerDialogTitle = "恢复容器重启策略";
            SelectedDockerPolicy = DockerPolicyChoices[0];
            DockerCanForce = DockerCanContinue = false; IsDockerConfirmVisible = true;
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception e) { ShowToast("无法读取策略记录", e.Message, true); }
        finally { _operationBusy = false; NotifyDockerDialog(); }
    }
}
