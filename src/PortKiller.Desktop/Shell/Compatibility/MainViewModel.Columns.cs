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

/// <summary>MainViewModel 分部类：表格列系统（列宽 / 列序 / 可见性 / 排序）。</summary>
public partial class MainViewModel
{
    // ══════════════════════════════════════════════════════════════════
    //  表格列系统 · 列宽拖拽 / 列顺序 / 可见性 / 列排序
    //  （设计稿「列宽拖拽与列顺序」「表格交互增强」画板）
    // ══════════════════════════════════════════════════════════════════

    public static readonly string[] ColTitles = { "端口", "状态", "服务分类", "进程名", "PID", "本地地址", "操作", "应用名称" };

    /// <summary>列设置浮层是否展开（窗内浮层，替代原先落点不可控的 Popup）。</summary>
    [ObservableProperty]
    private bool _isColumnMenuOpen;

    /// <summary>MinWidth 保护规格（拖到底手柄停住并转琥珀色）。操作列不可拖。</summary>
    public static readonly double[] ColMinWidths = { 88, 120, 118, 140, 72, 150, 116, 104 };

    private const double SelectionColumnWidth = 44;
    private const double TableTrailingGap = 16;
    private double[] _colWidths = { 96, 132, 132, 220, 78, 180, 132, 144 };
    private int[] _colOrder = { 0, 1, 2, 7, 3, 4, 5, 6 };
    private bool[] _colVisible = { true, true, true, true, true, true, true, true };
    // 只保留可见列的显示顺序。表头和数据行都通过这一个快照取 Grid.Column，
    // 隐藏列不会再留下零宽占位，列宽与内容因此始终使用同一套坐标系。
    private int[] _displayOrder = { 0, 1, 2, 7, 3, 4, 5, 6 };
    // 每次列顺序或可见性拓扑改变时递增。虚拟化行据此整体重建，
    // 避免 Avalonia Grid 在旧模板缓存上执行动态列重排。
    private int _columnLayoutVersion;
    // 列宽变化只需要重新测量行，不应伪装成列拓扑变化。
    private int _columnMeasureVersion;
    private int _sortCol;
    private bool _sortAsc = true;

    /// <summary>
    /// 表头与数据行共享的列宽（按「显示位置」索引）。
    /// Avalonia 的 Grid.ColumnDefinitions 不支持整体绑定，因此逐列暴露 ColWidth0..7，
    /// 由 XAML 显式声明 9 个 ColumnDefinition 后分别绑定。
    /// </summary>
    public GridLength ColWidth0 => ColWidthAt(0);
    public GridLength ColWidth1 => ColWidthAt(1);
    public GridLength ColWidth2 => ColWidthAt(2);
    public GridLength ColWidth3 => ColWidthAt(3);
    public GridLength ColWidth4 => ColWidthAt(4);
    public GridLength ColWidth5 => ColWidthAt(5);
    public GridLength ColWidth6 => ColWidthAt(6);

    public GridLength ColWidth7 => ColWidthAt(7);
    public int ColumnLayoutVersion => _columnLayoutVersion;
    public int ColumnMeasureVersion => _columnMeasureVersion;
    public double TableMinWidth => SelectionColumnWidth + TableTrailingGap + _displayOrder.Sum(MinimumColumnWidth);

    private double MinimumColumnWidth(int logical)
        => Math.Max(ColMinWidths[logical], _colWidths[logical]);

    private GridLength ColWidthAt(int position)
    {
        if (position < 0 || position >= _displayOrder.Length) return new GridLength(0);
        int logical = _displayOrder[position];
        return new GridLength(MinimumColumnWidth(logical));
    }

    private void RebuildColumns()
    {
        var nextDisplayOrder = _colOrder.Where(i => i >= 0 && i < _colVisible.Length && _colVisible[i]).ToArray();
        bool topologyChanged = !_displayOrder.SequenceEqual(nextDisplayOrder);
        _displayOrder = nextDisplayOrder;
        if (topologyChanged)
        {
            _columnLayoutVersion++;
            OnPropertyChanged(nameof(ColumnLayoutVersion));
        }
        if (topologyChanged)
        {
            _columnMeasureVersion++;
            OnPropertyChanged(nameof(ColumnMeasureVersion));
        }
        for (int i = 0; i < ColumnSettingsMigrator.Count; i++)
        {
            OnPropertyChanged($"ColWidth{i}");
            OnPropertyChanged($"ColIdx{i}");
        }
        // 先让表头和已实现行收到新的列宽，再更新滚动内容的最小宽度；
        // 反过来会让虚拟化面板在旧 Grid 定义上进入一次 Arrange，Avalonia 12 可能抛 NRE。
        OnPropertyChanged(nameof(TableMinWidth));
    }

    /// <summary>校验旧配置，拒绝非有限宽度和重复列索引以防布局异常。</summary>
    private void InitColumnsFromSettings(AppSettings settings)
    {
        ColumnSettingsMigrator.Migrate(settings);
        _colWidths = settings.ColWidths.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        _colOrder = settings.ColOrder.Split(',').Select(int.Parse).ToArray();
        _colVisible = settings.ColVisible.Split(',').Select(bool.Parse).ToArray();
        for (int i = 0; i < _colWidths.Length; i++)
            _colWidths[i] = Math.Max(ColMinWidths[i], _colWidths[i]);
        RebuildColumns();
        NotifyColVisible();
    }

    public void SaveColumnState()
    {
        SaveSettings();
    }

    private void PersistColumns()
    {
        SaveSettings();
    }

    /// <summary>逻辑列的可见位置（0 = 勾选列之后第一列），供单元格 Grid.Column 绑定。</summary>
    private int PositionOf(int logical)
    {
        for (int i = 0; i < _displayOrder.Length; i++)
            if (_displayOrder[i] == logical) return i;
        // 隐藏列不参与显示坐标；保留其原始顺序槽位，避免切换可见性时
        // 让已隐藏控件也移动一次 Grid.Column，虚拟化行可平滑完成重排。
        for (int i = 0; i < _colOrder.Length; i++)
            if (_colOrder[i] == logical) return i;
        return 0;
    }

    public int ColIdx0 => 1 + PositionOf(0);
    public int ColIdx1 => 1 + PositionOf(1);
    public int ColIdx2 => 1 + PositionOf(2);
    public int ColIdx3 => 1 + PositionOf(3);
    public int ColIdx4 => 1 + PositionOf(4);
    public int ColIdx5 => 1 + PositionOf(5);
    public int ColIdx6 => 1 + PositionOf(6);
    public int ColIdx7 => 1 + PositionOf(7);

    public double GetColWidth(int logical) => _colWidths[logical];
    public double GetColMin(int logical) => ColMinWidths[logical];
    public bool IsColVisible(int logical) => _colVisible[logical];

    public bool IsColVisible0 => _colVisible[0];
    public bool IsColVisible1 => _colVisible[1];
    public bool IsColVisible2 => _colVisible[2];
    public bool IsColVisible3 => _colVisible[3];
    public bool IsColVisible4 => _colVisible[4];
    public bool IsColVisible5 => _colVisible[5];
    public bool IsColVisible6 => _colVisible[6];
    public bool IsColVisible7 => _colVisible[7];

    /// <summary>拖拽过程中实时写入列宽（不落盘）；松手时由视图调用 SaveColumnState() 持久化。</summary>
    public void SetColumnWidth(int logical, double width)
    {
        if (logical < 0 || logical >= ColumnSettingsMigrator.Count) return;
        if (!double.IsFinite(width)) return;
        double next = Math.Clamp(Math.Round(width), ColMinWidths[logical], ColumnSettingsMigrator.MaximumWidth);
        if (Math.Abs(_colWidths[logical] - next) < 0.1) return;

        _colWidths[logical] = next;
        int position = Array.IndexOf(_displayOrder, logical);
        if (position >= 0)
            OnPropertyChanged($"ColWidth{position}");
        _columnMeasureVersion++;
        OnPropertyChanged(nameof(ColumnMeasureVersion));
        OnPropertyChanged(nameof(TableMinWidth));
    }

    /// <summary>列是否已拖到最小宽度（手柄转琥珀色提示「已到下限」）。</summary>
    public bool IsColAtMin(int logical) => _colWidths[logical] <= ColMinWidths[logical] + 0.5;

    /// <summary>列顺序拖拽落位：把 logical 移到数据列的第 targetPos 位。</summary>
    public void ReorderColumn(int logical, int targetPos)
    {
        if (logical < 0 || logical >= ColumnSettingsMigrator.Count || !_colVisible[logical]) return;
        var list = _colOrder.ToList();
        list.Remove(logical);
        int visibleCount = list.Count(i => _colVisible[i]);
        int visibleTarget = Math.Clamp(targetPos, 0, visibleCount);
        int insertAt = list.Count;
        int seen = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!_colVisible[list[i]]) continue;
            if (seen++ >= visibleTarget)
            {
                insertAt = i;
                break;
            }
        }
        list.Insert(insertAt, logical);
        _colOrder = list.ToArray();
        RebuildColumns();
        PersistColumns();
    }

    /// <summary>表头点击排序：同列反复点击在升 / 降序间切换（设计稿三态指示器）。</summary>
    public void ToggleSort(int logical)
    {
        if (logical == 6) return; // 操作列不可排序
        if (_sortCol == logical) _sortAsc = !_sortAsc;
        else { _sortCol = logical; _sortAsc = true; }
        NotifySortState();
        ApplyFilter();
    }

    private void NotifySortState()
    {
        for (int i = 0; i < ColumnSettingsMigrator.Count; i++)
        {
            OnPropertyChanged($"IsSort{i}Asc");
            OnPropertyChanged($"IsSort{i}Desc");
            OnPropertyChanged($"IsSort{i}None");
        }
    }

    public bool IsSort0None => _sortCol != 0;
    public bool IsSort1None => _sortCol != 1;
    public bool IsSort2None => _sortCol != 2;
    public bool IsSort3None => _sortCol != 3;
    public bool IsSort4None => _sortCol != 4;
    public bool IsSort5None => _sortCol != 5;
    public bool IsSort6None => _sortCol != 6;
    public bool IsSort7None => _sortCol != 7;

    private void NotifyColVisible()
    {
        for (int i = 0; i < ColumnSettingsMigrator.Count; i++) OnPropertyChanged($"IsColVisible{i}");
    }

    public bool IsSort0Asc => _sortCol == 0 && _sortAsc;
    public bool IsSort0Desc => _sortCol == 0 && !_sortAsc;
    public bool IsSort1Asc => _sortCol == 1 && _sortAsc;
    public bool IsSort1Desc => _sortCol == 1 && !_sortAsc;
    public bool IsSort2Asc => _sortCol == 2 && _sortAsc;
    public bool IsSort2Desc => _sortCol == 2 && !_sortAsc;
    public bool IsSort3Asc => _sortCol == 3 && _sortAsc;
    public bool IsSort3Desc => _sortCol == 3 && !_sortAsc;
    public bool IsSort4Asc => _sortCol == 4 && _sortAsc;
    public bool IsSort4Desc => _sortCol == 4 && !_sortAsc;
    public bool IsSort5Asc => _sortCol == 5 && _sortAsc;
    public bool IsSort5Desc => _sortCol == 5 && !_sortAsc;
    public bool IsSort6Asc => _sortCol == 6 && _sortAsc;
    public bool IsSort6Desc => _sortCol == 6 && !_sortAsc;
    public bool IsSort7Asc => _sortCol == 7 && _sortAsc;
    public bool IsSort7Desc => _sortCol == 7 && !_sortAsc;

    [RelayCommand]
    public void ToggleColVisible(string index)
    {
        if (!int.TryParse(index, out int i) || i < 0 || i >= ColumnSettingsMigrator.Count) return;
        ToggleColumnVisibility(i, persist: true);
    }

    internal void ToggleColumnVisibility(int i, bool persist)
    {
        if (i < 0 || i >= ColumnSettingsMigrator.Count) return;
        _colVisible[i] = !_colVisible[i];
        RebuildColumns();
        NotifyColVisible();
        if (persist) PersistColumns();
    }

    [RelayCommand]
    public void ResetColumns()
    {
        _colWidths = new double[] { 96, 132, 132, 220, 78, 180, 132, 144 };
        _colOrder = new[] { 0, 1, 2, 7, 3, 4, 5, 6 };
        _colVisible = new[] { true, true, true, true, true, true, true, true };
        RebuildColumns();
        NotifyColVisible();
        PersistColumns();
        ShowToast("已重置列布局", "列宽与顺序已恢复设计稿默认值");
    }

    /// <summary>精简列模式：折叠「服务分类」与「本地地址」两列。</summary>
    [RelayCommand]
    public void ToggleColumnMode()
    {
        bool target = !(_colVisible[2] && _colVisible[5]);
        _colVisible[2] = target;
        _colVisible[5] = target;
        RebuildColumns();
        NotifyColVisible();
        PersistColumns();
        ShowToast(
            target ? "已切换为完整列" : "已切换为精简列",
            target ? "已展开服务分类与本地地址列" : "已折叠「服务分类」与「本地地址」，为进程名让出更多宽度");
    }
}
