using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Updates;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

internal static class ScreenFitChecks
{
    internal static void Placement()
    {
        // 物理工作区已扣除任务栏，包含负坐标副屏、顶部/左侧任务栏和高 DPI。
        var areas = new[] { new PixelRect(0, 0, 1920, 1040), new PixelRect(0, 0, 1366, 728),
            new PixelRect(-1920, 40, 1920, 1040), new PixelRect(48, 0, 1318, 768),
            new PixelRect(0, 0, 3840, 2080), new PixelRect(0, 0, 800, 560) };
        int count = 0;
        foreach (var area in areas)
        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
        foreach (bool startup in new[] { true, false })
        foreach (var point in new[] { new PixelPoint(-20000, -20000), new PixelPoint(20000, 20000) })
        {
            var fit = MainWindow.CalculateScreenFit(area, scale, new Size(1600, 900), point, startup);
            int width = (int)Math.Ceiling(fit.Size.Width * scale), height = (int)Math.Ceiling(fit.Size.Height * scale);
            Program.Assert(fit.Position.X >= area.X && fit.Position.Y >= area.Y
                && fit.Position.X + width <= area.Right && fit.Position.Y + height <= area.Bottom,
                $"工作区 {area}/{scale}/{startup} 窗口仍越界：{fit}。");
            Program.Assert(fit.Size.Width >= fit.Minimum.Width && fit.Size.Height >= fit.Minimum.Height,
                "最小尺寸阻止窗口适应工作区。");
            var repeated = MainWindow.CalculateScreenFit(area, scale, fit.Size, fit.Position, startup: false);
            Program.Assert(repeated == fit, "反复调整窗口导致尺寸或位置漂移。");
            count++;
        }
        var unchanged = MainWindow.CalculateScreenFit(new(0, 0, 2560, 1400), 1,
            new(1000, 600), new(200, 100), startup: false);
        Program.Assert(unchanged.Size == new Size(1000, 600) && unchanged.Position == new PixelPoint(200, 100),
            "工作区足够时覆盖了用户的正常窗口大小与位置。");
        var fallback = MainWindow.CalculateScreenFit(new(0, 0, 1920, 1040), double.NaN,
            new(double.NaN, double.NaN), default, startup: true);
        Program.Assert(fallback.Size == new Size(1600, 900), "无效缩放或尺寸未回退到安全默认值。");
        Console.WriteLine($"PASS 小屏窗口 {count} 组工作区/缩放/跨屏边界、幂等及用户尺寸保留");
    }

    internal static async Task WindowLayout()
    {
        // 不能覆盖 Width/Height：此处专门验证正式默认启动尺寸，避免重演 DPI 验收遗漏。
        var window = new MainWindow { ShowActivated = false };
        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            window.Show();
            await Settle(window);
            var screen = window.Screens.ScreenFromWindow(window)!;
            var area = screen.WorkingArea;
            Program.Assert(window.Position.X >= area.X && window.Position.Y >= area.Y
                && window.Position.X + Math.Ceiling(window.ClientSize.Width * window.RenderScaling) <= area.Right
                && window.Position.Y + Math.Ceiling(window.ClientSize.Height * window.RenderScaling) <= area.Bottom,
                $"默认启动窗口超出工作区：{window.Position}/{window.ClientSize}/{window.RenderScaling}，work={area}。");
            Console.WriteLine($"METRIC 默认启动 size={window.ClientSize}, scale={window.RenderScaling}, work={area}");
            var main = new MainViewModel();
            window.DataContext = main;
            main.UpdateUi.Apply(new(UpdateStage.Available, "测试", new UpdateManifest { Version = "0.1.3" }));
            var toolbar = window.GetVisualDescendants().OfType<Grid>().Single(c => c.Name == "PortToolbar");
            var search = window.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "SearchField");
            var release = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ToolbarReleaseButton");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var size in new[] { new Size(1000, 600), new Size(960, 540), new Size(800, 450), new Size(1180, 700) })
            {
                Application.Current.RequestedThemeVariant = theme;
                // 小于 960 的布局模拟对应高 DPI 工作区允许降低最小尺寸的情况。
                window.MinWidth = Math.Min(960, size.Width);
                window.MinHeight = Math.Min(540, size.Height);
                window.Width = size.Width;
                window.Height = size.Height;
                await Settle(window);
                foreach (var button in window.GetVisualDescendants().OfType<Button>()
                    .Where(c => c.Classes.Contains("wctl") || c.Classes.Contains("wctl-close") || c == release))
                {
                    var origin = button.TranslatePoint(default, window)!.Value;
                    Program.Assert(origin.X >= 0 && origin.Y >= 0
                        && origin.X + button.Bounds.Width <= window.ClientSize.Width
                        && origin.Y + button.Bounds.Height <= window.ClientSize.Height,
                        $"{theme}/{size} 按钮超出窗口：{button.Name}，{origin}/{button.Bounds}。");
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    var hit = window.InputHitTest(point) as Visual;
                    Program.Assert(hit == button || hit?.FindAncestorOfType<Button>() == button,
                        $"{theme}/{size} 按钮被覆盖：{button.Name}，hit={hit?.GetType().Name}。");
                }
                bool compact = toolbar.Bounds.Width < 920;
                Program.Assert(Grid.GetRow(release) == (compact ? 1 : 0)
                    && Grid.GetColumnSpan(search) == (compact ? 6 : 1), "工具栏未在宽窄布局间正确切换。");
                var output = Environment.GetEnvironmentVariable("PORTKILLER_UI_ARTIFACTS");
                if (output != null)
                {
                    Directory.CreateDirectory(output);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
                    bitmap.Render((Control)window.Content!);
                    bitmap.Save(Path.Combine(output, $"ScreenFit-{theme}-{size.Width}x{size.Height}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            // 模拟旧显示器坐标在拔屏后不可见，验证真实窗口校正入口与还原路径。
            window.Position = new PixelPoint(-20000, -20000);
            window.EnsureScreenFit();
            await Settle(window);
            Program.Assert(window.Position.X >= area.X && window.Position.Y >= area.Y
                && window.Position.X + Math.Ceiling(window.ClientSize.Width * window.RenderScaling) <= area.Right
                && window.Position.Y + Math.Ceiling(window.ClientSize.Height * window.RenderScaling) <= area.Bottom,
                "不可见位置恢复后窗口仍在屏幕外。");
            window.WindowState = WindowState.Maximized;
            await Task.Delay(100);
            Program.Assert(window.WindowState == WindowState.Maximized, "工作区适配覆盖了最大化状态。");
            window.WindowState = WindowState.Normal;
            await Task.Delay(300);
            Program.Assert(window.WindowState == WindowState.Normal && window.ClientSize.Width <= area.Width / window.RenderScaling
                && window.ClientSize.Height <= area.Height / window.RenderScaling, "还原后的窗口超过工作区。");
        }
        finally
        {
            window.PrepareClose();
            window.Close();
            Application.Current!.RequestedThemeVariant = previousTheme;
        }
        Console.WriteLine("PASS 正式默认启动边界、小屏深浅主题布局、窗口控制按钮命中与工具栏自适应");
    }

    private static async Task Settle(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Task.Delay(80);
        window.UpdateLayout();
    }
}
