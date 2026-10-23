using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using IconPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Models;
using PortKiller.Services;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

internal static class ApplicationNameUiChecks
{
    internal static async Task Run()
    {
        await QueryIntentsSurviveMetadataUpdates();
        var app = Application.Current!;
        var themeBefore = app.RequestedThemeVariant;
        var typographyBefore = TypographyService.Compute(12, 12, true).Keys.ToDictionary(k => k, k => app.Resources[k]);
        var provider = new FakeProvider();
        var services = new PortKiller.Modules.Ports.PortToolServices(new NetworkScannerService(), new ProcessManagerService(),
            new ProcessDetailService(), new HttpSysDiagnosticService(), provider);
        var vm = new MainViewModel(services);
        var window = new MainWindow { Width = 1600, Height = 900, ShowActivated = false,
            Position = new PixelPoint(-20000, -20000), WindowStartupLocation = WindowStartupLocation.Manual };
        string? output = Environment.GetEnvironmentVariable("PORTKILLER_APP_NAME_ARTIFACTS");
        if (output != null) Directory.CreateDirectory(output);
        try
        {
            window.Classes.Add("nomotion");
            window.Show(); window.DataContext = vm; // 显示后绑定，跳过托盘和自动启动，不读写用户配置。
            var rows = Enumerable.Range(18000, 500).Select(x => ApplicationNameChecks.Row(x, 7000 + x % 2, x % 2 == 0 ? "wechat.exe" : "browser.exe")).ToArray();
            vm.ApplyScanResult(new ScanResult { Items = rows }, true);
            var first = vm.FilteredItems[0]; first.IsChecked = true;
            var original = vm.FilteredItems.ToArray();
            int collectionChanges = 0;
            vm.FilteredItems.CollectionChanged += (_, _) => collectionChanges++;
            provider.Publish(rows[0].ProcessIdentity, "微信");
            provider.Publish(rows[1].ProcessIdentity, "浏览器");
            await Pump(window);
            Program.Assert(original.SequenceEqual(vm.FilteredItems) && collectionChanges == 0
                && first.IsChecked && first.ApplicationName == "微信" && first.ProcessName == "wechat.exe",
                "仅应用名称更新未复用显示行、污染进程名或丢失选择。");

            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Default })
            {
                app.RequestedThemeVariant = theme;
                foreach (string tool in new[] { "settings", "kernel-diagnostics", "ports" })
                {
                    vm.SwitchToolCommand.Execute(tool); await Pump(window);
                    var buttons = window.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("nav")).ToArray();
                    Program.Assert(buttons.Length == 3 && buttons.Count(x => x.Classes.Contains("active")) == 1,
                        "导航没有保持唯一选中状态。");
                    var active = buttons.Single(x => x.Classes.Contains("active"));
                    Program.Assert(((PortKiller.Shell.ToolEntry)active.DataContext!).Id == tool, "导航样式选中项与页面不一致。");
                    foreach (string pseudo in new[] { "", ":pointerover", ":pressed", ":focus-visible" })
                    {
                        if (pseudo.Length > 0) ((IPseudoClasses)active.Classes).Set(pseudo, true);
                        await Pump(window);
                        var title = active.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Classes.Contains("navTitle"));
                        var icon = active.GetVisualDescendants().OfType<IconPath>().Single(x => x.Classes.Contains("navIcon"));
                        app.Resources.TryGetResource("AccentFg", window.ActualThemeVariant, out var resource);
                        var expected = (ISolidColorBrush)resource!;
                        Program.Assert(title.Foreground is ISolidColorBrush text && text.Color == expected.Color
                            && icon.Stroke is ISolidColorBrush stroke && stroke.Color == expected.Color,
                            $"{theme}/{tool}/{pseudo} 导航文字或图标未使用主题蓝色。");
                        foreach (var other in buttons.Where(x => !ReferenceEquals(x, active)))
                        {
                            var otherIcon = other.GetVisualDescendants().OfType<IconPath>().Single(x => x.Classes.Contains("navIcon"));
                            Program.Assert(otherIcon.Stroke is ISolidColorBrush otherStroke && otherStroke.Color != expected.Color,
                                "原导航项的图标未恢复默认颜色。");
                        }
                        if (pseudo.Length > 0) ((IPseudoClasses)active.Classes).Set(pseudo, false);
                    }
                }
                if (output != null && theme != ThemeVariant.Default)
                    Save(window, System.IO.Path.Combine(output, $"{theme}-1600.png"));
            }

            // 在最小窗口打开详情面板，验证新增列不会破坏表头对齐和垂直虚拟化。
            window.Width = 1180; window.Height = 700;
            vm.FocusRow(vm.FilteredItems[0]);
            await Pump(window);
            var body = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "PortListScroller");
            var header = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "PortHeaderScroller");
            Program.Assert(body.Extent.Width > body.Viewport.Width, "窄窗口下新增列没有可达的水平滚动范围。");
            foreach (double x in new[] { 0, 120, body.Extent.Width })
            {
                body.Offset = new Vector(x, 2400); await Pump(window);
                Program.Assert(Math.Abs(header.Offset.X - body.Offset.X) < 1, "表头与数据行水平偏移不同步。");
                var headerGrid = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "HeaderGrid");
                var realized = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("row") && b.IsVisible).ToArray();
                Program.Assert(realized.Length is > 0 and < 100, "横向滚动破坏了垂直虚拟化。");
                var sample = realized[0];
                var rowGrid = sample.GetVisualDescendants().OfType<Grid>().Single(g => g.ColumnDefinitions.Count == 9);
                for (int i = 0; i < 9; i++)
                    Program.Assert(Math.Abs(headerGrid.ColumnDefinitions[i].ActualWidth - rowGrid.ColumnDefinitions[i].ActualWidth) < 1,
                        $"表头与数据行第 {i} 列宽度不同：{headerGrid.ColumnDefinitions[i].ActualWidth}/{rowGrid.ColumnDefinitions[i].ActualWidth}。");
            }
            header.Offset = new Vector(50, 0); await Pump(window);
            Program.Assert(Math.Abs(header.Offset.X - body.Offset.X) < 1, "从表头横向滚动未同步数据行。");
            foreach (double font in new[] { 11.0, 12.0, 14.0, 16.0 })
            {
                foreach (var pair in TypographyService.Compute(font, Math.Min(14, font), true)) app.Resources[pair.Key] = pair.Value;
                await Pump(window);
                var realized = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row") && b.IsVisible).ToArray();
                Program.Assert(realized.All(r => Math.Abs(r.Bounds.Height - 48) < .1), "字体预设改变了固定行高。");
                Program.Assert(realized.SelectMany(r => r.GetVisualDescendants().OfType<TextBlock>())
                    .Where(t => t.Text is "微信" or "wechat.exe").All(t => t.TextWrapping == TextWrapping.NoWrap), "名称发生换行。");
            }
            foreach (var pair in typographyBefore) app.Resources[pair.Key] = pair.Value;
            await Pump(window);
            if (output != null) Save(window, System.IO.Path.Combine(output, $"{window.ActualThemeVariant}-1180-scrolled.png"));

            // 生产异步路径：先搜索未知应用，再发布名称；无需新的网络快照。
            vm.EnableBackgroundConnectionQueries();
            vm.SearchText = "新应用";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 0);
            provider.Publish(rows[0].ProcessIdentity, "新应用");
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 250);
            Program.Assert(vm.FilteredItems.All(r => r.ApplicationName == "新应用"), "名称补齐未重新匹配全量连接。");
            vm.ToggleColumnVisibility(7, persist: false);
            vm.SearchText = "browser";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 250 && vm.FilteredItems[0].ProcessName == "browser.exe");
            vm.SearchText = "新应用";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 250 && vm.FilteredItems[0].ApplicationName == "新应用");
            vm.SetListInteractionHold(true);
            provider.Publish(rows[0].ProcessIdentity, "已改名"); await Pump(window);
            Program.Assert(vm.FilteredCount == 250, "名称到达在交互期间提前重排/删除连接。");
            vm.SetListInteractionHold(false);
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 0);
            vm.SearchText = "已改名";
            vm.CurrentView = 2; await Pump(window);
            vm.CurrentView = 0;
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 250);
            if (output != null)
            {
                var measurements = new List<ApplicationNameMeasurement>();
                foreach (int size in new[] { 1000, 10000 })
                {
                    var large = Enumerable.Range(10000, size).Select(x => ApplicationNameChecks.Row(x, 7000 + x % 2,
                        x % 2 == 0 ? "wechat.exe" : "browser.exe")).ToArray();
                    vm.SearchText = ""; vm.ApplyScanResult(new ScanResult { Items = large }, true);
                    await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == size);
                    var times = new List<double>();
                    var commits = new List<double>();
                    var merges = new List<double>();
                    var metadataPublishes = new List<double>();
                    for (int sample = -5; sample < 50; sample++)
                    {
                        string search = sample % 2 == 0 ? "已改名" : "浏览器";
                        long start = System.Diagnostics.Stopwatch.GetTimestamp(); vm.SearchText = search;
                        await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == size / 2 && vm.FilteredItems[0].ApplicationName == search);
                        if (sample >= 0)
                        {
                            times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                            commits.Add(vm.LastConnectionCommitMilliseconds);
                            merges.Add(vm.LastConnectionMergeMilliseconds);
                            metadataPublishes.Add(vm.LastMetadataPublishMilliseconds);
                        }
                    }
                    measurements.Add(new(size, times.Count, ApplicationNameProbe.Percentile(times, .50),
                        ApplicationNameProbe.Percentile(times, .95), ApplicationNameProbe.Percentile(times, .99),
                        5, ApplicationNameProbe.Percentile(commits, .95), ApplicationNameProbe.Percentile(commits, .99),
                        ApplicationNameProbe.Percentile(merges, .95), ApplicationNameProbe.Percentile(metadataPublishes, .95), commits));
                }
                File.WriteAllText(System.IO.Path.Combine(output, "ui-query-latency.json"),
                    System.Text.Json.JsonSerializer.Serialize(measurements, ApplicationNameMeasurementContext.Default.ListApplicationNameMeasurement));
            }
            Console.WriteLine("PASS 独立应用名称列、异步补名搜索、交互收敛、水平滚动和三种主题导航文字/图标");
        }
        finally
        {
            window.PrepareClose(); window.Close(); provider.Dispose(); app.RequestedThemeVariant = themeBefore;
            foreach (var pair in typographyBefore) app.Resources[pair.Key] = pair.Value;
        }
    }
    private static async Task QueryIntentsSurviveMetadataUpdates()
    {
        using var provider = new FakeProvider();
        var scanner = new InspectorScanner();
        var services = new PortKiller.Modules.Ports.PortToolServices(scanner, new ProcessManagerService(),
            new ProcessDetailService(), new HttpSysDiagnosticService(), provider);
        var vm = new MainViewModel(services);
        var rows = Enumerable.Range(18000, 2)
            .Select(port => ApplicationNameChecks.Row(port, Environment.ProcessId, System.IO.Path.GetFileName(Environment.ProcessPath!)))
            .ToArray();
        try
        {
            vm.ApplyScanResult(new ScanResult { Items = rows }, true);
            await ApplicationNameChecks.WaitUntil(() => vm.CaptureMemoryDiagnosticState().InspectorPending == 0);
            vm.EnableBackgroundConnectionQueries();
            long previousQueries = vm.CaptureMemoryDiagnosticState().InspectorQueries;
            scanner.ChildPid = 900001;
            // 名称回调先进入 UI 队列，随后强制刷新入队；回调处理时必须保留刷新详情的意图。
            provider.Publish(rows[0].ProcessIdentity, "补全名称");
            vm.ApplyScanResult(new ScanResult { Items = rows, HasChanged = false }, true);
            await ApplicationNameChecks.WaitUntil(() => vm.InspChildren.Any(node => node.Pid == scanner.ChildPid));
            Program.Assert(vm.CaptureMemoryDiagnosticState().InspectorQueries == previousQueries + 1,
                "名称回调覆盖强制刷新，未重查进程子树或重复重查详情。");

            vm.SetListInteractionHold(true);
            // 在真实的 80ms 防抖查询读取名称快照时补名，确保补名回调早于查询提交。
            provider.BeforeNextSnapshotRead = () => provider.Publish(rows[0].ProcessIdentity, "最新名称");
            vm.SearchText = "没有匹配的应用";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 0);
            Program.Assert(vm.CaptureMemoryDiagnosticState().ListHolds > 0, "主动搜索等待交互闸口释放才提交。");
            vm.SetListInteractionHold(false);

            vm.SearchText = "最新名称";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == rows.Length);
            vm.SetListInteractionHold(true);
            provider.Publish(rows[0].ProcessIdentity, "再次改名");
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredItems.All(row => row.ApplicationName == "再次改名"));
            Program.Assert(vm.FilteredCount == rows.Length, "已提交搜索的交互豁免泄漏到后续后台名称更新。");
            vm.SetListInteractionHold(false);
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == 0);

            vm.SearchText = "";
            await ApplicationNameChecks.WaitUntil(() => vm.FilteredCount == rows.Length);
            vm.FocusRow(vm.FilteredItems[0]);
            await ApplicationNameChecks.WaitUntil(() => vm.CaptureMemoryDiagnosticState().InspectorPending == 0);
            previousQueries = vm.CaptureMemoryDiagnosticState().InspectorQueries;
            scanner.ChildPid++;
            vm.SetListInteractionHold(true);
            provider.Publish(rows[0].ProcessIdentity, "最终名称");
            vm.ApplyScanResult(new ScanResult { Items = rows }, true);
            // 再来一轮普通扫描，也不能把尚未提交的手动刷新降为后台交互策略。
            vm.ApplyScanResult(new ScanResult { Items = rows }, false);
            await ApplicationNameChecks.WaitUntil(() => vm.InspChildren.Any(node => node.Pid == scanner.ChildPid));
            Program.Assert(vm.CaptureMemoryDiagnosticState().InspectorQueries == previousQueries + 1,
                "连续扫描或补名丢失了手动刷新的详情更新意图。");
            vm.SetListInteractionHold(false);
        }
        finally { vm.PrepareShutdown(); }
        Console.WriteLine("PASS 补名与连续扫描保留主动搜索和强制刷新意图，提交后恢复后台交互闸口");
    }
    private sealed class InspectorScanner : NetworkScannerService
    {
        public int ChildPid;
        public override Dictionary<int, ProcessSnapshotItem> GetProcessSnapshotMap()
        {
            var result = new Dictionary<int, ProcessSnapshotItem>();
            if (ChildPid > 0) result[ChildPid] = new()
            { Pid = ChildPid, ParentPid = Environment.ProcessId, ProcessName = "review-child.exe" };
            return result;
        }
    }
    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(15); window.UpdateLayout();
    }
    private static void Save(Window window, string path)
    {
        using var target = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        target.Render(window); target.Save(path, PngBitmapEncoderOptions.Default);
    }
    private sealed class FakeProvider : IProcessMetadataProvider
    {
        private ImmutableDictionary<ProcessIdentity, ProcessMetadata> _snapshot = ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty;
        public Action? BeforeNextSnapshotRead;
        public event Action? Changed;
        public IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> Snapshot
        {
            get { Interlocked.Exchange(ref BeforeNextSnapshotRead, null)?.Invoke(); return _snapshot; }
        }
        public void Publish(ProcessIdentity identity, string name)
        { _snapshot = _snapshot.SetItem(identity, new(name, name, "", ProcessMetadataState.Available)); Changed?.Invoke(); }
        public IProcessMetadataSession CreateSession() => new Session();
        public void Dispose() { Changed = null; _snapshot = _snapshot.Clear(); }
        private sealed class Session : IProcessMetadataSession
        {
            public IReadOnlyDictionary<int, ProcessObservation> Observe(IReadOnlyList<ProcessReference> processes) =>
                ImmutableDictionary<int, ProcessObservation>.Empty;
            public void Dispose() { }
        }
    }
}
