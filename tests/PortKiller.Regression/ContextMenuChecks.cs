using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Views;
using SkiaSharp;

namespace PortKiller.Regression;

internal static class ContextMenuChecks
{
    internal static async Task HoverTransitions()
    {
        var app = Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        // 无 VM 的离屏宿主不启动扫描；真实弹出菜单但不触发任何复制或终止动作。
        var window = new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000) };
        var menu = (ContextMenu)window.FindResource("RowCtxMenu")!;
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None;
        // 捕获动画期间阻止外部桌面焦点变化提前收起菜单，结束后正常关闭。
        bool capturing = false;
        menu.Closing += (_, e) => e.Cancel = capturing;
        try
        {
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                app.RequestedThemeVariant = theme;
                capturing = true;
                menu.Open(window);
                await Dispatcher.UIThread.InvokeAsync(menu.UpdateLayout, DispatcherPriority.Background);
                // 先让跨主题资源的 120ms 过渡结束，再采样该主题自身的悬停动画。
                await Task.Delay(160);
                Program.Assert(menu.IsOpen, "端口右键菜单没有实际打开。");
                var rest = ((ISolidColorBrush)menu.Background!).Color;
                var hover = theme == ThemeVariant.Dark ? Color.Parse("#344156") : Color.Parse("#E5EDF8");
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    var surface = item.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "root");
                    var state = (IPseudoClasses)item.Classes;
                    Program.Assert(surface.Transitions?.Count > 0, "菜单回归未启用真实背景过渡。");
                    foreach (var (hovering, frames, settle) in new[]
                    {
                        (true, 10, true), (false, 10, true),
                        (true, 2, false), (false, 2, false), (true, 2, false), (false, 10, true)
                    })
                    {
                        state.Set(":pointerover", hovering);
                        for (int frame = 0; frame < frames; frame++)
                        {
                            await Task.Delay(16);
                            using var target = new RenderTargetBitmap(new PixelSize((int)surface.Bounds.Width,
                                (int)surface.Bounds.Height), new Vector(96, 96));
                            target.Render(surface);
                            using var png = new MemoryStream(); target.Save(png, PngBitmapEncoderOptions.Default); png.Position = 0;
                            using var image = SKBitmap.Decode(png);
                            var pixel = image.GetPixel(4, image.Height / 2);
                            double alpha = pixel.Alpha / 255.0;
                            var composed = Color.FromRgb((byte)Math.Round(pixel.Red * alpha + rest.R * (1 - alpha)),
                                (byte)Math.Round(pixel.Green * alpha + rest.G * (1 - alpha)),
                                (byte)Math.Round(pixel.Blue * alpha + rest.B * (1 - alpha)));
                            static bool Between(byte value, byte a, byte b) => value >= Math.Min(a, b) - 3 && value <= Math.Max(a, b) + 3;
                            Program.Assert(Between(composed.R, rest.R, hover.R) && Between(composed.G, rest.G, hover.G)
                                && Between(composed.B, rest.B, hover.B),
                                $"{theme} 右键菜单过渡泛白：{composed}，背景 {surface.Background}。");
                        }
                        if (settle)
                        {
                            Program.Assert(surface.Background is ISolidColorBrush,
                                $"{theme} 菜单背景资源丢失：hover={hovering}, open={menu.IsOpen}, attached={TopLevel.GetTopLevel(surface) != null}。");
                            var final = ((ISolidColorBrush)surface.Background!).Color;
                            Program.Assert(hovering ? final == hover : final.A == 0,
                                $"{theme} 右键菜单未达到悬停/透明状态：{final}。");
                        }
                    }
                }
                capturing = false;
                menu.Close();
            }
        }
        finally
        {
            capturing = false;
            menu.Close(); window.PrepareClose(); window.Close();
            app.RequestedThemeVariant = previousTheme;
        }
        Console.WriteLine("PASS 端口右键菜单：五项操作、深浅主题、移入移出与快速反向悬停均无白色中间帧");
    }
}
