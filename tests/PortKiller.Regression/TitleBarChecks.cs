using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.ViewModels;
using PortKiller.Views;
using SkiaSharp;

namespace PortKiller.Regression;

internal static class TitleBarChecks
{
    /// <summary>真实窗口的状态切换、最外层像素命中和不同 DPI 下的标题栏栅格化。</summary>
    internal static async Task EdgesAndHitTargets()
    {
        var app = Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        // 尺寸按四个逻辑单位对齐，使栅格图的最外层像素在四档缩放下均完整落入窗口。
        var window = new MainWindow { Width = 1100, Height = 640, ShowActivated = false };
        int samples = 0;
        try
        {
            // Show 后才绑定 VM，跳过正式启动服务，同时让菜单/模态层处于真实的关闭状态。
            window.Show();
            var vm = new MainViewModel();
            window.DataContext = vm;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal })
            {
                app.RequestedThemeVariant = theme;
                window.WindowState = state;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(180);
                window.UpdateLayout();
                Program.Assert(window.WindowState == state, $"标题栏状态切换失败：{state}。");
                var buttons = window.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("wctl") || b.Classes.Contains("wctl-close"))
                    .OrderBy(b => b.TranslatePoint(default, window)!.Value.X).ToArray();
                Program.Assert(buttons.Length == 3, "标题栏三个窗口按钮不完整。");
                foreach (var button in buttons)
                {
                    var origin = button.TranslatePoint(default, window)!.Value;
                    Program.Assert(Math.Abs(origin.Y) < .001,
                        $"{theme}/{state} 按钮顶部没有贴到窗口 y=0：{button.Classes}, y={origin.Y}。");
                    foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
                    foreach (double x in new[] { origin.X + .5 / scale, origin.X + button.Bounds.Width / 2,
                        origin.X + button.Bounds.Width - .5 / scale })
                    foreach (double y in new[] { 0, .5 / scale, 4 / scale })
                    {
                        AssertButtonHit(window, button, new Point(x, y), $"{theme}/{state}/{scale}");
                        samples++;
                    }
                }

                if (state == WindowState.Maximized)
                {
                    foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
                    {
                        AssertButtonHit(window, buttons[2],
                            new Point(window.ClientSize.Width - .5 / scale, .5 / scale), "最大化右上角");
                        AssertButtonHit(window, buttons[2],
                            new Point(window.ClientSize.Width - .5 / scale, buttons[2].Bounds.Height / 2), "最大化右边缘");
                    }
                }
                else
                {
                    // 按钮区以外的顶部、左右、底部及左上角仍能缩放。
                    foreach (var (point, tag) in new[]
                    {
                        (new Point(window.ClientSize.Width / 2, 2), "N"),
                        (new Point(2, window.ClientSize.Height / 2), "W"),
                        (new Point(window.ClientSize.Width - 2, window.ClientSize.Height / 2), "E"),
                        (new Point(window.ClientSize.Width / 2, window.ClientSize.Height - 2), "S"),
                        (new Point(2, 2), "NW")
                    })
                        Program.Assert(window.InputHitTest(point) is Border { Tag: string actual } && actual == tag,
                            $"正常态 {tag} 缩放热区丢失：{point}。");
                }

                var root = (Control)window.Content!;
                var border = ResourceColor(window, "BorderStrong");
                foreach (var button in buttons)
                {
                    var pseudo = (IPseudoClasses)button.Classes;
                    pseudo.Set(":pointerover", true);
                    try
                    {
                        await Task.Delay(140);
                        var hover = button.Classes.Contains("wctl-close") ? Color.Parse("#E81123")
                            : ResourceColor(window, "BgHover");
                        foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
                        {
                            using var image = RenderTitleBar(root, scale);
                            int x = (int)Math.Floor((button.TranslatePoint(default, window)!.Value.X
                                + button.Bounds.Width / 2) * scale);
                            AssertColor(image.GetPixel(x, 0), hover, $"{theme}/{state}/{scale} 悬停顶部有缝");
                            if (state == WindowState.Normal)
                                AssertColor(image.GetPixel(image.Width - 1, (int)(20 * scale)), border,
                                    $"{theme}/{scale} 悬停覆盖了正常态右侧描边");
                            if (state == WindowState.Maximized && button == buttons[2])
                                AssertColor(image.GetPixel(image.Width - 1, 0), hover,
                                    $"{theme}/{scale} 最大化右上角未铺满");
                            samples++;
                        }
                    }
                    finally { pseudo.Set(":pointerover", false); }
                }

                await Task.Delay(140);
                foreach (double scale in new[] { 1, 1.25, 1.5, 2 })
                {
                    using var image = RenderTitleBar(root, scale);
                    if (state == WindowState.Normal)
                    {
                        AssertColor(image.GetPixel(image.Width / 2, 0), border, "正常态顶部描边丢失");
                        foreach (var button in buttons)
                            AssertColor(image.GetPixel((int)((button.TranslatePoint(default, window)!.Value.X
                                + button.Bounds.Width / 2) * scale), 0), border, "按钮移出后顶部描边没有恢复");
                        AssertColor(image.GetPixel(image.Width - 1, (int)(20 * scale)), border, "正常态右侧描边丢失");
                        using var full = RenderTitleBar(root, scale, root.Bounds.Height);
                        AssertColor(full.GetPixel(0, full.Height / 2), border, "正常态左侧描边丢失");
                        AssertColor(full.GetPixel(full.Width / 2, full.Height - 1), border, "正常态底部描边丢失");
                    }
                }
                vm.IsExitConfirmVisible = true;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                // 输入命中使用已提交的合成场景，等待可见性变化完成一帧提交。
                await Task.Delay(80);
                var covered = window.InputHitTest(new Point(window.ClientSize.Width - 20, 0)) as Visual;
                Program.Assert(covered != buttons[2] && covered?.FindAncestorOfType<Button>() != buttons[2],
                    $"模态遮罩未能拦截标题栏按钮：visible={window.FindControl<Panel>("ExitOverlay")!.IsVisible}，"
                    + $"bounds={window.FindControl<Panel>("ExitOverlay")!.Bounds}，hit={covered?.GetType().Name}。");
                vm.IsExitConfirmVisible = false;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                await Task.Delay(80);
                Console.WriteLine($"PASS 标题栏 {theme}/{state} 顶边、角落命中及 100/125/150/200% 栅格化");
            }

            // 控件移动到根层后，原有窗口操作仍必须接通。
            var controls = window.FindControl<StackPanel>("WindowControls")!.Children.OfType<Button>().ToArray();
            controls[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Assert(window.WindowState == WindowState.Minimized, "最小化按钮没有最小化窗口。");
            window.WindowState = WindowState.Normal;
            controls[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Assert(window.WindowState == WindowState.Maximized, "最大化按钮没有最大化窗口。");
            controls[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Assert(window.WindowState == WindowState.Normal, "还原按钮没有还原窗口。");
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            controls[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Assert(closed, "关闭按钮没有关闭无托盘的验收窗口。");
        }
        finally
        {
            window.PrepareClose();
            window.Close();
            app.RequestedThemeVariant = previousTheme;
        }
        Console.WriteLine($"PASS 标题栏共 {samples} 个边缘命中和悬停像素检查，边框/缩放热区保留及三个按钮操作");
    }

    private static void AssertButtonHit(MainWindow window, Button button, Point point, string scenario)
    {
        var hit = window.InputHitTest(point) as Visual;
        Program.Assert(hit == button || hit?.FindAncestorOfType<Button>() == button,
            $"{scenario} 按钮边缘命中被遮挡：{point}, hit={hit?.GetType().Name}/{(hit as Control)?.Name}/{(hit as Control)?.Tag}，"
            + $"button={button.Bounds}, border={button.BorderThickness}, children="
            + string.Join(";", button.GetVisualDescendants().OfType<Control>().Take(3)
                .Select(c => $"{c.GetType().Name}/{c.Name}/{c.Bounds}/{c.TranslatePoint(default, window)}"))
            + ", ancestors=" + string.Join(";", (hit?.GetVisualAncestors() ?? []).OfType<Control>()
                .Select(c => $"{c.GetType().Name}/{c.Name}/{c.ZIndex}/{c.Bounds}"))
            + $", visible={button.IsVisible}, enabled={button.IsEnabled}, hit={button.IsHitTestVisible}");
    }

    private static SKBitmap RenderTitleBar(Control root, double scale, double height = 48)
    {
        using var target = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(root.Bounds.Width * scale), (int)Math.Ceiling(height * scale)),
            new Vector(96 * scale, 96 * scale));
        target.Render(root);
        using var png = new MemoryStream();
        target.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return SKBitmap.Decode(png);
    }

    private static void AssertColor(SKColor actual, Color expected, string message)
        => Program.Assert(Math.Abs(actual.Red - expected.R) <= 2 && Math.Abs(actual.Green - expected.G) <= 2
            && Math.Abs(actual.Blue - expected.B) <= 2 && actual.Alpha == 255,
            $"{message}：实际 {actual}，预期 {expected}。");

    private static Color ResourceColor(MainWindow window, string key)
    {
        Program.Assert(window.TryFindResource(key, window.ActualThemeVariant, out var resource)
            && resource is ISolidColorBrush, $"主题资源 {key} 不存在。");
        return ((ISolidColorBrush)resource!).Color;
    }
}
