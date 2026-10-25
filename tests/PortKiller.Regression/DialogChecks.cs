using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Models;
using PortKiller.Services;
using PortKiller.ViewModels;
using PortKiller.Views;
using SkiaSharp;

namespace PortKiller.Regression;

internal static class DialogChecks
{
    internal static async Task RoundedCardsAndInteraction()
    {
        var app = Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        var previousTokens = TypographyService.Compute(12, 12, true).Keys.ToDictionary(k => k, k => app.Resources[k]);
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_DIALOG_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        // 先显示空宿主，避免启动扫描、托盘或连接真实终止服务。
        var window = new MainWindow { Width = 1600, Height = 900, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
        var vm = new MainViewModel { InterceptHasForceable = true };
        try
        {
            // 固定帧验证不采样 120ms 动画的中间色；过渡开关另外检查。
            window.Classes.Add("nomotion");
            window.Show(); window.DataContext = vm;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                SetTypography(12, 12);
                foreach (string name in new[] { "Exit", "Family", "Intercept" })
                {
                    Populate(vm, false); Show(vm, name); await Layout(window);
                    var card = window.FindControl<ContentControl>(name + "Card")!;
                    CheckLayout(window, name, false);
                    Program.Assert(card.Bounds.Height < 650, $"{theme}/{name} 短内容弹窗被无故拉高。");
                    CheckTextContrast(card);
                    foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        using var image = Render(card, scale);
                        if (output != null) SaveImage(image, Path.Combine(output, $"{theme}-{name}-{scale:0.00}.png"));
                        CheckRoundedOutline(card, image, scale, $"{theme}/{name}/{scale}");
                    }
                    if (output != null) SaveContext(card, Path.Combine(output, $"{theme}-{name}-context.png"));
                    await CheckKeyboard(window, vm, name);
                    // 最小主窗口 + 最大设置字号 + 长目标列表。
                    window.Width = 1180; window.Height = 700; SetTypography(16, 14);
                    Populate(vm, true); Show(vm, name); await Layout(window);
                    CheckLayout(window, name, true);
                    var body = window.FindControl<ScrollViewer>(name + "Body")!;
                    body.Offset = new Vector(0, body.Extent.Height); await Layout(window);
                    Program.Assert(body.Offset.Y > 0, $"{name} 长正文不能滚动到底部。");
                    if (output != null)
                    {
                        body.Offset = default; await Layout(window);
                        SaveContext(card, Path.Combine(output, $"{theme}-{name}-long-max-font.png"));
                    }
                    Show(vm, null); window.Width = 1600; window.Height = 900; SetTypography(12, 12);
                }
            }
            await CheckRiskGuards(window, vm);
            Show(vm, "Exit");
            app.RequestedThemeVariant = ThemeVariant.Light; await Layout(window);
            var exit = window.FindControl<ContentControl>("ExitCard")!;
            var light = ((ISolidColorBrush)exit.Background!).Color;
            app.RequestedThemeVariant = ThemeVariant.Dark; await Layout(window);
            Program.Assert(light != ((ISolidColorBrush)exit.Background!).Color, "已打开弹窗未跟随主题切换。");
            await Layout(window);
            Program.Assert(exit.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "DialogButtonSurface")
                .All(b => b.Transitions is null || b.Transitions.Count == 0), "减少动画设置没有关闭按钮过渡。");
            await CheckCloseHover(window, vm);
        }
        finally
        {
            window.PrepareClose(); window.Close();
            foreach (var (key, value) in previousTokens) app.Resources[key] = value;
            app.RequestedThemeVariant = previousTheme;
        }
        Console.WriteLine("PASS 三类确认弹窗：24 组圆角主题渲染、最小窗口/大字号滚动、等宽按钮、键盘焦点和风险保护");
    }

    private static void SetTypography(double ui, double mono)
    {
        foreach (var (key, value) in TypographyService.Compute(ui, mono, true)) Application.Current!.Resources[key] = value;
    }

    private static async Task Layout(MainWindow window) => await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);

    private static void Show(MainViewModel vm, string? name)
    {
        vm.IsExitConfirmVisible = name == "Exit"; vm.IsFamilyConfirmVisible = name == "Family"; vm.IsInterceptVisible = name == "Intercept";
    }

    private static void Populate(MainViewModel vm, bool longContent)
    {
        string details = longContent ? string.Join("\n", Enumerable.Repeat("目标说明：关联服务仍可能重新启动，请核对目标及影响后再继续。", 20))
            : "PID 4180 (Example.Service.exe) 属系统特权服务。强制终止可能影响依赖服务，请先确认目标与风险。";
        vm.InterceptDesc = details;
        vm.FamilyConfirmDesc = longContent ? details : "已检测到 3 个关联实例，终止主进程会同时结束其子进程。";
        vm.ExitConfirmText = longContent ? details : "PortKiller 仍在后台监听端口变化。退出后将停止监控，确定要结束程序吗？";
        vm.InterceptChips.Clear(); vm.FamilyConfirmChips.Clear();
        for (int i = 0; i < (longContent ? 30 : 3); i++)
        {
            var chip = new InterceptChip($"PID {4180 + i} · {(longContent ? new string('W', 100) : "Example.Service.exe")}", i == 0);
            vm.InterceptChips.Add(chip); vm.FamilyConfirmChips.Add(chip);
        }
        vm.InterceptRiskChecked = false;
    }

    private static void CheckLayout(MainWindow window, string name, bool scrollExpected)
    {
        var card = window.FindControl<ContentControl>(name + "Card")!;
        var body = window.FindControl<ScrollViewer>(name + "Body")!;
        var footer = window.FindControl<Border>(name + "Footer")!;
        var cancel = window.FindControl<Button>(name + "CancelButton")!;
        var confirm = window.FindControl<Button>(name + "ConfirmButton")!;
        var top = card.TranslatePoint(default, window)!.Value;
        Program.Assert(top.Y >= 23 && top.Y + card.Bounds.Height <= window.ClientSize.Height - 23,
            $"{name} 弹窗超出窗口：top={top}, card={card.Bounds}, client={window.ClientSize}。");
        var bodyTop = body.TranslatePoint(default, card)!.Value;
        var footerTop = footer.TranslatePoint(default, card)!.Value;
        Program.Assert(body.Bounds.Height > 0 && bodyTop.Y + body.Bounds.Height <= footerTop.Y + 1
            && footerTop.Y + footer.Bounds.Height <= card.Bounds.Height, $"{name} 正文覆盖或挤掉底部操作区。");
        var cancelTop = cancel.TranslatePoint(default, card)!.Value;
        var confirmTop = confirm.TranslatePoint(default, card)!.Value;
        Program.Assert(Math.Abs(cancel.Bounds.Width - confirm.Bounds.Width) < 1 && cancel.Bounds.Height >= 42
            && confirmTop.X - (cancelTop.X + cancel.Bounds.Width) >= 12
            && confirmTop.X + confirm.Bounds.Width <= card.Bounds.Width - 24, $"{name} 按钮不等宽、过小或相互挤压。");
        Program.Assert(body.Extent.Width <= body.Viewport.Width + 1, $"{name} 长目标文本横向溢出。");
        if (scrollExpected) Program.Assert(body.Extent.Height > body.Viewport.Height + 50, $"{name} 长内容没有进入有界滚动区。");
        foreach (var button in new[] { cancel, confirm })
        {
            var text = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Program.Assert(text.Bounds.Width >= text.DesiredSize.Width - 1 && text.Bounds.Height >= text.DesiredSize.Height - 1,
                $"{name} 大字号按钮文案被裁剪。");
        }
    }

    private static async Task CheckKeyboard(MainWindow window, MainViewModel vm, string name)
    {
        var card = window.FindControl<ContentControl>(name + "Card")!;
        var cancel = window.FindControl<Button>(name + "CancelButton")!;
        Program.Assert(ReferenceEquals(window.FocusManager?.GetFocusedElement(), cancel), $"{name} 打开后未默认聚焦取消。");
        var underlying = window.GetVisualDescendants().OfType<TextBox>().First(c => c.IsEffectivelyVisible);
        underlying.Focus();
        Program.Assert(ReferenceEquals(window.FocusManager?.GetFocusedElement(), cancel), $"{name} 焦点逃到底层。");
        foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift })
        for (int i = 0; i < 12; i++)
        {
            Key(window, Avalonia.Input.Key.Tab, modifiers);
            var focused = window.FocusManager?.GetFocusedElement() as Control;
            Program.Assert(focused != null && focused.GetVisualAncestors().Contains(card),
                $"{name} Tab 焦点离开弹窗。");
            if (focused is Button button && button.Classes.Contains("dialogAction"))
                Program.Assert(button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DialogButtonFocus").IsVisible,
                    $"{name} 键盘聚焦按钮缺少可见焦点环。");
        }
        cancel.Focus(); vm.SearchText = "不能清空";
        Key(window, Avalonia.Input.Key.F, KeyModifiers.Control);
        Program.Assert(ReferenceEquals(window.FocusManager?.GetFocusedElement(), cancel), "Ctrl+F 聚焦了底层搜索框。");
        Key(window, Avalonia.Input.Key.Escape); await Layout(window);
        Program.Assert(!card.IsEffectivelyVisible && vm.SearchText == "不能清空", $"{name} Esc 未取消或影响底层状态。");
        vm.SearchText = ""; underlying.Focus(); Show(vm, name); await Layout(window);
        Key(window, Avalonia.Input.Key.Enter); await Layout(window);
        Program.Assert(!card.IsEffectivelyVisible, $"{name} Enter 未激活当前取消按钮。");
        Program.Assert(ReferenceEquals(window.FocusManager?.GetFocusedElement(), underlying), $"{name} 关闭后未恢复原焦点。");
        Show(vm, name); await Layout(window);
        card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("dialogClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Layout(window);
        Program.Assert(!card.IsEffectivelyVisible, $"{name} 右上关闭未走取消路径。");
    }

    private static async Task CheckRiskGuards(MainWindow window, MainViewModel vm)
    {
        vm.InterceptHasForceable = true; vm.InterceptRiskChecked = false; Show(vm, "Intercept"); await Layout(window);
        var check = window.FindControl<CheckBox>("InterceptRiskCheckBox")!;
        var confirm = window.FindControl<Button>("InterceptConfirmButton")!;
        Program.Assert(!confirm.IsEnabled, "未确认风险时仍可终止。");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await vm.ConfirmInterceptAsync();
        Program.Assert(vm.IsInterceptVisible, "未勾选风险绕过业务校验。");
        check.Focus(); Key(window, Avalonia.Input.Key.Space); await Layout(window);
        Program.Assert(vm.InterceptRiskChecked && confirm.IsEnabled, "Space 未切换风险复选框。");
        vm.InterceptHasForceable = false; await Layout(window);
        Program.Assert(!confirm.IsEffectivelyVisible && !check.IsEffectivelyVisible, "绝对保护目标仍显示强制入口。");
        await vm.ConfirmInterceptAsync();
        Program.Assert(vm.IsInterceptVisible, "绝对保护目标绕过业务校验。");
        Show(vm, null); await Layout(window);
    }

    private static async Task CheckCloseHover(MainWindow window, MainViewModel vm)
    {
        window.Classes.Remove("nomotion");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            foreach (string name in new[] { "Exit", "Family", "Intercept" })
            {
                Show(vm, name); await Layout(window);
                var card = window.FindControl<ContentControl>(name + "Card")!;
                var close = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("dialogClose"));
                var surface = close.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DialogButtonSurface");
                var state = (IPseudoClasses)close.Classes;
                var header = close.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("dialogHeader"));
                var rest = ((ISolidColorBrush)header.Background!).Color;
                var hover = theme == ThemeVariant.Dark ? Color.Parse("#40506A") : Color.Parse("#DCE7F5");
                await Task.Delay(140);
                // 保留实际过渡；逐帧检查普通移入/移出及尚未完成就反向的快速悬停。
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
                        CheckFrame();
                    }
                    if (settle)
                    {
                        var final = ((ISolidColorBrush)surface.Background!).Color;
                        Program.Assert(hovering ? final == hover : final.A == 0,
                            $"{theme}/{name} 关闭按钮没有达到预期悬停/透明状态：{final}。");
                    }
                }

                void CheckFrame()
                {
                    // 直接渲染实际按钮背景层，并合成到标题栏，避免漏掉动画中的白色中间帧。
                    using var image = Render(surface, 1);
                    var pixel = image.GetPixel(5, image.Height / 2);
                    double alpha = pixel.Alpha / 255.0;
                    var composed = Color.FromRgb((byte)Math.Round(pixel.Red * alpha + rest.R * (1 - alpha)),
                        (byte)Math.Round(pixel.Green * alpha + rest.G * (1 - alpha)),
                        (byte)Math.Round(pixel.Blue * alpha + rest.B * (1 - alpha)));
                    // 透明像素经预乘/还原有 8 位量化误差，每通道留 3 阶容差。
                    static bool Between(byte value, byte a, byte b) => value >= Math.Min(a, b) - 3 && value <= Math.Max(a, b) + 3;
                    Program.Assert(Between(composed.R, rest.R, hover.R) && Between(composed.G, rest.G, hover.G)
                        && Between(composed.B, rest.B, hover.B),
                        $"{theme}/{name} 关闭按钮过渡出现异常亮帧：{composed}（背景 {surface.Background}）。");
                }
            }
        }
        Console.WriteLine("PASS 关闭按钮深浅主题真实动画：移入、移出与快速反向均无白色中间帧");
    }

    private static void Key(MainWindow window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var target = (Control?)window.FocusManager?.GetFocusedElement() ?? window;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
    }

    private static void CheckRoundedOutline(ContentControl card, SKBitmap image, double scale, string label)
    {
        var surfaces = card.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "DialogSurface").ToArray();
        Program.Assert(surfaces.Length == 1, $"{label} 弹窗卡片必须只有一个圆角表面，实际为 {surfaces.Length} 个。");
        Program.Assert(!card.GetVisualDescendants().OfType<Border>().Any(b => b.Name is "DialogClip" or "DialogOutlineLayer"),
            $"{label} 仍存在旧的嵌套圆角层。");
        // 用不含标题栏/底栏的独立表面作为描边基准，不能把待测内容也画进基准图。
        // 否则被矩形背景盖掉的圆弧会同时出现在实际图和基准图中，导致漏检。
        var surface = surfaces[0];
        var reference = new Border
        {
            Background = surface.Background, BorderBrush = surface.BorderBrush,
            // 独立控件不属于窗口，布局缩放为 1；直接使用真实窗口已舍入的内缩量，
            // 避免把 150% 布局尺寸再次按 100% 舍入后产生错误基准。
            BorderThickness = new Thickness(surface.Child!.Bounds.X, surface.Child.Bounds.Y,
                surface.Bounds.Width - surface.Child.Bounds.Right, surface.Bounds.Height - surface.Child.Bounds.Bottom),
            CornerRadius = surface.CornerRadius, UseLayoutRounding = false
        };
        reference.Measure(surface.Bounds.Size);
        reference.Arrange(new Rect(surface.Bounds.Size));
        using var mask = Render(reference, scale);
        var color = ((ISolidColorBrush)surface.BorderBrush!).Color;
        var outline = new SKColor(color.R, color.G, color.B, color.A);
        foreach (bool right in new[] { false, true })
        foreach (bool bottom in new[] { false, true })
        {
            Program.Assert(image.GetPixel(right ? image.Width - 1 : 0, bottom ? image.Height - 1 : 0).Alpha < 150, $"{label} 外角被矩形填满。");
            int solid = 0;
            for (int y = 0; y < Math.Ceiling(12 * scale); y++)
            for (int x = 0; x < Math.Ceiling(12 * scale); x++)
            {
                int px = right ? image.Width - 1 - x : x, py = bottom ? image.Height - 1 - y : y;
                var expected = mask.GetPixel(px, py);
                // 仅比较实色线芯，抗锯齿像素会与下方表面混色。
                if (expected.Alpha < 254 || Distance(expected, outline) > 3) continue;
                var actual = image.GetPixel(px, py);
                Program.Assert(actual.Alpha >= 245 && Distance(actual, expected) < 10,
                    $"{label} 内容遮住圆角描边：({px},{py}) actual={actual}, outline={expected}, card={card.Bounds}。");
                solid++;
            }
            Program.Assert(solid > 4 * scale, $"{label} 圆弧描边没有清晰线芯。");
        }
        var edge = image.GetPixel(0, image.Height / 2);
        var inside = image.GetPixel((int)(4 * scale), image.Height / 2);
        Program.Assert(edge.Alpha > 240 && Distance(edge, inside) > 30, $"{label} 外框与表面缺少区分。");
    }

    private static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    private static void CheckTextContrast(ContentControl card)
    {
        foreach (var text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible
            && !t.GetVisualAncestors().OfType<Button>().Any(b => !b.IsEnabled)))
        {
            if (text.Foreground is not ISolidColorBrush foreground) continue;
            var background = text.GetVisualAncestors().OfType<Border>().Select(b => b.Background).OfType<ISolidColorBrush>()
                .FirstOrDefault(b => b.Color.A == 255) ?? (ISolidColorBrush)card.Background!;
            double a = Luminance(foreground.Color), b = Luminance(background.Color);
            Program.Assert((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5,
                $"弹窗文字对比不足：{text.Text} / {foreground.Color} / {background.Color}。");
        }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte v) => v <= 10 ? v / 3294.6 : Math.Pow((v / 255.0 + .055) / 1.055, 2.4);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static SKBitmap Render(Control visual, double scale)
    {
        using var target = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(visual.Bounds.Width * scale),
            (int)Math.Ceiling(visual.Bounds.Height * scale)), new Vector(96, 96));
        // 使用显式变换，避免当前 Skia 离屏后端在阴影恢复画布后重复应用隐藏 DPI 矩阵。
        using (var context = target.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawRectangle(new VisualBrush(visual), null, new Rect(visual.Bounds.Size));
        using var png = new MemoryStream(); target.Save(png, PngBitmapEncoderOptions.Default); png.Position = 0;
        return SKBitmap.Decode(png) ?? throw new InvalidOperationException("无法读取弹窗离屏渲染结果。");
    }

    private static void SaveContext(ContentControl card, string path)
    {
        // 保留周围遮罩和阴影，检视与主界面的实际分层。
        var root = (Control)card.GetVisualAncestors().Last();
        using var full = Render(root, 1);
        var point = card.TranslatePoint(default, root)!.Value;
        var bounds = SKRectI.Create((int)point.X - 40, (int)point.Y - 40, (int)card.Bounds.Width + 80, (int)card.Bounds.Height + 80);
        bounds.Intersect(SKRectI.Create(full.Width, full.Height));
        using var crop = new SKBitmap();
        Program.Assert(full.ExtractSubset(crop, bounds), "弹窗预览裁剪失败。");
        SaveImage(crop, path);
    }

    private static void SaveImage(SKBitmap image, string path)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
