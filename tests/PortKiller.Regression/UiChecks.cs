using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Models;
using PortKiller.Services;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

/// <summary>使用真实 Avalonia 模板和窗口验证启动、资源类型与托盘释放。</summary>
internal static class UiChecks
{
    /// <summary>整个窗口验证的时限，超时返回失败。</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>沿用正式程序渲染配置，在 STA 线程运行消息循环。</summary>
    public static void Run()
    {
        global::PortKiller.Program.BuildAvaloniaApp().SetupWithoutStarting();
        using var stop = new CancellationTokenSource(Deadline);
        Exception? failure = null;
        bool completed = false;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            failure = e.Exception;
            e.Handled = true;
            stop.Cancel();
        };
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                ShellChecks.NavigationAndExtensionLifetime();
                FontChecks.EmbeddedFacesAndFallback();
                await FontLicenseChecks.ViewerAndDocuments();
                await ContextMenuChecks.HoverTransitions();
                await DialogChecks.RoundedCardsAndInteraction();
                await UpdateUiChecks.StatesAndWindowLifetime();
                CheckTemplates();
                await CheckFontLoadingAsync(stop.Token);
                CheckListScrollingContract();
                await CheckInspectorAccuracyAsync(stop.Token);
                await CheckDiagnosticOrderingAsync();
                await CheckOperationPreparationAsync(stop.Token);
                await CheckMemoryDiagnosticsAsync(stop.Token);
                await CheckWindowLifecycleAsync(stop.Token);
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (failure != null) throw new InvalidOperationException("窗口回归失败。", failure);
        Program.Assert(completed, "窗口验证超时，可能存在 UI 阻塞。");
    }

    /// <summary>列表滚动必须实时、带惯性且不在行悬停时启动背景动画。</summary>
    private static void CheckListScrollingContract()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var vm = new MainViewModel();
        vm.TotalCount = 500;
        vm.FilteredCount = 500;
        for (int port = 10000; port < 10500; port++) vm.FilteredItems.Add(new PortConnectionItem
        {
            Protocol = "TCP",
            LocalAddress = "127.0.0.1",
            LocalPort = port,
            State = "LISTENING",
            RawState = PortKiller.Native.MibTcpState.Listen,
            Pid = Environment.ProcessId
        });
        var window = new MainWindow { DataContext = vm, ShowActivated = false };
        try
        {
            window.Measure(new Size(1440, 900));
            window.Arrange(new Rect(0, 0, 1440, 900));

            var rows = window.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("row")).ToArray();
            Program.Assert(rows.Length > 0, "端口列表没有生成数据行，无法验证悬停渲染。");
            Program.Assert(rows.Length < 100,
                $"初始视口创建了 {rows.Length} 行，虚拟化未限制控件数量。");
            Program.Assert(rows.All(x => x.Transitions is null || x.Transitions.Count == 0),
                "数据行仍配置 Background 过渡，快速移动鼠标时会触发闪烁。");

            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(x => x.GetVisualDescendants().OfType<ItemsControl>()
                    .Any(items => ReferenceEquals(items.ItemsSource, vm.FilteredItems)));
            Program.Assert(scroll is not null, "端口列表滚动容器未找到。");
            Program.Assert(!scroll!.IsDeferredScrollingEnabled,
                "端口列表启用了延迟滚动，拖动滚动条时内容不会实时跟随。");
            Program.Assert(scroll.IsScrollInertiaEnabled,
                "端口列表未启用滚动惯性，滚轮和触控板滚动不够平滑。");

            var panel = scroll.GetVisualDescendants().OfType<ConnectionViewportPanel>().FirstOrDefault();
            Program.Assert(panel is not null, "端口列表未使用虚拟化面板。");
            Program.Assert(scroll.Extent.Height > scroll.Viewport.Height * 2,
                "测试数据未形成可滚动列表。");
            scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
            window.Measure(new Size(1440, 900));
            window.Arrange(new Rect(0, 0, 1440, 900));
            int middleRows = window.GetVisualDescendants().OfType<Border>()
                .Count(x => x.Classes.Contains("row"));
            Program.Assert(middleRows is > 0 and < 100,
                $"滚动到中段后保留了 {middleRows} 行，虚拟化未限制控件数量。");

            // 让真实 ItemsSourceView / 虚拟化面板消费多项 Replace、Add、Remove。
            // 仅验证模型集合不足以发现框架不支持范围通知或回收后显示旧数据。
            var collection = vm.FilteredItems;
            var initialRows = window.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("row")).ToHashSet();
            RefreshChecks.Publish(vm, Enumerable.Range(20000, 500)
                .Select(p => RefreshChecks.Row(p, PortKiller.Native.MibTcpState.TimeWait)).ToList(), force: true);
            window.Measure(new Size(1440, 900));
            window.Arrange(new Rect(0, 0, 1440, 900));
            var replacedRows = window.GetVisualDescendants().OfType<Border>()
                .Where(x => x.Classes.Contains("row") && x.IsVisible).ToArray();
            Program.Assert(replacedRows.Any(initialRows.Contains), "等长刷新重建了所有可视行模板。");
            foreach (var row in replacedRows)
            {
                var item = (PortConnectionItem)row.DataContext!;
                Program.Assert(item.IsTimeWait && row.GetVisualDescendants().OfType<TextBlock>()
                    .Any(x => x.Text == item.LocalPort.ToString()), $"复用模板端口文字或状态未更新：{item.LocalPort}/{item.State}。");
                Program.Assert(row.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "TIME_WAIT") &&
                    row.GetVisualDescendants().OfType<Button>().Any(x => Equals(x.Content, "释放") && x.IsVisible),
                    "复用模板状态胶囊或操作按钮过期。");
            }
            foreach (int count in new[] { 900, 60, 750, 0, 1000 })
            {
                RefreshChecks.Publish(vm, Enumerable.Range(20000, count)
                    .Select(p => RefreshChecks.Row(p)).ToList(), force: true);
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                var realized = window.GetVisualDescendants().OfType<Border>()
                    .Where(x => x.Classes.Contains("row")).ToArray();
                Program.Assert(ReferenceEquals(collection, vm.FilteredItems) && realized.Length < 100,
                    "高变化刷新破坏了集合复用或虚拟化。");
                Program.Assert(realized.Where(x => x.DataContext is PortConnectionItem)
                    .All(x => collection.Contains((PortConnectionItem)x.DataContext!)),
                    "范围更新后容器仍显示旧连接。");
            }
            foreach (double fraction in new[] { .0, .9, .1, .5, 1.0 })
            {
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) * fraction);
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                var realized = window.GetVisualDescendants().OfType<Border>()
                    .Where(x => x.Classes.Contains("row") && x.IsVisible).ToArray();
                Program.Assert(realized.Length is > 0 and < 100,
                    $"大幅滚动产生空白或实现了全部行：{fraction}/{realized.Length}，{scroll.Offset}/{scroll.Viewport}，" +
                    $"slots={panel!.Children.Count}，" + string.Join(";", panel.Children.Take(2).Select(x =>
                        $"visible={x.IsVisible},dc={x.DataContext},bounds={x.Bounds}")));
                foreach (var row in realized)
                {
                    int index = (int)Math.Round(row.Bounds.Y / ConnectionViewportPanel.RowHeight);
                    Program.Assert(index >= 0 && index < collection.Count &&
                        ReferenceEquals(row.DataContext, collection[index]), "滚动位置与槽位数据不一致。");
                    Program.Assert(row.GetVisualDescendants().OfType<TextBlock>()
                        .Any(x => x.Text == collection[index].LocalPort.ToString()), "滚动回收后显示旧端口文字。");
                }
            }
        }
        finally
        {
            window.PrepareClose();
            window.Close();
        }
        Console.WriteLine("PASS 列表悬停与滚动性能契约");
    }

    private static async Task CheckInspectorAccuracyAsync(CancellationToken cancellationToken)
    {
        var vm = new MainViewModel();
        System.Diagnostics.Process? child = null;
        try
        {
            var row = RefreshChecks.Row(10000);
            row.Pid = Environment.ProcessId;
            row.ProcessName = "inspector-regression.exe";
            var peer = RefreshChecks.Row(10001);
            peer.Pid = row.Pid;
            peer.ProcessName = row.ProcessName;
            RefreshChecks.Publish(vm, new() { row, peer });
            while (vm.InspExePath == "正在读取…") await Task.Delay(10, cancellationToken);
            Program.Assert(System.IO.File.Exists(vm.InspExePath), "详情没有读到自有进程路径。");
            var previousPath = vm.InspExePath;
            int pathChanges = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.InspExePath)) pathChanges++; };
            vm.FocusRow(peer);
            vm.FocusRow(peer);
            Program.Assert(vm.InspExePath == previousPath && pathChanges == 0 && vm.InspMeta.Contains("10001"),
                "同一进程切换端口重复查询详情或端口信息未更新。");

            child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--owned-child")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
            // 网络表完全相同，F5 仍必须重新读取已经变化的进程子树。
            vm.ApplyScanResult(new ScanResult { Items = new PortConnection[] { row, peer }, HasChanged = false, Fingerprint = 1 }, force: true);
            while (vm.InspExePath == "正在读取…") await Task.Delay(10, cancellationToken);
            Program.Assert(vm.InspChildren.Any(x => x.Pid == child.Id), "强制刷新未更新同一进程的子树。");
            vm.FocusRow(RefreshChecks.Row(11000));
            Program.Assert(vm.InspExePath == "正在读取…" && vm.InspStartTime == "—" && vm.InspChildren.Count == 0,
                "新进程加载期间仍显示或允许复制旧进程详情。");
        }
        finally
        {
            vm.PrepareShutdown();
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); }
                child.Dispose();
            }
        }
        Console.WriteLine("PASS 进程详情复用、切换准确性与强制刷新子树");
    }

    private static async Task CheckMemoryDiagnosticsAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"PortKiller-memory-test-{Guid.NewGuid():N}.csv");
        int captured = 0;
        var session = MemoryDiagnosticSession.Start(() =>
        {
            Program.Assert(Dispatcher.UIThread.CheckAccess(), "诊断跨线程读取了界面集合。");
            captured++;
            return new MemoryDiagnosticState { SnapshotRows = 321, FilteredRows = 12, AutoRefresh = true };
        }, path, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(30));
        try
        {
            while (captured < 2) await Task.Delay(10, cancellationToken);
            session.Stop();
            await session.Completion.WaitAsync(cancellationToken);
            session.Stop();
            string[] lines = await File.ReadAllLinesAsync(path, cancellationToken);
            string[] header = lines[0].Split(',');
            Program.Assert(lines.Length >= 3 && header.Length == 43 && lines.Skip(1).All(x => x.Split(',').Length == header.Length),
                "诊断 CSV 丢样本或列错位。");
            int sourceIndex = Array.IndexOf(header, "SnapshotRows");
            Program.Assert(lines.Skip(1).All(x => x.Split(',')[sourceIndex] == "321") &&
                (await File.ReadAllTextAsync(path + ".metadata.txt", cancellationToken)).Contains("Reason=stopped"),
                "日志上下文不准确或停止后未关闭输出。");
        }
        finally
        {
            session.Stop();
            await session.Completion;
            File.Delete(path);
            File.Delete(path + ".metadata.txt");
        }
        string deadlinePath = path + ".deadline.csv";
        var deadline = MemoryDiagnosticSession.Start(() => default, deadlinePath,
            TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(30));
        try
        {
            await deadline.Completion.WaitAsync(cancellationToken);
            Program.Assert((await File.ReadAllTextAsync(deadlinePath + ".metadata.txt", cancellationToken)).Contains("Reason=duration-limit"),
                "临时诊断未在到期时自动停止。");
        }
        finally
        {
            deadline.Stop();
            await deadline.Completion;
            File.Delete(deadlinePath);
            File.Delete(deadlinePath + ".metadata.txt");
        }
        Console.WriteLine("PASS 临时内存日志列、UI 采样和幂等停止");
    }

    private static async Task CheckFontLoadingAsync(CancellationToken cancellationToken)
    {
        bool inputRan = false;
        int inputAt = -1, validated = 0;
        var families = Enumerable.Range(0, 30).Select(i => new Avalonia.Media.FontFamily($"TestFont{i:D2}"))
            .Concat(new[] { new Avalonia.Media.FontFamily("TestFont00"), new Avalonia.Media.FontFamily("Inter") });
        var sliced = await TypographyService.BuildFontListAsync(families, name =>
        {
            Program.Assert(Dispatcher.UIThread.CheckAccess(), "字体后端检查离开了 UI 线程。");
            if (++validated == 1)
                Dispatcher.UIThread.Post(() => { inputRan = true; inputAt = validated; }, DispatcherPriority.Input);
            // 放大一次字体探针的成本，检查加载中让出调度的行为，不作为真实字体性能数据。
            Thread.Sleep(2);
            return name != "TestFont05";
        });
        Program.Assert(inputRan && inputAt < 30 && validated == 30,
            "字体检查未在加载中处理输入，或重复/内置字体未被去重。");
        Program.Assert(sliced.Count == 30 && sliced[0].IsDefault &&
            sliced.All(x => x.FamilyName != "TestFont05") &&
            sliced.Skip(1).Select(x => x.DisplayName).SequenceEqual(sliced.Skip(1)
                .Select(x => x.DisplayName).OrderBy(x => x, StringComparer.CurrentCulture)),
            "分片加载丢失默认项、排序或不可用字体过滤。");

        while (!TypographyService.IsFontListReady) await Task.Delay(10, cancellationToken);
        var cached = TypographyService.Fonts;
        int readyEvents = 0;
        void OnReady() => readyEvents++;
        TypographyService.FontListReady += OnReady;
        try
        {
            TypographyService.RequestFontList();
            TypographyService.RequestFontList();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Program.Assert(ReferenceEquals(cached, TypographyService.Fonts) && readyEvents == 0 &&
                !TypographyService.IsFontListBuilding && cached.Count > 1 && cached[0].IsDefault,
                "本机字体加载未完成、重复加载或丢失缓存。");
        }
        finally { TypographyService.FontListReady -= OnReady; }
        Console.WriteLine($"PASS 字体分片让出输入、去重过滤及本机字体缓存（{cached.Count} 项）");
    }

    /// <summary>准备阶段与确认阶段都不能被其他终止入口覆盖；使用合成快照，不执行终止。</summary>
    private static async Task CheckOperationPreparationAsync(CancellationToken cancellationToken)
    {
        var scanner = new ControlledSnapshotScanner();
        var vm = new MainViewModel(new HttpSysDiagnosticService(), scanner);
        var root = RefreshChecks.Row(12000);
        try
        {
            RefreshChecks.Publish(vm, new() { root });
            while (vm.InspExePath == "正在读取…") await Task.Delay(10, cancellationToken);
            vm.ToggleCheck(root);
            scanner.Arm();
            var focused = vm.KillFocusedAsync();
            await scanner.Started.Task.WaitAsync(cancellationToken);
            await vm.KillFocusedAsync();
            await vm.KillCheckedAsync();
            await vm.KillFocusedTreeAsync();
            await vm.ConfirmFamilyKillAsync();
            Program.Assert(scanner.RequestCount == 1 && !vm.IsFamilyConfirmVisible,
                "准备阶段仍创建了重复目标查询或错误弹窗。");
            scanner.Complete(FamilySnapshot(root));
            await focused;
            Program.Assert(vm.IsFamilyConfirmVisible && vm.FamilyConfirmChips.Count == 2,
                "目标准备未打开对应的确认范围。");
            string desc = vm.FamilyConfirmDesc;
            await vm.KillCheckedAsync();
            await vm.KillFocusedAsync();
            Program.Assert(scanner.RequestCount == 1 && vm.FamilyConfirmDesc == desc,
                "待确认范围被另一个入口覆盖。");
            vm.CancelFamilyKill();

            scanner.Arm();
            var batch = vm.KillCheckedAsync();
            await scanner.Started.Task.WaitAsync(cancellationToken);
            await vm.KillFocusedAsync();
            await vm.KillCheckedAsync();
            Program.Assert(scanner.RequestCount == 2, "批量准备未阻止重复查询。");
            scanner.Complete(FamilySnapshot(root));
            await batch;
            Program.Assert(vm.IsFamilyConfirmVisible && vm.FamilyConfirmChips.Count == 2,
                "取消上次操作后入口未恢复，或批量确认范围错误。");
            vm.CancelFamilyKill();

            scanner.Arm();
            var closing = vm.KillFocusedAsync();
            await scanner.Started.Task.WaitAsync(cancellationToken);
            vm.PrepareShutdown();
            scanner.Complete(FamilySnapshot(root));
            await closing;
            Program.Assert(!vm.IsFamilyConfirmVisible, "退出后仍发布了待确认目标。");
        }
        finally
        {
            scanner.Complete(new());
            vm.PrepareShutdown();
        }
        foreach (bool batch in new[] { false, true })
        {
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--owned-child")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!;
            var executingScanner = new ControlledSnapshotScanner();
            executingScanner.Arm();
            executingScanner.Complete(new()
            {
                [child.Id] = new() { Pid = child.Id, ProcessName = child.ProcessName + ".exe" }
            });
            var executing = new MainViewModel(new HttpSysDiagnosticService(), executingScanner);
            try
            {
                var owned = RefreshChecks.Row(0, process: child.ProcessName + ".exe");
                owned.Pid = child.Id;
                RefreshChecks.Publish(executing, new() { owned });
                if (batch)
                {
                    executing.ToggleCheck(owned);
                    await executing.KillCheckedAsync();
                }
                else await executing.KillFocusedAsync();
                Program.Assert(child.WaitForExit(2000), "入口的 busy 状态阻止了自己的实际执行阶段。");
            }
            finally
            {
                executing.PrepareShutdown();
                if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); }
            }
        }
        Console.WriteLine("PASS 终止准备互斥、确认范围保护、取消恢复与退出丢弃");
    }

    private static Dictionary<int, ProcessSnapshotItem> FamilySnapshot(PortConnectionItem root) => new()
    {
        [root.Pid] = new() { Pid = root.Pid, ProcessName = root.ProcessName },
        [root.Pid + 1] = new() { Pid = root.Pid + 1, ParentPid = root.Pid, ProcessName = "owned-test-child.exe" }
    };

    private sealed class ControlledSnapshotScanner : NetworkScannerService
    {
        private TaskCompletionSource<Dictionary<int, ProcessSnapshotItem>>? _completion;
        public TaskCompletionSource<bool> Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount;

        public void Arm()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Complete(Dictionary<int, ProcessSnapshotItem> snapshot) => _completion?.TrySetResult(snapshot);

        public override Dictionary<int, ProcessSnapshotItem> GetProcessSnapshotMap()
        {
            var pending = _completion;
            if (pending is null) return new();
            Interlocked.Increment(ref RequestCount);
            Started.TrySetResult(true);
            return pending.Task.GetAwaiter().GetResult();
        }
    }

    /// <summary>不同入口的请求乱序完成时，只能发布最新诊断，并取消过期任务。</summary>
    private static async Task CheckDiagnosticOrderingAsync()
    {
        var service = new ControlledDiagnosticService();
        var vm = new MainViewModel(service);
        try
        {
            var states = vm.LoadServiceStatesAsync();
            Program.Assert(ReferenceEquals(states, vm.LoadServiceStatesAsync()), "并发服务状态查询未合并。");
            await states;

            vm.KernelItems.Add(RefreshChecks.Row(80));
            var first = vm.RunKernelDiagnosticAsync();
            vm.FocusRow(RefreshChecks.Row(8080));
            var second = vm.DiagnoseFocusedAsync();
            Program.Assert(service.Requests.Count == 2 && service.Requests[0].Token.IsCancellationRequested,
                "切换诊断目标未取消前一个请求。");
            service.Requests[1].Completion.SetResult(new() { Port = 8080, Summary = "latest:8080" });
            await second;
            service.Requests[0].Completion.SetResult(new() { Port = 80, Summary = "stale:80" });
            await first;
            Program.Assert(vm.DiagnosticReport == "latest:8080", "较慢的旧请求覆盖了新端口报告。");

            var failing = vm.RunKernelDiagnosticAsync();
            var newest = vm.DiagnoseFocusedAsync();
            service.Requests[3].Completion.SetResult(new() { Port = 8080, Summary = "newest:8080" });
            await newest;
            service.Requests[2].Completion.SetException(new InvalidOperationException("过期请求的错误"));
            await failing;
            Program.Assert(vm.DiagnosticReport == "newest:8080", "过期请求的错误覆盖了最新成功报告。");

            var pending = vm.RunKernelDiagnosticAsync();
            vm.PrepareShutdown();
            var request = service.Requests[4];
            Program.Assert(request.Token.IsCancellationRequested, "退出未取消在途诊断。");
            request.Completion.SetCanceled(request.Token);
            await pending;
            await vm.RunKernelDiagnosticAsync();
            Program.Assert(service.Requests.Count == 5, "退出后仍创建了诊断任务。");
        }
        finally { vm.PrepareShutdown(); }
        Console.WriteLine("PASS 诊断乱序、过期错误、退出取消及服务查询合并");
    }

    private sealed class ControlledDiagnosticService : HttpSysDiagnosticService
    {
        public List<(CancellationToken Token, TaskCompletionSource<HttpSysDiagnosticResult> Completion)> Requests { get; } = new();

        public override Task<HttpSysDiagnosticResult> DiagnosePortAsync(int port, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<HttpSysDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((cancellationToken, completion));
            // 故意允许取消后的任务完成/失败，验证发布侧不会只依赖服务合作取消。
            return completion.Task;
        }
    }

    /// <summary>未启动的模型不写配置；强制套用所有滑块模板验证资源类型。</summary>
    private static void CheckTemplates()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        vm.CurrentView = 2;
        vm.SettingsTab = "appearance";
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Application.Current.TryGetResource("SliderPreContentMargin", theme, out var preMargin);
            Application.Current.TryGetResource("SliderPostContentMargin", theme, out var postMargin);
            Program.Assert(preMargin is GridLength { Value: 5 } && postMargin is GridLength { Value: 5 },
                $"{theme} 主题滑块上下留白不是 5px，字号滑块 Thumb 可能被裁剪。");
            foreach (var slider in window.GetLogicalDescendants().OfType<Slider>()) slider.ApplyTemplate();
            window.Measure(new Size(1440, 900));
            window.Arrange(new Rect(0, 0, 1440, 900));
            var typographySliders = window.GetLogicalDescendants().OfType<Slider>()
                .Where(x => x.Classes.Contains("typescale")).ToArray();
            Program.Assert(typographySliders.Length == 2, "字号设置页应有界面和数据两条滑块。");
            foreach (var slider in typographySliders)
            {
                var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
                if (thumb is null) throw new InvalidOperationException($"{theme} 主题字号滑块没有 Thumb。");
                var topLeft = thumb.TranslatePoint(new Point(0, 0), slider);
                Program.Assert(thumb.Bounds.Height >= 17.5 && slider.Bounds.Height >= 27.5 &&
                    topLeft.HasValue && topLeft.Value.Y >= -0.5 &&
                    topLeft.Value.Y + thumb.Bounds.Height <= slider.Bounds.Height + 0.5,
                    $"{theme} 主题字号滑块圆点超出控件边界：Thumb={thumb.Bounds}, Slider={slider.Bounds}。");
            }
        }
        var copy = window.GetLogicalDescendants().OfType<Button>().First(x => Equals(x.Content, "复制报告"));
        Program.Assert(ReferenceEquals(copy.Command, vm.CopyOperationReportCommand), "复制报告仍绑定到当前行。");
        vm.SettingsTab = "engine";
        vm.IsUpdateReady = true;
        vm.RequestInstallUpdateCommand.Execute(null);
        Program.Assert(!vm.IsUpdateConfirmVisible, "没有已校验的更新包仍允许安装确认。");
        var openUpdate = window.GetLogicalDescendants().OfType<Button>().First(x => Equals(x.Content, "打开更新窗口"));
        Program.Assert(ReferenceEquals(openUpdate.Command, vm.OpenUpdatesCommand), "设置页没有打开独立更新窗口的入口。");
        vm.CancelInstallUpdateCommand.Execute(null);
        Program.Assert(!vm.IsUpdateConfirmVisible, "稍后更新没有关闭确认。");
        vm.IsInstallingUpdate = true;
        vm.RequestInstallUpdateCommand.Execute(null);
        Program.Assert(!vm.IsUpdateConfirmVisible, "安装准备期间允许重复打开确认。");
        vm.IsInstallingUpdate = false;
        window.PrepareClose();
        window.Close();
        Console.WriteLine("PASS 深浅主题模板及复制报告绑定");
    }

    /// <summary>实际启动窗口并刷新；关闭前确认托盘集合立即清空。</summary>
    private static async Task CheckWindowLifecycleAsync(CancellationToken cancellationToken)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, ShowActivated = false, Position = new PixelPoint(-20000, -20000) };
        try
        {
            window.Show();
            while (vm.TotalCount == 0 && !vm.HasScanError)
                await Task.Delay(50, cancellationToken);
            Program.Assert(!vm.HasScanError && vm.TotalCount > 0, "启动未完成有效扫描。");
            await Task.WhenAll(vm.Refresh(), vm.Refresh(), vm.Refresh());
            Program.Assert(!vm.HasScanError, "并发刷新失败。");
            var pending = vm.Refresh();
            window.PrepareClose();
            window.PrepareClose();
            await pending;
            Program.Assert(TrayIcon.GetIcons(Application.Current!)?.Count == 0, "退出后托盘集合未清空。");
            Console.WriteLine($"PASS 窗口启动、合并刷新、扫描中退出和托盘清理（{vm.TotalCount} 条连接）");
        }
        finally { window.PrepareClose(); window.Close(); }
    }
}
