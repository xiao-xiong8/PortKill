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

    private double[] _colWidths = { 96, 132, 132, 0, 78, 180, 132, 144 }; // 0 = 星号列（进程名）
    private int[] _colOrder = { 0, 1, 2, 7, 3, 4, 5, 6 };
    private bool[] _colVisible = { true, true, true, true, true, true, true, true };
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
    public double TableMinWidth => 60 + Enumerable.Range(0, ColumnSettingsMigrator.Count)
        .Where(i => _colVisible[i]).Sum(i => i == 3 ? Math.Max(140, _colWidths[i]) : Math.Max(ColMinWidths[i], _colWidths[i]));

    private GridLength ColWidthAt(int position)
    {
        int logical = _colOrder[position];
        if (!_colVisible[logical]) return new GridLength(0);
        if (_colWidths[logical] == 0) return new GridLength(1, GridUnitType.Star);
        return new GridLength(_colWidths[logical]);
    }

    private void RebuildColumns()
    {
        OnPropertyChanged(nameof(TableMinWidth));
        for (int i = 0; i < ColumnSettingsMigrator.Count; i++)
        {
            OnPropertyChanged($"ColWidth{i}");
            OnPropertyChanged($"ColIdx{i}");
        }
    }

    /// <summary>校验旧配置，拒绝非有限宽度和重复列索引以防布局异常。</summary>
    private void InitColumnsFromSettings(AppSettings settings)
    {
        ColumnSettingsMigrator.Migrate(settings);
        _colWidths = settings.ColWidths.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        _colOrder = settings.ColOrder.Split(',').Select(int.Parse).ToArray();
        _colVisible = settings.ColVisible.Split(',').Select(bool.Parse).ToArray();
        for (int i = 0; i < _colWidths.Length; i++)
            if (i != 3 || _colWidths[i] > 0) _colWidths[i] = Math.Max(ColMinWidths[i], _colWidths[i]);
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
        if (logical == 3) return; // 星号列宽度由窗口分配
        if (!double.IsFinite(width)) return;
        _colWidths[logical] = Math.Clamp(Math.Round(width), ColMinWidths[logical], ColumnSettingsMigrator.MaximumWidth);
        RebuildColumns();
    }

    /// <summary>列是否已拖到最小宽度（手柄转琥珀色提示「已到下限」）。</summary>
    public bool IsColAtMin(int logical) => _colWidths[logical] <= ColMinWidths[logical] + 0.5;

    /// <summary>列顺序拖拽落位：把 logical 移到数据列的第 targetPos 位。</summary>
    public void ReorderColumn(int logical, int targetPos)
    {
        var list = _colOrder.ToList();
        list.Remove(logical);
        list.Insert(Math.Clamp(targetPos, 0, ColumnSettingsMigrator.Count - 1), logical);
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
        _colWidths = new double[] { 96, 132, 132, 0, 78, 180, 132, 144 };
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
