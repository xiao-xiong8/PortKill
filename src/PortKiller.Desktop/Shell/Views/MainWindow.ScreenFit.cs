using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PortKiller.Views;

public partial class MainWindow
{
    private DispatcherTimer? _screenFitTimer;
    private PixelRect _lastWorkingArea;
    private double _lastScreenScale;
    private bool _screenFitAttached;
    private bool _applyingScreenFit;

    private void InitializeScreenFit()
    {
        // 在 Show 前限制首帧尺寸，避免先显示超大窗口再缩小。
        if (Screens.Primary is { } screen) FitToScreen(screen, startup: true);
        Opened += (_, _) =>
        {
            if (!_screenFitAttached)
            {
                Screens.Changed += OnScreensChanged;
                ScalingChanged += OnWindowScalingChanged;
                PositionChanged += OnScreenPositionChanged;
                _screenFitAttached = true;
            }
            EnsureScreenFit();
        };
        Closed += (_, _) => StopScreenFit();
    }

    private void OnScreensChanged(object? sender, EventArgs e) => QueueScreenFit();
    private void OnWindowScalingChanged(object? sender, EventArgs e) => QueueScreenFit();

    private void OnScreenPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_applyingScreenFit || !IsVisible) return;
        var screen = Screens.ScreenFromWindow(this);
        // 同屏拖动不改用户的位置；跨屏时等 DPI/位置通知收敛后再校正。
        if (screen != null && (screen.WorkingArea != _lastWorkingArea || screen.Scaling != _lastScreenScale))
            QueueScreenFit();
    }

    private void QueueScreenFit()
    {
        if (!_screenFitAttached || _exitConfirmed || _applyingScreenFit) return;
        _screenFitTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(200) };
        _screenFitTimer.Tick -= OnScreenFitTimer;
        _screenFitTimer.Tick += OnScreenFitTimer;
        _screenFitTimer.Stop();
        _screenFitTimer.Start();
    }

    private void OnScreenFitTimer(object? sender, EventArgs e)
    {
        _screenFitTimer?.Stop();
        if (IsVisible && !_exitConfirmed) EnsureScreenFit();
    }

    internal void EnsureScreenFit()
    {
        if (_applyingScreenFit) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null) FitToScreen(screen, startup: false);
    }

    private void FitToScreen(Screen screen, bool startup)
    {
        if (screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0) return;
        var placement = CalculateScreenFit(screen.WorkingArea, screen.Scaling,
            new Size(Width, Height), Position, startup);
        _applyingScreenFit = true;
        try
        {
            MinWidth = placement.Minimum.Width;
            MinHeight = placement.Minimum.Height;
            // 不覆盖系统最大化/最小化的边界与还原尺寸。
            if (WindowState == WindowState.Normal)
            {
                Width = placement.Size.Width;
                Height = placement.Size.Height;
                if (!startup) Position = placement.Position;
            }
            _lastWorkingArea = screen.WorkingArea;
            _lastScreenScale = screen.Scaling;
        }
        finally { _applyingScreenFit = false; }
    }

    internal readonly record struct ScreenPlacement(Size Size, Size Minimum, PixelPoint Position);

    internal static ScreenPlacement CalculateScreenFit(PixelRect workArea, double scale,
        Size requested, PixelPoint position, bool startup)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        double workWidth = workArea.Width / scale, workHeight = workArea.Height / scale;
        // 普通窗口四周预留 16 DIP；极小工作区按比例缩减边距。
        double margin = Math.Min(16, Math.Min(workWidth, workHeight) / 20);
        double availableWidth = Math.Max(1, Math.Floor(workWidth - 2 * margin));
        double availableHeight = Math.Max(1, Math.Floor(workHeight - 2 * margin));
        var minimum = new Size(Math.Min(960, availableWidth), Math.Min(540, availableHeight));
        double widthLimit = startup ? Math.Min(availableWidth, Math.Floor(workWidth * .92)) : availableWidth;
        double heightLimit = startup ? Math.Min(availableHeight, Math.Floor(workHeight * .92)) : availableHeight;
        var size = new Size(
            Math.Clamp(double.IsFinite(requested.Width) ? requested.Width : 1600, minimum.Width, Math.Max(minimum.Width, widthLimit)),
            Math.Clamp(double.IsFinite(requested.Height) ? requested.Height : 900, minimum.Height, Math.Max(minimum.Height, heightLimit)));
        int widthPx = (int)Math.Ceiling(size.Width * scale), heightPx = (int)Math.Ceiling(size.Height * scale);
        int left = workArea.X + (int)Math.Floor(margin * scale);
        int top = workArea.Y + (int)Math.Floor(margin * scale);
        int right = Math.Max(left, workArea.Right - (int)Math.Floor(margin * scale) - widthPx);
        int bottom = Math.Max(top, workArea.Bottom - (int)Math.Floor(margin * scale) - heightPx);
        var point = startup
            ? new PixelPoint(workArea.X + (workArea.Width - widthPx) / 2, workArea.Y + (workArea.Height - heightPx) / 2)
            : new PixelPoint(Math.Clamp(position.X, left, right), Math.Clamp(position.Y, top, bottom));
        return new ScreenPlacement(size, minimum, point);
    }

    private void StopScreenFit()
    {
        _screenFitTimer?.Stop();
        if (!_screenFitAttached) return;
        Screens.Changed -= OnScreensChanged;
        ScalingChanged -= OnWindowScalingChanged;
        PositionChanged -= OnScreenPositionChanged;
        _screenFitAttached = false;
    }
}
