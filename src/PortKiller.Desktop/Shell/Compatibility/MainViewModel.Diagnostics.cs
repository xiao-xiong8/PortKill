using System.Threading;
using PortKiller.Services;

namespace PortKiller.ViewModels;

public partial class MainViewModel
{
    private long _publishedScans, _changedPublishes, _mergeCount, _inspectorQueries, _inspectorPending;

    /// <summary>UI 线程每五秒读取标量；不保存连接、详情、字体或可视树的历史引用。</summary>
    internal MemoryDiagnosticState CaptureMemoryDiagnosticState() => new(
        _allItems.Count, FilteredItems.Count, InspChildren.Count, InspPorts.Count, HudRows.Count, KernelItems.Count,
        IsAutoRefresh, IsScanning, HasScanError, _listHoldCount, _mergePending, CurrentView, IsHudVisible,
        false, false, 0, _scans.CompletedScans, _scans.LastScanMilliseconds, _publishedScans,
        _changedPublishes, _mergeCount, Interlocked.Read(ref _inspectorQueries), Interlocked.Read(ref _inspectorPending));
}
