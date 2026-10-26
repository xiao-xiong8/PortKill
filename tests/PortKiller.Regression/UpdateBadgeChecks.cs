using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Features.Updates;
using PortKiller.Updates;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

internal static class UpdateBadgeChecks
{
    internal static async Task Run()
    {
        var main = new MainViewModel();
        // 先显示无 DataContext 的测试窗口，避免启动真实后台扫描和线上更新服务。
        var window = new MainWindow { ShowActivated = false, Width = 1180, Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
        window.Show();
        window.DataContext = main;
        var previousTheme = Application.Current!.RequestedThemeVariant;
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_UI_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        try
        {
            var manifest = new UpdateManifest { Version = "0.1.2", Size = 1000 };
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                foreach (var stage in Enum.GetValues<UpdateStage>())
                {
                    main.UpdateUi.Apply(new(stage, "测试", manifest, 380));
                    await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
                    window.Measure(new Size(1180, 700));
                    window.Arrange(new Rect(0, 0, 1180, 700));
                    window.UpdateLayout();
                    // 原生窗口命中测试读取上一帧的合成树，等待可见性/尺寸提交后再验证。
                    await Task.Delay(40);
                    var badge = window.FindControl<Button>("UpdateBadgeButton")!;
                    bool visible = stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Paused
                        or UpdateStage.Verifying or UpdateStage.Ready or UpdateStage.Failed;
                    Program.Assert(badge.IsVisible == visible && main.IsUpdateBadgeVisible == visible,
                        $"{theme}/{stage} 徽章状态未立即同步。");
                    if (visible)
                    {
                        Program.Assert(badge.Bounds.Width >= 68 && badge.Bounds.Height == 24
                            && ToolTip.GetTip(badge)?.ToString() == main.UpdateBadgeTooltip,
                            $"{theme}/{stage} 徽章布局或提示缺失：{badge.Bounds}，tip={ToolTip.GetTip(badge)}，expected={main.UpdateBadgeTooltip}。");
                        Program.Assert(main.UpdateBadgeMenuText.Length > 0 && main.UpdateBadgeText.Length > 0,
                            "徽章/托盘文案缺失。");
                        var point = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, 12), window)!.Value;
                        var hit = window.InputHitTest(point) as Visual;
                        Program.Assert(hit == badge || hit?.FindAncestorOfType<Button>() == badge,
                            $"{theme}/{stage} 版本徽章点击区域被覆盖：hit={hit?.GetType().Name}/{(hit as Control)?.Name}，point={point}，badge={badge.Bounds}。");
                    }
                    if (output != null && visible)
                    {
                        using var image = new RenderTargetBitmap(new PixelSize(1180, 700), new Vector(96, 96));
                        image.Render((Control)window.Content!);
                        image.Save(Path.Combine(output, $"MainBadge-{theme}-{stage}-100.png"), PngBitmapEncoderOptions.Default);
                    }
                }
                main.UpdateUi.Apply(new(UpdateStage.Downloading, "下载", manifest, 200));
                main.UpdateUi.Apply(new(UpdateStage.Downloading, "下载", manifest, 380));
                Program.Assert(main.UpdateBadgeText == "38%", "合并后的进度没有同步到主界面。");
            }
            int opens = 0;
            main.RequestShowUpdates += () => opens++;
            main.OpenUpdatesCommand.Execute(null);
            Program.Assert(opens == 1, "版本号入口未打开更新窗口。");

            main.UpdateUi.Apply(new(UpdateStage.Downloading, "下载", manifest, 380));
            await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
            var keyboardBadge = window.FindControl<Button>("UpdateBadgeButton")!;
            foreach (var key in new[] { Key.Enter, Key.Space })
            {
                keyboardBadge.Focus(NavigationMethod.Tab);
                keyboardBadge.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                keyboardBadge.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
            }
            Program.Assert(opens == 3, "徽章未响应 Enter/Space 键盘激活。");

            main.UpdateUi.Apply(new(UpdateStage.Ready, "就绪", manifest));
            main.IsUpdateToast = true;
            main.IsToastVisible = true;
            await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
            var toastAction = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ToastUpdateButton");
            Program.Assert(toastAction.IsEffectivelyVisible && toastAction.Content?.ToString() == "重启更新"
                && ReferenceEquals(toastAction.Command, main.OpenUpdateEntryCommand), "更新 Toast 未复用更新入口。");
            main.ShowToast("端口操作完成", "普通操作仍应显示复制报告");
            Program.Assert(!main.IsUpdateToast, "更新 Toast 操作泄漏到普通操作提示。");
            main.DismissToast();

            int installs = 0;
            using var vm = new UpdateViewModel(beginInstall: () => true,
                install: () => { installs++; return Task.FromResult<string?>(null); });
            vm.Apply(new(UpdateStage.Ready, "就绪", manifest));
            await vm.OpenOrRunUpdateAsync();
            await vm.OpenOrRunUpdateAsync();
            Program.Assert(vm.IsConfirming && installs == 0, "重启入口绕过确认或重复安装。");

            using var latest = new UpdateViewModel();
            latest.Apply(new(UpdateStage.Latest, "当前版本为最新"));
            await latest.OpenOrRunUpdateAsync();
            Program.Assert(latest.Notice.Length == 0 && latest.IsLatest, "最新版本入口触发了额外检查或服务错误。");

            await DirectDownload();
        }
        finally
        {
            window.PrepareClose();
            window.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
        Console.WriteLine("PASS 主界面徽章深浅主题、最终快照/进度同步、直接下载去重及重启确认");
    }

    private static async Task DirectDownload()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortKillBadge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var rsa = RSA.Create(2048);
            byte[] package = [1, 2, 3, 4];
            var manifest = new UpdateManifest { Schema = 2, Version = "1.0.2", Size = package.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(package)), PackageUrl = "https://example.com/release.zip",
                Sequence = 1, PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJsonContext.Default.UpdateManifest);
            byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate { Payload = Convert.ToBase64String(payload),
                Signature = Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
                UpdateJsonContext.Default.SignedUpdate);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int downloads = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".zip"))
                { downloads++; await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }; }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(envelope) };
            }));
            var service = new UpdateService(new UpdateOptions { Enabled = true, ManifestUrl = "https://example.com/manifest.json",
                PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() }, root, http, "1.0.1", trustPath: Path.Combine(root, "trust.json"));
            var main = new MainViewModel();
            main.UpdateUi.Attach(service);
            await service.CheckAsync(CancellationToken.None, download: false);
            await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
            Program.Assert(main.UpdateUi.IsAvailable && downloads == 0, "发现版本时已擅自下载。");
            Task entry = main.OpenUpdateEntryCommand.ExecuteAsync(null);
            for (int i = 0; downloads == 0 && i < 100; i++) await Task.Delay(10);
            await entry;
            Program.Assert(!main.OpenUpdateEntryCommand.IsRunning && main.OpenUpdateEntryCommand.CanExecute(null),
                "后台下载期间主界面更新入口仍被 AsyncRelayCommand 禁用。");
            Task repeated = main.OpenUpdateEntryCommand.ExecuteAsync(null);
            await repeated;
            Program.Assert(downloads == 1, "再次打开进度重复启动了下载。");
            release.SetResult();
            for (int i = 0; i < 100 && service.Snapshot.Stage != UpdateStage.Ready; i++) await Task.Delay(10);
            await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
            Program.Assert(downloads == 1 && main.UpdateUi.IsReady && service.Prepared != null,
                "徽章未直接下载，或重复入口创建了多个下载任务。");
            main.PrepareShutdown();
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
