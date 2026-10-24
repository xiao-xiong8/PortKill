using Avalonia;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        Program.Assert(PortKiller.Views.MainWindow.IsUpdateResumeMessage(0x0218, 18)
            && PortKiller.Views.MainWindow.IsUpdateResumeMessage(0x0218, 7)
            && !PortKiller.Views.MainWindow.IsUpdateResumeMessage(0x0218, 4)
            && !PortKiller.Views.MainWindow.IsUpdateResumeMessage(0x0010, 18), "休眠恢复消息分类不正确。");
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var manifest = new UpdateManifest
        {
            Version = "0.2", Size = 71722598,
            ReleaseNotes = ["新增：独立的软件更新窗口，可在后台下载更新。", "优化：暂停与继续下载，显示实时进度与速度。", "修复：网络波动后的更新重试。", "安装失败时自动恢复上一可用版本。"]
        };
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_UI_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                manifest.ReleaseNotes = ["新增：独立的软件更新窗口，可在后台下载更新。", "优化：暂停与继续下载，显示实时进度与速度。", "修复：网络波动后的更新重试。", "安装失败时自动恢复上一可用版本。"];
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
                    var samples = new List<double>();
                    vm.Apply(new(UpdateStage.Downloading, "正在下载更新…", manifest, 0, 1000000));
                    window.UpdateLayout();
                    for (int sample = 0; sample < 1000; sample++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        vm.Apply(new(UpdateStage.Downloading, "正在下载更新…", manifest, manifest.Size * (sample % 100) / 100, 1000000 + sample));
                        window.UpdateLayout();
                        samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        if (sample % 50 == 0) await Task.Delay(1);
                    }
                    samples.Sort();
                    Program.Assert(samples[949] <= 16 && samples[^1] < 100, $"{theme} 更新 UI 执行超时：P95={samples[949]:F2}ms max={samples[^1]:F2}ms。");
                    Console.WriteLine($"METRIC update UI {theme} n=1000 Apply+Layout P95={samples[949]:F2}ms max={samples[^1]:F2}ms");
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
                        Program.Assert(root.Bounds.Size == new Size(560, 400) && root.CornerRadius == new CornerRadius(18),
                            $"{theme}/{stage} 窗口尺寸或圆角不符：root={root.Bounds}, window={window.Bounds}, radius={root.CornerRadius}。");
                        Program.Assert(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == vm.Title),
                            $"{theme}/{stage} 缺少状态标题。");
                        Program.Assert(root.Background is ISolidColorBrush surface && surface.Color == Color.Parse(theme == ThemeVariant.Light ? "#FFFFFF" : "#141D2E"),
                            $"{theme}/{stage} 窗口未使用 HTML 的主题底色。");
                        var footer = window.FindControl<Border>("UpdateFooter")!;
                        foreach (var button in footer.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
                        {
                            var point = button.TranslatePoint(default, root)!.Value;
                            Program.Assert(button.Bounds.Height > 0 && point.X >= 0 && point.Y >= footer.Bounds.Y
                                && point.X + button.Bounds.Width <= 560 && point.Y + button.Bounds.Height <= 400,
                                $"{theme}/{stage} 底部按钮被裁切：{button.Name ?? button.Content?.ToString()}。");
                        }
                        if (!vm.IsTransfer)
                        {
                            var primary = window.FindControl<Button>("UpdatePrimaryButton")!;
                            Program.Assert(primary.Background is ISolidColorBrush accent && accent.Color == Color.Parse(theme == ThemeVariant.Light ? "#345FF1" : "#537BFF")
                                && primary.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == vm.PrimaryText && t.Foreground is ISolidColorBrush foreground && foreground.Color == Colors.White),
                                $"{theme}/{stage} 主按钮的主题蓝色或浅色文字被其他模板覆盖。");
                        }
                        if (vm.IsLatest)
                        {
                            var toggle = window.FindControl<ToggleButton>("UpdateAutomaticSwitch")!;
                            toggle.IsChecked = false;
                            Program.Assert(!vm.AutomaticCheck && vm.AutomaticCheckDescription.Contains("已关闭"), "最新页面开关未回写自动检查偏好。");
                            toggle.IsChecked = true;
                        }
                        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0))
                        {
                            var point = text.TranslatePoint(default, root);
                            Program.Assert(point != null && point.Value.X >= 0 && point.Value.Y >= 0 && point.Value.X + text.Bounds.Width <= 560.5,
                                $"{theme}/{stage} 文字超出窗口：{text.Text}");
                        }
                        if (output != null)
                        {
                            // 仅导出当前已核对的 100% 绘制；高倍率离屏接口有裁切，不能作为实机 DPI 验证。
                            using var image = new RenderTargetBitmap(new PixelSize(560, 400), new Vector(96, 96));
                            image.Render(root);
                            image.Save(Path.Combine(output, $"{theme}-{stage}-100.png"), PngBitmapEncoderOptions.Default);
                        }
                    }
                    // 长说明、旧清单空说明和长错误均不能改变底部按钮的可见性。
                    manifest.ReleaseNotes = Enumerable.Range(0, 20).Select(i => $"{i + 1}. " + new string('测', 280)).ToArray();
                    vm.Apply(new(UpdateStage.Available, "发现更新", manifest));
                    window.Measure(new Size(560, 400)); window.Arrange(new Rect(0, 0, 560, 400));
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
                    var scroller = window.FindControl<ScrollViewer>("UpdateNotesScroller")!;
                    Program.Assert(scroller.Extent.Height > scroller.Viewport.Height, $"长更新说明未使用滚动区域：{scroller.Extent}/{scroller.Viewport}。");
                    Program.Assert(window.FindControl<Button>("UpdatePrimaryButton")!.IsEffectivelyVisible && vm.PrimaryText == "立即更新",
                        "长说明挤掉了更新按钮。");
                    manifest.ReleaseNotes = [];
                    vm.Apply(new(UpdateStage.Available, "发现更新", manifest));
                    Program.Assert(vm.ReleaseNotes.Single().Contains("未提供"), "旧清单缺少更新说明时没有兜底。");
                }
                finally { window.CloseForShutdown(); }
            }

            CheckPresentationAndProgress();

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

    private static void CheckPresentationAndProgress()
    {
        using var vm = new UpdateViewModel(currentVersion: "0.1");
        var manifest = new UpdateManifest { Version = "0.2", Size = 1000,
            ReleaseNotes = ["新增：新功能", "优化:更快", "【修复】旧问题", "普通说明", "新增："] };
        vm.Apply(new(UpdateStage.Available, "发现更新", manifest));
        Program.Assert(vm.ReleaseNoteItems.Select(note => note.Tag).SequenceEqual(new[] { "新增", "优化", "修复", "更新", "更新" })
            && vm.ReleaseNoteItems[0].Text == "新功能" && vm.ReleaseNoteItems[^1].Text == "新增：",
            "说明标签分类丢失了原文或兼容性。");
        vm.Apply(new(UpdateStage.Downloading, "下载中", manifest, 200, 100));
        var items = vm.ReleaseNoteItems;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        vm.Apply(new(UpdateStage.Downloading, "下载中", manifest, 400, 200));
        Program.Assert(vm.PercentNumber == "40" && ReferenceEquals(items, vm.ReleaseNoteItems)
            && !notifications.Contains("") && !notifications.Contains(nameof(vm.ReleaseNoteItems))
            && notifications.Contains(nameof(vm.PercentNumber)), "同阶段进度重建了说明或通知全部界面。");
        vm.Apply(new(UpdateStage.Latest, "已是最新"));
        Program.Assert(vm.StatusSubtitle.Contains("上次检查"), "最新页面没有真实检查时间。");
        vm.AutomaticCheck = false;
        Program.Assert(vm.AutomaticCheckDescription.Contains("已关闭"), "最新页面的自动检查说明未同步。");
        var failed = new UpdateSnapshot(UpdateStage.Failed, "失败", manifest,
            Failure: UpdateFailure.Network, ErrorDetail: "真实错误详情");
        vm.Apply(failed);
        vm.ViewReleaseNotesCommand.Execute(null);
        Program.Assert(vm.IsReleaseNotesVisible && vm.IsViewingReleaseNotes && ReferenceEquals(vm.Snapshot, failed),
            "查看更新说明覆盖了失败状态。");
        vm.ReturnToStatusCommand.Execute(null);
        string? copied = null;
        vm.RequestCopy += detail => copied = detail;
        vm.CopyDetailsCommand.Execute(null);
        Program.Assert(vm.IsFailedStatus && vm.ErrorDetail == "真实错误详情" && copied?.Contains("真实错误详情") == true,
            "返回失败页或复制详情丢失了原始原因。");
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
