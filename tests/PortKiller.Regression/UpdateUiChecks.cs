using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PortKiller.Features.Updates;
using PortKiller.Updates;

namespace PortKiller.Regression;

internal static class UpdateUiChecks
{
    internal static async Task StatesAndWindowLifetime()
    {
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var manifest = new UpdateManifest
        {
            Version = "0.2", Size = 71722598,
            ReleaseNotes = ["新增独立的软件更新窗口，可在后台下载更新。", "支持暂停与继续下载，显示实时进度与速度。", "优化 HTTP 更新校验和失败重试。", "安装失败时自动恢复上一可用版本。"]
        };
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_UI_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                using var vm = new UpdateViewModel(currentVersion: "0.1");
                var window = new UpdateWindow(vm) { ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var closeButton = window.FindControl<Button>("UpdateCloseButton")!;
                    byte[] firstCaption = RenderCaption(closeButton);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
                    window.UpdateLayout();
                    for (int frame = 0; frame < 4; frame++)
                    {
                        await Task.Delay(40);
                        Program.Assert(firstCaption.AsSpan().SequenceEqual(RenderCaption(closeButton)),
                            $"{theme} 更新窗口首次显示后关闭按钮的颜色或图标发生跳变。");
                    }
                    window.Hide(); window.Show(); window.UpdateLayout();
                    Program.Assert(firstCaption.AsSpan().SequenceEqual(RenderCaption(closeButton)),
                        $"{theme} 更新窗口重新打开后关闭按钮发生变化。");
                    var titleBar = window.FindControl<Border>("UpdateTitleBar")!;
                    // 输入命中使用合成器已提交的场景，需要等待首帧，不能只完成 Measure/Arrange。
                    using (var frameDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        while (window.InputHitTest(new Point(280, 20)) == null)
                            await Task.Delay(16, frameDeadline.Token);
                    foreach (var point in new[] { new Point(40, 20), new Point(280, 20), new Point(450, 20) })
                    {
                        var hit = window.InputHitTest(point) as Visual;
                        Program.Assert(hit == titleBar || hit?.GetVisualAncestors().Contains(titleBar) == true,
                            $"标题栏文字或空白区域不能命中拖动入口：{point}, hit={hit?.GetType().Name}, bar={titleBar.Bounds}, window={window.Bounds}。");
                    }
                    foreach (var point in new[] { new Point(500, 20), new Point(536, 20) })
                    {
                        var hit = window.InputHitTest(point) as Visual;
                        Program.Assert(hit is Button || hit?.FindAncestorOfType<Button>() != null,
                            $"窗口按钮的点击区域被标题栏拖动覆盖：{point}。");
                    }
                    foreach (var stage in Enum.GetValues<UpdateStage>())
                    {
                        vm.Apply(new(stage, stage == UpdateStage.Disabled ? "自动更新未配置" : "正在检查应用更新…", manifest,
                            (long)(manifest.Size * .62), 2516582, Failure: UpdateFailure.Network,
                            ErrorDetail: stage == UpdateStage.Failed ? "HttpRequestException: 网络连接已断开，请稍后重试。" : ""));
                        window.Measure(new Size(560, 400)); window.Arrange(new Rect(0, 0, 560, 400));
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
                        var root = window.FindControl<Border>("UpdateSurfaceRoot")!;
                        Program.Assert(root.Bounds.Size == new Size(560, 400) && root.CornerRadius == new CornerRadius(10),
                            $"{theme}/{stage} 窗口尺寸或圆角不符：root={root.Bounds}, window={window.Bounds}, radius={root.CornerRadius}。");
                        Program.Assert(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == vm.Title),
                            $"{theme}/{stage} 缺少状态标题。");
                        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0))
                        {
                            var point = text.TranslatePoint(default, root);
                            Program.Assert(point != null && point.Value.X >= 0 && point.Value.Y >= 0 && point.Value.X + text.Bounds.Width <= 560.5,
                                $"{theme}/{stage} 文字超出窗口：{text.Text}");
                        }
                        if (output != null)
                        {
                            foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
                            {
                                using var image = new RenderTargetBitmap(new PixelSize((int)(560 * scale), (int)(400 * scale)), new Vector(96 * scale, 96 * scale));
                                image.Render(root);
                                image.Save(Path.Combine(output, $"{theme}-{stage}-{scale * 100:0}.png"), PngBitmapEncoderOptions.Default);
                            }
                        }
                    }
                    // 长说明、旧清单空说明和长错误均不能改变底部按钮的可见性。
                    manifest.ReleaseNotes = Enumerable.Range(0, 20).Select(i => $"{i + 1}. " + new string('测', 280)).ToArray();
                    vm.Apply(new(UpdateStage.Available, "发现更新", manifest));
                    window.Measure(new Size(560, 400)); window.Arrange(new Rect(0, 0, 560, 400));
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
                    var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
                    Program.Assert(scroller.Extent.Height > scroller.Viewport.Height, $"长更新说明未使用滚动区域：{scroller.Extent}/{scroller.Viewport}。");
                    Program.Assert(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "立即更新")),
                        "长说明挤掉了更新按钮。");
                    manifest.ReleaseNotes = [];
                    vm.Apply(new(UpdateStage.Available, "发现更新", manifest));
                    Program.Assert(vm.ReleaseNotes.Single().Contains("未提供"), "旧清单缺少更新说明时没有兜底。");
                }
                finally { window.CloseForShutdown(); }
            }

            int starts = 0, cancellations = 0, saves = 0;
            using var interaction = new UpdateViewModel(beginInstall: () => true,
                install: () => { starts++; return Task.FromResult<string?>("模拟保存失败，当前程序继续运行。"); },
                cancelInstall: () => cancellations++, saveAutomaticCheck: _ => { saves++; return false; });
            interaction.Apply(new(UpdateStage.Ready, "已就绪", manifest, manifest.Size));
            Program.Assert(starts == 0 && !interaction.IsConfirming, "更新就绪后未经确认即重启。");
            await interaction.PrimaryCommand.ExecuteAsync(null);
            Program.Assert(interaction.IsConfirming && starts == 0, "立即重启未经过用户确认。");
            await interaction.ConfirmRestartCommand.ExecuteAsync(null);
            Program.Assert(starts == 1 && interaction.HasNotice && !interaction.IsInstalling, "安装准备失败后未恢复可操作状态。");
            interaction.CancelConfirmationCommand.Execute(null);
            Program.Assert(!interaction.IsConfirming && cancellations == 1, "取消重启未释放操作保护。");
            interaction.AutomaticCheck = false;
            Program.Assert(interaction.AutomaticCheck && saves == 1, "偏好保存失败时界面未回退。");

            using var hiddenVm = new UpdateViewModel();
            var owner = new Window { Width = 640, Height = 480, ShowActivated = false };
            var child = new UpdateWindow(hiddenVm) { ShowActivated = false };
            try
            {
                owner.Show(); child.ShowFor(owner); child.Close();
                Program.Assert(!child.IsVisible, "关闭更新窗口没有隐藏。");
                hiddenVm.Apply(new(UpdateStage.Downloading, "继续后台下载", manifest, 10));
                child.ShowFor(owner);
                Program.Assert(child.IsVisible && hiddenVm.IsTransfer, "窗口关闭后状态被清空或无法再次打开。");
            }
            finally { child.CloseForShutdown(); owner.Close(); }
        }
        finally { Application.Current.RequestedThemeVariant = previousTheme; }
        Console.WriteLine("PASS 更新窗口全部状态、深浅主题、关闭按钮首帧稳定、长内容、重启确认与窗口生命周期");
    }

    private static byte[] RenderCaption(Button caption)
    {
        using var image = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        image.Render(caption);
        using var stream = new MemoryStream();
        image.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }
}
