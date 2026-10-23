using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Features.Settings;
using PortKiller.ViewModels;

namespace PortKiller.Regression;

internal static class FontLicenseChecks
{
    internal static async Task ViewerAndDocuments()
    {
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_FONT_LICENSE_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        var previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                var settings = new SettingsView { DataContext = new MainViewModel { SettingsTab = "fonts" } };
                var owner = new Window { Content = settings, Width = 1040, Height = 840, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
                FontLicensesWindow? window = null;
                try
                {
                    owner.Show(); owner.UpdateLayout();
                    var open = settings.FindControl<Button>("FontLicensesButton")!;
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window = owner.OwnedWindows.OfType<FontLicensesWindow>().Single();
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Program.Assert(owner.OwnedWindows.OfType<FontLicensesWindow>().Count() == 1, "重复查看字体授权创建了多份窗口。");
                    var list = window.FindControl<ListBox>("FontDocuments")!;
                    var reader = window.FindControl<ScrollViewer>("LicenseReader")!;
                    var text = window.FindControl<SelectableTextBlock>("LicenseText")!;
                    foreach (var size in new[] { new Size(940, 700), new Size(800, 580) })
                    foreach (var document in window.ViewModel.Documents)
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        list.SelectedItem = document;
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                        Program.Assert(ReferenceEquals(window.ViewModel.SelectedDocument, document) && text.Text == document.Text,
                            "字体选择未切换到完整授权原文。");
                        string resource = document.Family == "Inter" ? "Inter" : "NotoSansSC";
                        using var raw = new StreamReader(AssetLoader.Open(new Uri($"avares://PortKiller/Assets/Fonts/{resource}-OFL.txt")));
                        Program.Assert(text.Text == raw.ReadToEnd() && text.Text.Contains("DISCLAIMER"), "授权文档被改写或遗漏末尾条款。");
                        text.SelectionStart = 0; text.SelectionEnd = 80;
                        Program.Assert(text.SelectedText == document.Text[..80], "授权阅读区无法选择原文。");
                        text.SelectionStart = text.SelectionEnd = 0;
                        Program.Assert(reader.Extent.Height > reader.Viewport.Height, "长授权文档没有进入滚动阅读区。");
                        var root = window.FindControl<Border>("FontLicensesSurface")!;
                        var copy = window.FindControl<Button>("CopyFontLicenseButton")!;
                        var point = copy.TranslatePoint(default, root);
                        Program.Assert(copy.IsEffectivelyVisible && point.HasValue && point.Value.Y + copy.Bounds.Height <= root.Bounds.Height,
                            "授权原文挤掉了底部操作按钮。");
                        if (output != null) Save(root, Path.Combine(output, $"{theme}-{resource}-{size.Width:0}.png"));
                        reader.Offset = new Vector(0, reader.Extent.Height);
                        window.UpdateLayout();
                        Program.Assert(reader.Offset.Y > 0, "授权原文无法向下滚动。");
                        list.SelectedItem = window.ViewModel.Documents.First(d => d != document);
                        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                        Program.Assert(reader.Offset.Y == 0, "切换字体后仍停留在上一份授权的滚动位置。");
                    }
                    if (output != null)
                    {
                        var card = settings.FindControl<Border>("FontLicenseInfo")!.GetVisualAncestors().OfType<Border>()
                            .First(b => b.Classes.Contains("card"));
                        Save(card, Path.Combine(output, $"{theme}-settings-entry.png"));
                    }
                    window.Close();
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window = owner.OwnedWindows.OfType<FontLicensesWindow>().Single();
                    Program.Assert(window.IsVisible, "关闭后无法再次查看字体授权。");
                }
                finally { window?.Close(); owner.Close(); }
            }
        }
        finally { Application.Current.RequestedThemeVariant = previousTheme; }
        Console.WriteLine("PASS 字体授权原文、可选择阅读、切换回顶、最小窗口布局与重复打开");
    }

    private static void Save(Control control, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)), new Vector(96, 96));
        image.Render(control); image.Save(path, PngBitmapEncoderOptions.Default);
    }
}
