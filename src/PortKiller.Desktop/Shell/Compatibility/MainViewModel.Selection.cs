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

/// <summary>MainViewModel 分部类：行选择与详情面板。</summary>
public partial class MainViewModel
{
    // ── 行交互 ──────────────────────────────────────────────────────────
    /// <summary>点击勾选框：切换批量选择。</summary>
    public void ToggleCheck(PortConnectionItem item)
    {
        item.IsChecked = !item.IsChecked;
    }

    /// <summary>点击详情面板端口胶囊：以「:端口」语法筛选列表；再次点击同一胶囊清除筛选。</summary>
    [RelayCommand]
    private void FilterByPortChip(PortChipItem? chip)
    {
        if (chip == null || !int.TryParse(chip.Port, out _)) return;
        var target = $":{chip.Port}";
        SearchText = string.Equals(SearchText.Trim(), target, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : target;
    }

    /// <summary>点击行：把该行设为详情面板焦点。</summary>
    public void FocusRow(PortConnectionItem item)
    {
        if (ReferenceEquals(_focusedItem, item)) return;
        if (_focusedItem != null && !ReferenceEquals(_focusedItem, item))
        {
            _focusedItem.IsFocused = false;
        }
        _focusedItem = item;
        item.IsFocused = true;
        RecomputeInspector();
    }

    /// <summary>当前焦点行（键盘导航与 Space 勾选用）。</summary>
    public PortConnectionItem? FocusedItem => _focusedItem;

    /// <summary>是否存在勾选的行（Esc 逐层关闭的最后一层）。</summary>
    public bool HasCheckedRows => IsAllChecked || IsPartialChecked;

    /// <summary>
    /// 键盘 ↑/↓：焦点在筛选结果中步进，返回新焦点在列表中的索引（-1 表示无可用行），
    /// 视图据此把该行滚入视口。
    /// </summary>
    public int MoveFocusIndex(int delta)
    {
        if (FilteredItems.Count == 0) return -1;
        int index = _focusedItem != null ? FilteredItems.IndexOf(_focusedItem) : -1;
        int next = index < 0
            ? (delta > 0 ? 0 : FilteredItems.Count - 1)
            : Math.Clamp(index + delta, 0, FilteredItems.Count - 1);
        var item = FilteredItems[next];
        if (!ReferenceEquals(item, _focusedItem)) FocusRow(item);
        return next;
    }

    /// <summary>清除焦点行并收起详情面板（Esc 逐层关闭的一环）。</summary>
    public void ClearFocus()
    {
        if (_focusedItem == null) return;
        _focusedItem.IsFocused = false;
        _focusedItem = null;
        RecomputeInspector();
    }

    [RelayCommand]
    public void ToggleCheckAll()
    {
        bool allChecked = FilteredItems.Count > 0 && FilteredItems.All(x => x.IsChecked);
        SetAllChecks(!allChecked);
    }

    [RelayCommand]
    public void ClearSelection()
    {
        SetAllChecks(false);
        StatusMessage = "已取消全部选择。";
    }

    private bool _updatingChecks;

    private void SetAllChecks(bool value)
    {
        _updatingChecks = true;
        try
        {
            foreach (var item in FilteredItems) item.IsChecked = value;
        }
        finally
        {
            _updatingChecks = false;
            RecomputeSelection();
        }
    }

    private void RecomputeSelection()
    {
        // 单遍统计（原先 ToList + All + Any 三次全表枚举）
        int count = 0;
        foreach (var x in FilteredItems)
        {
            if (x.IsChecked) count++;
        }

        SelectedCount = count;

        bool allChecked = FilteredItems.Count > 0 && count == FilteredItems.Count;
        IsAllChecked = allChecked;
        IsPartialChecked = count > 0 && !allChecked;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderCheckState))]
    private bool _isAllChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderCheckState))]
    private bool _isPartialChecked;

    /// <summary>
    /// 表头全选复选框的三态值（框架 CheckBox 的 IsThreeState）：
    /// true = 全选 · false = 全不选 · null = 部分选中（横杠）。
    /// </summary>
    public bool? HeaderCheckState => IsAllChecked ? true : (IsPartialChecked ? null : false);

    // ── 详情面板装配 ────────────────────────────────────────────────────
    /// <summary>
    /// 详情面板装配，分两级：
    /// · full=false（实时归并且焦点行未换）：只刷新端口胶囊与徽章。
    /// · full=true（切换进程或手动刷新）：装配进程详情；同进程的其他端口复用已加载的数据。
    ///   子进程树可能变化，强制刷新会显式使这份按需快照失效。
    /// </summary>
    private void RecomputeInspector(bool full = true)
    {
        var item = _focusedItem;
        if (item == null)
        {
            InspApplicationName = "—";
            InspectorVisible = false;
            _inspectorProcess = null;
            _inspectorVersion++;
            return;
        }

        InspectorVisible = true;
        InspPillText = item.StatusBadge;
        InspPillListening = item.IsPillListen;
        InspPillEstablished = item.IsPillEstablished;
        InspPillTimeWait = item.IsPillTimeWait;
        InspPillKernel = item.IsPillKernel;
        InspPillIdle = item.IsPillIdle;
        InspCanKill = !item.IsKernelHeld;
        InspCanDiagnose = item.IsKernelHeld;

        var ports = _detailService.GetListeningPorts(item.Pid, _allItems);
        for (int i = 0; i < ports.Count; i++)
        {
            var chip = ports[i];
            if (i >= InspPorts.Count) InspPorts.Add(chip);
            else if (InspPorts[i].Port != chip.Port || InspPorts[i].StateText != chip.StateText ||
                     InspPorts[i].IsListening != chip.IsListening)
                InspPorts[i] = chip;
        }
        while (InspPorts.Count > ports.Count) InspPorts.RemoveAt(InspPorts.Count - 1);

        InspApplicationName = item.ApplicationNameDisplay;
        if (!full) return;

        InspName = string.IsNullOrWhiteSpace(item.ProcessName) ? "（未知进程）" : item.ProcessName;
        InspMeta = $"PID {item.Pid} · 端口 {item.LocalPort}";

        var process = (item.Pid, item.ProcessName);
        if (_inspectorProcess == process) return;
        _inspectorProcess = process;
        // 新进程加载期间不能把旧进程路径/子树挂在新标题下，复制路径也必须等待新结果。
        InspExePath = "正在读取…";
        InspStartTime = InspParent = "—";
        InspChildren.Clear();
        InspChildCountText = "子进程树 · 正在读取…";
        _ = LoadInspectorAsync(item, ++_inspectorVersion);
    }

    private (int Pid, string Name)? _inspectorProcess;

    /// <summary>进程路径与进程树放到后台查询；只接受当前焦点对应的结果。</summary>
    private async Task LoadInspectorAsync(PortConnectionItem item, int version)
    {
        Interlocked.Increment(ref _inspectorQueries);
        Interlocked.Increment(ref _inspectorPending);
        try
        {
            var details = await Task.Run(() =>
            {
                // 键盘按住 ↑/↓ 时按键重复（~30 次/秒）会连发本任务；开工前先作废检查，
                // 过期任务直接返回，不再跑 Toolhelp32 全量快照。返回值随后仍会被下方校验丢弃。
                if (_shuttingDown || version != _inspectorVersion) return default!;
                var snapshot = _scannerService.GetProcessSnapshotMap();
                return (Path: _processService.GetProcessFullPath(item.Pid),
                    Start: _detailService.GetStartTime(item.Pid),
                    Parent: _detailService.GetParent(item.Pid, snapshot),
                    Children: _detailService.BuildProcessTree(item.Pid, item.ProcessName, snapshot));
            }, _lifetime.Token);
            if (_shuttingDown || version != _inspectorVersion || _focusedItem?.Pid != item.Pid ||
                _focusedItem.ProcessName != item.ProcessName) return;
            InspExePath = details.Path ?? (item.IsTimeWait ? "连接已关闭，无关联可执行文件" : "进程已退出或无读取权限");
            InspStartTime = ProcessDetailService.FormatStartTime(details.Start);
            InspParent = details.Parent.Pid > 0 ? $"{details.Parent.Name} (PID {details.Parent.Pid})" : "已退出 / 无父进程";
            InspChildren.Clear();
            foreach (var node in details.Children) InspChildren.Add(ProcessTreeNodeViewModel.FromSnapshot(node));
            InspChildCountText = $"子进程树 · 共 {Math.Max(0, InspChildren.Count - 1)} 个";
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("进程详情读取失败", ex); }
        finally { Interlocked.Decrement(ref _inspectorPending); }
    }
}
