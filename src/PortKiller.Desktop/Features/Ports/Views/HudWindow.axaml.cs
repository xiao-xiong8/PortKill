using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PortKiller.Models;
using PortKiller.ViewModels;

namespace PortKiller.Views;

/// <summary>
/// 桌面悬浮窗 HUD（设计稿「托盘态与多窗口」344×内容高）。
/// 置顶但不抢焦点（ShowActivated=false），点击行 / 双击底栏唤起主窗口，标题栏可拖动。
/// </summary>
public partial class HudWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public HudWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => { /* 保持置顶即可，不做任何抢焦点处理 */ };
    }

    /// <summary>按持久化位置摆放；无记录时落到工作区右下角，并对多屏/改分辨率做边界夹紧。</summary>
    public void PositionHud(double x, double y)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (!double.IsNaN(x) && !double.IsNaN(y))
        {
            int targetX = (int)x;
            int targetY = (int)y;
            var hitScreen = Screens.ScreenFromPoint(new PixelPoint(targetX, targetY)) ?? screen;
            if (hitScreen is not null)
            {
                var wa = hitScreen.WorkingArea;
                double scale = hitScreen.Scaling;
                int w = Math.Max(180, (int)(344 * scale));
                int h = Math.Max(120, (int)((double.IsNaN(Height) || Height <= 0 ? 220 : Height) * scale));
                int maxX = Math.Max(wa.X, wa.X + wa.Width - w);
                int maxY = Math.Max(wa.Y, wa.Y + wa.Height - h);
                targetX = Math.Clamp(targetX, wa.X, maxX);
                targetY = Math.Clamp(targetY, wa.Y, maxY);
            }
            Position = new PixelPoint(targetX, targetY);
            return;
        }

        if (screen is null) return;

        var workArea = screen.WorkingArea;
        double dpiScale = screen.Scaling;
        int widthPx = (int)(344 * dpiScale);
        int heightPx = (int)((double.IsNaN(Height) || Height <= 0 ? 260 : Height) * dpiScale);
        Position = new PixelPoint(
            workArea.X + workArea.Width - widthPx - 24,
            workArea.Y + workArea.Height - heightPx - 24);
    }

    private void OnTitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            SavePosition();
        }
    }

    private void OnCloseHud(object? sender, RoutedEventArgs e)
    {
        SavePosition();
        if (Vm != null) Vm.IsHudVisible = false;
    }

    /// <summary>点击端口行 → 唤起主窗口（HUD 本身不抢焦点语义交给系统处理）。</summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.ShowMainFromTrayCommand.Execute(null);
    }

    private void OnExpandMain(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowMainFromTrayCommand.Execute(null);
    }

    private void SavePosition()
    {
        if (Vm == null) return;
        Vm.SaveHudPosition(Position.X, Position.Y);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 关闭动作一律转成隐藏，由 IsHudVisible 状态驱动
        SavePosition();
        if (Vm is { IsHudVisible: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
