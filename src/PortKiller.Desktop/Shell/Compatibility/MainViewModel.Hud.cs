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

/// <summary>MainViewModel 分部类：桌面悬浮窗 HUD 与托盘。</summary>
public partial class MainViewModel
{
    // ══════════════════════════════════════════════════════════════════
    //  桌面悬浮窗 HUD（设计稿「托盘态与多窗口」：置顶不抢焦点、可选开关）
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty] private bool _isHudVisible;
    [ObservableProperty] private string _hudTotalText = "共 0 个端口";
    public double HudX { get; set; } = double.NaN;
    public double HudY { get; set; } = double.NaN;

    /// <summary>HUD 拖动结束后由视图回调：持久化位置。</summary>
    public void SaveHudPosition(double x, double y)
    {
        HudX = x;
        HudY = y;
        SaveSettings();
    }

    public ObservableCollection<HudRow> HudRows { get; } = new();

    partial void OnIsHudVisibleChanged(bool value)
    {
        if (value) UpdateHudRows();
    }

    private void UpdateHudRows()
    {
        if (!IsHudVisible) return;
        // HUD 悬浮窗代表全机活跃监听概览，直接从全量快照 _allItems 取数，不受主表搜索词影响
        int index = 0;
        foreach (var item in _allItems.Where(x => x.IsListening)
                     .DistinctBy(x => (x.LocalPort, x.Pid))
                     .OrderBy(x => x.LocalPort).Take(4))
        {
            string port = item.LocalPort.ToString();
            string process = item.IsKernelHeld ? $"{item.ProcessName} · 受保护" : item.ProcessName;
            if (index >= HudRows.Count)
                HudRows.Add(new HudRow(port, process, item.ServiceCategory));
            else if (HudRows[index].Port != port || HudRows[index].Process != process ||
                     HudRows[index].Category != item.ServiceCategory)
                HudRows[index] = new HudRow(port, process, item.ServiceCategory);
            index++;
        }
        while (HudRows.Count > index) HudRows.RemoveAt(HudRows.Count - 1);

        HudTotalText = $"共 {TotalCount} 个端口 · {ListeningCount} 个监听";
    }

    [RelayCommand]
    public void ToggleHud()
    {
        IsHudVisible = !IsHudVisible;
        OnPropertyChanged(nameof(IsTrayPauseChecked));
        SaveSettings();
    }

    /// <summary>托盘菜单：暂停后台实时监听（与设置页的自动刷新同一开关）。</summary>
    [RelayCommand]
    public void ToggleWatchFromTray()
    {
        IsAutoRefresh = !IsAutoRefresh;
        OnPropertyChanged(nameof(IsTrayPauseChecked));
        ShowToast(IsAutoRefresh ? "已恢复后台实时监听" : "已暂停后台实时监听",
            IsAutoRefresh ? $"FNV-1a 拓扑指纹监听已恢复 · 间隔 {SelectedInterval}" : "拓扑指纹监听已暂停，可随时从托盘恢复");
    }

    public bool IsTrayPauseChecked => !IsAutoRefresh;

    [RelayCommand]
    public void ShowMainFromTray()
    {
        RequestShowMainWindow?.Invoke();
    }

    [RelayCommand]
    public void OpenSettingsFromTray()
    {
        CurrentView = 2;
        RequestShowMainWindow?.Invoke();
    }

    [RelayCommand]
    public async Task ReleaseAllFromTrayAsync()
    {
        RequestShowMainWindow?.Invoke();
        await ReleaseAllTimeWaitAsync();
    }

    /// <summary>视图层订阅：从托盘把主窗口拉回前台。</summary>
    public event Action? RequestShowMainWindow;
}
