using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using PortKiller.Features.Ports.Services;
using PortKiller.Models;

namespace PortKiller.ViewModels;

public partial class MainViewModel
{
    private readonly PortConnectionQueryService _connectionQueries = new();
    private readonly PortKiller.Features.Ports.Models.ConnectionRowCache _connectionRowCache = new();
    private readonly PortKiller.Modules.Ports.PortToolServices _portServices;
    private IProcessMetadataProvider? MetadataProvider => _portServices.Metadata;
    private bool _backgroundQueriesEnabled, _queryInFlight, _queryDirty;
    // UI 线程持有：查询被补名、扫描或筛选替换时，尚未提交的用户意图仍需保留。
    private bool _queryBypassesHold, _refreshInspectorPending;
    private int _metadataPostPending;
    internal double LastConnectionCommitMilliseconds { get; private set; }
    internal double LastConnectionMergeMilliseconds { get; private set; }
    internal double LastMetadataPublishMilliseconds { get; private set; }
    private IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> MetadataSnapshot =>
        MetadataProvider?.Snapshot ?? PortsProcessMetadataAdapter.Empty;
    private bool _isApplicationMetadataPending;
    public bool IsApplicationMetadataPending
    {
        get => _isApplicationMetadataPending;
        private set
        {
            if (_isApplicationMetadataPending == value) return;
            _isApplicationMetadataPending = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(EmptySearchDescription));
        }
    }
    public string EmptySearchDescription => IsApplicationMetadataPending
        ? "应用名称补全中，结果将自动更新" : "试试减少关键词，或清除「服务分类 / 状态」筛选条件后重试";

    private void InitializeProcessMetadata()
    {
        if (MetadataProvider != null) MetadataProvider.Changed += OnProcessMetadataChanged;
        _connectionQueries.Completed += OnConnectionQueryCompleted;
    }
    private void OnProcessMetadataChanged()
    {
        if (Interlocked.Exchange(ref _metadataPostPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _metadataPostPending, 0);
            if (_shuttingDown) return;
            if (!IsPortsViewVisible) { _queryDirty = true; return; }
            ApplyProcessMetadata(MetadataSnapshot);
            if (_queryInFlight || !string.IsNullOrWhiteSpace(SearchText) || _sortCol == 7)
            {
                _connectionQueries.Invalidate();
                RequestConnectionFilter(respectHold: true);
            }
        }, DispatcherPriority.Background);
    }
    private void ApplyProcessMetadata(IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> snapshot)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        PortsProcessMetadataAdapter.Apply(FilteredItems, snapshot);
        IsApplicationMetadataPending = PortsProcessMetadataAdapter.IsPending(_allItems, snapshot);
        InspApplicationName = _focusedItem?.ApplicationNameDisplay ?? "—";
        LastMetadataPublishMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    private void OnConnectionQueryCompleted(PortQueryResult result) => Dispatcher.UIThread.Post(() =>
    {
        if (_shuttingDown || result.Version != _connectionQueries.Version) return;
        _queryInFlight = false;
        if (!IsPortsViewVisible) { _queryDirty = true; return; }
        if (result.Query.RespectInteractionHold && IsMergeHeld) { _mergePending = true; return; }
        CommitConnectionQuery(result.Query, result.Items);
    }, DispatcherPriority.Background);

    private void CommitConnectionQuery(PortQuery query, List<PortConnection> rows)
    {
        _queryBypassesHold = false;
        _refreshInspectorPending = false;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var signature = (query.SortColumn, query.Ascending);
        bool sortChanged = signature != _mergeSortSignature;
        _mergeSortSignature = signature;
        MergeFilteredItems(rows, sortChanged);
        LastConnectionMergeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ApplyProcessMetadata(MetadataSnapshot);
        if (query.RefreshInspector) RecomputeInspector();
        LastConnectionCommitMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    private void ResumePortQueries()
    {
        if (IsPortsViewVisible && _queryDirty)
        { _queryDirty = false; ApplyFilter(); }
    }
    internal void EnableBackgroundConnectionQueries() => _backgroundQueriesEnabled = true;
    private void PausePortQueries()
    { _connectionQueries.Invalidate(); _queryDirty = true; _queryInFlight = false; }
    private void DisposeProcessMetadata()
    {
        if (MetadataProvider != null) MetadataProvider.Changed -= OnProcessMetadataChanged;
        _connectionQueries.Completed -= OnConnectionQueryCompleted;
        _connectionQueries.Dispose();
        _connectionRowCache.Clear();
        _portServices.Dispose();
    }
}
