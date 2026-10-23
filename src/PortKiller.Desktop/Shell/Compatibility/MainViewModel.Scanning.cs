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

/// <summary>MainViewModel 分部类：扫描与筛选（实时监听 / 增量归并 / 搜索排序）。</summary>
public partial class MainViewModel
{
    // ── 实时监听引擎 ────────────────────────────────────────────────────
    /// <summary>后台实时扫描进行中标志：扫描耗时超过定时间隔时防止 tick 叠加。</summary>


    /// <summary>实时监听连续失败计数：静默停摆必须可感知，连败达阈值上浮到状态栏。</summary>


    private void InitLiveWatcher()
    {
        _liveTimer = new DispatcherTimer();
        UpdateTimerInterval();
        _liveTimer.Tick += async (_, _) =>
        {
            if (IsAutoRefresh && !IsScanning && !_shuttingDown)
            {
                await BackgroundLiveScanAsync();
            }
        };
        _liveTimer.Start();
    }

    private void UpdateTimerInterval()
    {
        if (_liveTimer == null) return;
        int sec = SelectedInterval switch
        {
            "2 秒" => 2,
            "3 秒" => 3,
            _ => 1
        };
        _liveTimer.Interval = TimeSpan.FromSeconds(sec);
    }

    /// <summary>实时扫描与手动刷新共用一次更新流程。</summary>
    private Task BackgroundLiveScanAsync() => RefreshCoreAsync(false);

    [RelayCommand]
    public Task Refresh() => RefreshCoreAsync(true);

    /// <summary>合并重复请求；强制刷新遇到在途扫描时补一轮最新快照。</summary>
    public async Task RefreshCoreAsync(bool force)
    {
        if (_shuttingDown) return;
        if (_refreshTask is { IsCompleted: false })
        {
            _refreshRequested |= force;
            await _refreshTask;
            return;
        }
        _refreshTask = RefreshLoopAsync(force);
        await _refreshTask;
    }

    /// <summary>扫描后台执行，发布前检查退出状态；失败保留原列表并标记错误。</summary>
    private async Task RefreshLoopAsync(bool force)
    {
        IsScanning = true;
        try
        {
            do
            {
                _refreshRequested = false;
                var result = await _scans.ScanAsync(force ? 0 : _lastFingerprint, _lifetime.Token);
                if (_shuttingDown) return;
                await Dispatcher.UIThread.InvokeAsync(() => ApplyScanResult(result, force), DispatcherPriority.Background);
                force = true;
            } while (_refreshRequested && !_shuttingDown);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex)
        {
            if (_shuttingDown) return;
            HasScanError = true;
            ScanErrorTitle = "扫描失败，当前列表可能已过期";
            ScanErrorDescription = ex.GetBaseException().Message;
            ReportFailure("扫描失败", ex);
        }
        finally { if (!_shuttingDown) IsScanning = false; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  交互静默闸口：鼠标悬停列表 / 拖动滚动 / 右键菜单打开期间，后台扫描照常跑
    //  （状态栏计数持续更新），但「列表归并」推迟到交互结束 —— 归并的
    //  Remove/Insert/Move 会让虚拟化面板重建容器，正是悬停行 hover 背景闪烁、
    //  右键菜单 anchor 被回收导致菜单消失、焦点行被清空导致菜单动作静默无效
    //  这三个问题的共同根因。计数制以支持悬停 + 菜单等来源交叠。
    // ══════════════════════════════════════════════════════════════════

    private int _listHoldCount;
    private bool _mergePending;

    /// <summary>View 侧配对调用：进入交互（悬停/滚动/菜单打开）+1，结束 -1；归零时补做被推迟的归并。</summary>
    public void SetListInteractionHold(bool held)
    {
        int before = _listHoldCount;
        _listHoldCount = Math.Max(0, _listHoldCount + (held ? 1 : -1));
        if (before > 0 && _listHoldCount == 0 && _mergePending)
        {
            _mergePending = false;
            ApplyFilter();
            MaybeRefreshKernelView();
        }
    }

    private bool IsMergeHeld => _listHoldCount > 0;

    /// <summary>只在 UI 线程提交快照，未变化时保留现有集合和选择状态。</summary>
    internal void ApplyScanResult(ScanResult result, bool force)
    {
        if (_shuttingDown) return;
        bool changed = result.HasChanged || HasScanError || force;
        _publishedScans++;
        if (changed) _changedPublishes++;
        _lastFingerprint = result.Fingerprint;
        _allItems = result.Items;
        if (changed) _connectionRowCache.UpdateSnapshot(result.Items);
        if (changed) _connectionQueries.Invalidate();
        ApplyProcessMetadata(MetadataSnapshot);
        TotalCount = result.TotalCount;
        ListeningCount = result.ListeningCount;
        EnteredCountCore(result.Items);
        ScanDurationText = $"{result.ElapsedMilliseconds:F1} ms";
        LastScanAtText = DateTime.Now.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(LastScanSummary));
        HasScanError = false;
        ScanErrorTitle = ScanErrorDescription = string.Empty;
        if (changed)
        {
            UpdateHudRows();
            // 交互静默期：归并推迟到闸口归零时补做；手动强制刷新（F5/刷新按钮）不受限
            if (!force && IsMergeHeld && !_queryBypassesHold) _mergePending = true;
            else
            {
                _mergePending = false;
                if (force) _inspectorProcess = null;
                RequestConnectionFilter(respectHold: !force, refreshInspector: force);
                MaybeRefreshKernelView();
            }
        }
        StatusMessage = $"扫描完成：{TotalCount} 条连接，监听 {ListeningCount}，采集耗时 {ScanDurationText}";
    }

    [RelayCommand]
    public Task RetryScanAsync() => RefreshCoreAsync(true);

    /// <summary>统计已连接 / 等待释放 / 内核占用三类计数（单遍，原先三次 LINQ 全表）。</summary>
    private void EnteredCountCore(IReadOnlyList<PortConnection> items)
    {
        int est = 0, tw = 0, kern = 0;
        foreach (var x in items)
        {
            if (x.IsEstablished) est++;
            else if (x.IsTimeWait) tw++;
            if (x.IsKernelHeld && (x.IsListening || x.IsHttpSys)) kern++;
        }
        EstablishedCount = est;
        TimeWaitCount = tw;
        KernelCount = kern;
    }

    // ── 筛选 ────────────────────────────────────────────────────────────
    partial void OnSearchTextChanged(string value)
    {
        _connectionQueries.Invalidate();
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        _ = DebounceSearchAsync(_searchDebounceCts.Token);
    }

    /// <summary>80ms 防抖。async/await 替代 ContinueWith：异常可观察、回到 UI 线程无需 Post。</summary>
    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token);
            if (!_shuttingDown) ApplyFilter();
        }
        catch (OperationCanceledException) { }
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        ApplyFilter();
        SyncStatActives();
    }

    partial void OnSelectedStateChanged(string value)
    {
        ApplyFilter();
        SyncStatActives();
    }

    // ── 统计卡片快速筛选 ──────────────────────────────────────────────
    // 激活态与状态/分类下拉双向同步：点卡片切换筛选，手动改下拉同样刷新高亮。
    public bool IsStatAllActive => SelectedState == "全部状态" && SelectedCategory == "全部分类";
    public bool IsStatListenActive => SelectedState == "LISTENING";
    public bool IsStatEstablishedActive => SelectedState == "ESTABLISHED";
    public bool IsStatTimeWaitActive => SelectedState == "TIME_WAIT";
    public bool IsStatKernelActive => SelectedCategory == "内核服务";

    /// <summary>点击统计卡片切换筛选；点击已激活的卡片回到「全部」。</summary>
    [RelayCommand]
    private void FilterByStat(string? kind)
    {
        switch (kind)
        {
            case "listen": SelectedState = IsStatListenActive ? "全部状态" : "LISTENING"; break;
            case "established": SelectedState = IsStatEstablishedActive ? "全部状态" : "ESTABLISHED"; break;
            case "timewait": SelectedState = IsStatTimeWaitActive ? "全部状态" : "TIME_WAIT"; break;
            case "kernel": SelectedCategory = IsStatKernelActive ? "全部分类" : "内核服务"; break;
            default:
                SelectedState = "全部状态";
                SelectedCategory = "全部分类";
                break;
        }
    }

    private void SyncStatActives()
    {
        OnPropertyChanged(nameof(IsStatAllActive));
        OnPropertyChanged(nameof(IsStatListenActive));
        OnPropertyChanged(nameof(IsStatEstablishedActive));
        OnPropertyChanged(nameof(IsStatTimeWaitActive));
        OnPropertyChanged(nameof(IsStatKernelActive));
    }

    // ── 系统状态的联动失效 ──────────────────────────────────────────────
    partial void OnTotalCountChanged(int value) { Shell.UpdatePortCount(value); NotifySystemStates(); }
    partial void OnFilteredCountChanged(int value) => NotifySystemStates();
    partial void OnIsScanningChanged(bool value) => NotifySystemStates();
    partial void OnHasScanErrorChanged(bool value) => NotifySystemStates();
    partial void OnScanDurationTextChanged(string value) => OnPropertyChanged(nameof(SkeletonSubText));

    private void NotifySystemStates()
    {
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowAllClearState));
        OnPropertyChanged(nameof(SkeletonSubText));
    }

    // 归并使用 PortConnectionItem.Identity；排序与筛选始终基于最新扫描对象。

    [RelayCommand]
    public void ApplyFilter() => RequestConnectionFilter();

    private void RequestConnectionFilter(bool respectHold = false, bool refreshInspector = false)
    {
        if (_shuttingDown) return;
        _queryBypassesHold |= !respectHold;
        _refreshInspectorPending |= refreshInspector;
        if (_backgroundQueriesEnabled && !IsPortsViewVisible) { _queryDirty = true; return; }
        if (!_queryBypassesHold && IsMergeHeld) { _mergePending = true; return; }
        var query = new PortKiller.Features.Ports.Services.PortQuery(_allItems, MetadataSnapshot,
            SearchText, SelectedCategory, SelectedState, _sortCol, _sortAsc, !_queryBypassesHold, _refreshInspectorPending);
        if (_backgroundQueriesEnabled)
        {
            _queryInFlight = true;
            _connectionQueries.Enqueue(query);
        }
        else CommitConnectionQuery(query, PortKiller.Features.Ports.Services.PortConnectionQueryService.Evaluate(query));
    }

    private bool _initialFocusApplied;

    // ══════════════════════════════════════════════════════════════════
    //  增量归并：减少集合和可视行模板重建；身份匹配仍需线性遍历，筛选排序也处理全表。
    //  小批变化局部增删，大批变化范围替换；始终保留 ItemsSource 和可视行模板。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>超过阈值改用范围通知，避免大量逐条增删；不再替换集合。</summary>
    private const int SurgicalMergeLimit = 48;

    /// <summary>排序变化改用范围更新，避免逐行 Move 的平方级开销。</summary>
    private (int Col, bool Asc) _mergeSortSignature = (-1, true);

    private void MergeFilteredItems(List<PortConnection> ordered, bool sortChanged)
    {
        _mergeCount++;
        var old = FilteredItems;
        var focusBefore = _focusedItem; // 归并后对比引用：焦点行被沿用则详情面板走轻量路径

        // 类型键避免每秒为每行拼接 RowKey；索引链仅为重复身份保留一条 int 链。
        var reuse = new Dictionary<PortConnectionItem.ConnectionIdentity, int>(old.Count);
        var nextDuplicate = new int[old.Count];
        for (int i = old.Count - 1; i >= 0; i--)
        {
            var key = old[i].Identity;
            nextDuplicate[i] = reuse.TryGetValue(key, out int next) ? next : -1;
            reuse[key] = i;
        }

        // 2. 身份相同的行同步状态并复用，保留勾选/焦点；重复身份逐条匹配。
        var target = new List<PortConnectionItem>(ordered.Count);
        var fresh = new List<PortConnectionItem>();
        bool dataChanged = false;
        bool orderChanged = false;
        int previousIndex = -1;
        foreach (var desired in ordered)
        {
            var key = desired.Identity;
            if (reuse.TryGetValue(key, out int index) && index >= 0)
            {
                var item = old[index];
                orderChanged |= index < previousIndex;
                previousIndex = index;
                reuse[key] = nextDuplicate[index];
                dataChanged |= item.ApplySnapshot(desired);
                target.Add(item);
            }
            else
            {
                var row = _connectionRowCache.Take(desired) ?? PortConnectionItem.FromSnapshot(desired);
                target.Add(row);
                fresh.Add(row);
            }
        }

        // 3. 未被复用的旧行 = 真正消失的连接：退订事件；焦点行消失则清空焦点
        var targetSet = new HashSet<PortConnectionItem>(target);
        int removedCount = 0;
        foreach (var item in old)
        {
            if (!targetSet.Contains(item))
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                if (ReferenceEquals(_focusedItem, item)) { item.IsFocused = false; _focusedItem = null; }
                _connectionRowCache.Store(item);
                removedCount++;
            }
        }

        // 4. 顺序和引用一致时不发集合通知，属性变化已在 ApplySnapshot 通知。
        if (old.Count == target.Count)
        {
            bool identical = true;
            for (int i = 0; i < old.Count; i++)
            {
                if (!ReferenceEquals(old[i], target[i])) { identical = false; break; }
            }
            if (identical)
            {
                FilteredCount = old.Count;
                // 行引用不变也可能已更新状态；详情和 HUD 必须反映最新快照。
                RecomputeInspector(full: false);
                if (dataChanged) RecomputeSelection();
                return;
            }
        }

        // 5. 状态/分类等可变字段参与排序；检测共同行相对次序，变序时范围替换。
        //    只有相对次序未变的小批增删才逐条通知。
        if (sortChanged || orderChanged || removedCount + fresh.Count > SurgicalMergeLimit)
        {
            // 保留集合，ConnectionViewportPanel 原地换绑可视行槽位。
            foreach (var item in fresh)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                item.PropertyChanged += OnItemPropertyChanged;
            }
            old.ApplySnapshot(target);
        }
        else
        {
            // 集合差归并：先按引用删除消失的行（共同行相对顺序不变），
            // 再按目标下标升序插入新增行 —— 插入后 old 与 target 逐位一致
            for (int i = old.Count - 1; i >= 0; i--)
            {
                if (!targetSet.Contains(old[i])) old.RemoveAt(i);
            }

            var freshSet = new HashSet<PortConnectionItem>(fresh);
            for (int i = 0; i < target.Count; i++)
            {
                if (!freshSet.Contains(target[i])) continue;
                old.Insert(i, target[i]);
                target[i].PropertyChanged -= OnItemPropertyChanged;
                target[i].PropertyChanged += OnItemPropertyChanged;
            }
        }

        FilteredCount = target.Count;

        // 6. 首次载入自动聚焦第一条监听记录，让右侧详情面板立即有内容
        if (!_initialFocusApplied && _focusedItem == null && target.Count > 0)
        {
            var seed = target.FirstOrDefault(x => x.IsListening) ?? target[0];
            seed.IsFocused = true;
            _focusedItem = seed;
            _initialFocusApplied = true;
        }

        // 自动刷新沿用按需加载的进程详情；手动强制刷新在 ApplyScanResult 中使其失效。
        RecomputeInspector(full: !ReferenceEquals(focusBefore, _focusedItem));
        RecomputeSelection();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_updatingChecks && e.PropertyName == nameof(PortConnectionItem.IsChecked))
        {
            RecomputeSelection();
        }
    }

    private static bool TryParsePortRange(string input, out int min, out int max)
    {
        min = 0;
        max = 0;
        var parts = input.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out min) && int.TryParse(parts[1], out max))
        {
            if (min > max) (min, max) = (max, min);
            return true;
        }
        return false;
    }
}
