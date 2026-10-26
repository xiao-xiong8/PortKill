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

/// <summary>可见桌面验收宿主；使用正式窗口与模板，不连接更新服务、不安装、不保存用户配置。</summary>
internal static class UpdateDpiProbe
{
    internal static void Run(string output, string preset)
    {
        Directory.CreateDirectory(output);
        global::PortKiller.Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        Exception? failure = null;
        Dispatcher.UIThread.UnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; stop.Cancel(); };
        Dispatcher.UIThread.Post(() =>
        {
            var main = new MainViewModel();
            // Show 无 DataContext，跳过正式启动中的扫描、托盘和线上更新服务。
            var window = new MainWindow { Width = 1180, Height = 700, Title = "PortKiller · 更新 DPI 验收" };
            window.Show();
            window.DataContext = main;
            var updateVm = new UpdateViewModel(beginInstall: () => true,
                install: () => Task.FromResult<string?>("验收宿主禁止安装"), currentVersion: "0.1.1");
            var updates = new UpdateWindow(updateVm);
            // Probe 的 Dispatcher 回调会先返回到消息循环；ViewModel 必须活到窗口关闭。
            updates.Closed += (_, _) => updateVm.Dispose();
            var manifest = new UpdateManifest { Version = "0.1.2", Size = 25_000_000, ReleaseNotes =
                ["新增：版本号右侧的更新提示，点击即可下载或查看进度。",
                 "优化：重启前确认安装，保留当前操作的处理时间。",
                 "优化：安装完成后清理已使用的更新包与临时文件。"] };
            string scenario = "Available-Light";
            long revision = 0;
            void Record()
            {
                if (!window.IsVisible) return;
                var badge = window.FindControl<Button>("UpdateBadgeButton")!;
                var point = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), window);
                var hit = point is { } p ? window.InputHitTest(p) as Visual : null;
                var screens = window.Screens.All.Select(s => new { s.IsPrimary, s.Scaling, Bounds = s.Bounds.ToString(), WorkingArea = s.WorkingArea.ToString() }).ToArray();
                object Describe(Window w) => new { w.IsVisible, Position = w.Position.ToString(), ClientSize = w.ClientSize.ToString(),
                    w.RenderScaling, ScreenScale = w.Screens.ScreenFromWindow(w)?.Scaling,
                    PhysicalWidth = Math.Round(w.ClientSize.Width * w.RenderScaling), PhysicalHeight = Math.Round(w.ClientSize.Height * w.RenderScaling) };
                var snapshot = new { Timestamp = DateTimeOffset.Now, Revision = ++revision, Scenario = scenario, Screens = screens,
                    Main = Describe(window), Update = Describe(updates), Badge = new { badge.IsVisible, Bounds = badge.Bounds.ToString(),
                        Center = point?.ToString(), HitCorrect = hit == badge || hit?.FindAncestorOfType<Button>() == badge },
                    Confirming = updateVm.IsConfirming };
                string json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(Path.Combine(output, "current.json"), json);
                File.AppendAllText(Path.Combine(output, "observations.jsonl"), json + Environment.NewLine);
            }
            void Export(Window target, string name)
            {
                if (!target.IsVisible || target.Content is not Control content) return;
                target.UpdateLayout();
                int width = Math.Max(1, (int)Math.Round(target.ClientSize.Width * target.RenderScaling));
                int height = Math.Max(1, (int)Math.Round(target.ClientSize.Height * target.RenderScaling));
                var dpi = new Vector(96 * target.RenderScaling, 96 * target.RenderScaling);
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), dpi);
                bitmap.Render(content);
                bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
            }
            void Set(UpdateStage stage, bool dark, bool show, bool confirm = false)
            {
                updateVm.CancelConfirmationCommand.Execute(null);
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var snapshot = new UpdateSnapshot(stage, "桌面验收状态", manifest, 9_500_000, 1_250_000);
                main.UpdateUi.Apply(snapshot);
                updateVm.Apply(snapshot);
                scenario = $"{stage}-{(dark ? "Dark" : "Light")}{(confirm ? "-Confirm" : "")}";
                if (show) updates.ShowFor(window); else updates.Hide();
                if (confirm) updateVm.RequestRestartConfirmation();
                Dispatcher.UIThread.Post(() =>
                {
                    window.UpdateLayout(); updates.UpdateLayout(); Record();
                    Export(window, $"Desktop-{scenario}.png");
                    Export(updates, $"Update-{scenario}.png");
                }, DispatcherPriority.Background);
            }
            void OnProbeKeyDown(object? sender, KeyEventArgs e)
            {
                switch (e.Key)
                {
                    case Key.F1: Set(UpdateStage.Available, false, false); break;
                    case Key.F2: Set(UpdateStage.Downloading, false, true); break;
                    case Key.F3: Set(UpdateStage.Ready, true, false); break;
                    case Key.F4: Set(UpdateStage.Ready, true, true, true); break;
                    case Key.F5:
                        Application.Current!.RequestedThemeVariant = Application.Current.RequestedThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
                        break;
                    case Key.F6: window.Width = window.Width > 1000 ? 900 : 1180; window.Height = window.Width > 1000 ? 700 : 600; break;
                    case Key.F7: Record(); break;
                    case Key.F8: updates.CloseForShutdown(); window.PrepareClose(); window.Close(); stop.Cancel(); break;
                    case Key.F9: updates.ShowFor(window); break;
                    case Key.F10: updates.Hide(); break;
                    case Key.F11:
                        main.ShowToast("更新已准备就绪", "版本 0.1.2 已下载并通过校验，可在方便时重启更新。");
                        main.IsUpdateToast = true; main.IsToastVisible = true; break;
                    default: return;
                }
                e.Handled = true;
            }
            main.RequestShowUpdates += () => updates.ShowFor(window);
            window.KeyDown += OnProbeKeyDown; updates.KeyDown += OnProbeKeyDown;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Record(); timer.Start();
            window.Closed += (_, _) =>
            {
                timer.Stop(); updates.CloseForShutdown(); updateVm.Dispose(); main.PrepareShutdown(); stop.Cancel();
            };
            switch (preset)
            {
                case "available": Set(UpdateStage.Available, false, true); break;
                case "downloading": Set(UpdateStage.Downloading, false, true); break;
                case "ready": Set(UpdateStage.Ready, true, true); break;
                case "confirm": Set(UpdateStage.Ready, true, true, confirm: true); break;
                case "badge": Set(UpdateStage.Available, false, false); break;
                default: throw new ArgumentException("预设状态必须是 available/downloading/ready/confirm/badge", nameof(preset));
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (failure != null) throw new InvalidOperationException("DPI 验收宿主失败", failure);
    }
}
