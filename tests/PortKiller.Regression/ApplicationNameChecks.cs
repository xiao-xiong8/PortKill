using System.Collections.Immutable;
using System.Diagnostics;
using PortKiller.Features.Ports.Services;
using PortKiller.Models;
using PortKiller.Processes;
using PortKiller.Services;

namespace PortKiller.Regression;

internal static class ApplicationNameChecks
{
    internal static void ColumnMigration()
    {
        var old = new AppSettings { ColWidths = "91,121,122,0,73,161,117", ColOrder = "6,4,3,5,2,1,0",
            ColVisible = "True,False,True,False,True,False,True" };
        ColumnSettingsMigrator.Migrate(old);
        Program.Assert(old.ColWidths == "91,121,122,220,73,161,117,144" && old.ColOrder == "6,4,7,3,5,2,1,0"
            && old.ColVisible == "True,False,True,False,True,False,True,True", "旧列布局迁移丢失用户设置。");
        var migrated = (old.ColWidths, old.ColOrder, old.ColVisible);
        ColumnSettingsMigrator.Migrate(old);
        Program.Assert(migrated == (old.ColWidths, old.ColOrder, old.ColVisible), "重复迁移改变了有效布局。");
        old.ColWidths = "88,NaN,-1,0,72,Infinity,116,150";
        old.ColOrder = "7,7,3,99,0";
        old.ColVisible = "False,bad,False";
        ColumnSettingsMigrator.Migrate(old);
        Program.Assert(old.ColWidths == "88,132,132,220,72,180,116,150"
            && old.ColOrder.Split(',').Select(int.Parse).Order().SequenceEqual(Enumerable.Range(0, 8))
            && old.ColVisible.StartsWith("False,True,False,"), "异常字段修复未保留其他有效值。");
    }

    internal static PortConnection Row(int port, int pid, string process) => new()
    {
        LocalAddress = "127.0.0.1", RemoteAddress = "127.0.0.1", LocalPort = port, RemotePort = 443,
        Pid = pid, ProcessCreationTime = 100, ProcessName = process,
        State = "ESTABLISHED", RawState = PortKiller.Native.MibTcpState.Established
    };
    internal static void SearchAndSorting()
    {
        var rows = new[] { Row(18080, 6000, "wechat.exe"), Row(19090, 6001, "alpha.exe"), Row(20000, 6002, "z.exe") };
        var metadata = ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty
            .Add(rows[0].ProcessIdentity, new("微信", "微信桌面客户端", "Tencent WeChat 9", ProcessMetadataState.Available))
            .Add(rows[1].ProcessIdentity, new("Browser", "Web browser", "Browser", ProcessMetadataState.Available));
        PortQuery Query(string search, int column = 0, bool ascending = true) =>
            new(rows, metadata, search, "全部分类", "全部状态", column, ascending);
        foreach (string search in new[] { "微信", "WeChAt", "桌面", "Tencent", "9", "微信 443", "18080", "6000", ":18080", "18000-18500" })
            Program.Assert(PortConnectionQueryService.Evaluate(Query(search)).Single().Pid == 6000, "名称/旧搜索语义失效：" + search);
        Program.Assert(PortConnectionQueryService.Evaluate(Query("—")).Count == 0
            && PortConnectionQueryService.Evaluate(Query("微信 19090")).Count == 0, "占位符被当作名称或多词未使用 AND。");
        foreach (bool ascending in new[] { true, false })
        {
            var sorted = PortConnectionQueryService.Evaluate(Query("", 7, ascending));
            Program.Assert(sorted[^1].Pid == 6002, "应用名称缺失在升降序下未排到末尾。");
        }
        Program.Assert(PortConnectionQueryService.Evaluate(Query("", 3)).Select(x => x.ProcessName)
            .SequenceEqual(new[] { "alpha.exe", "wechat.exe", "z.exe" }), "进程名列被改成应用名称排序。");
        rows[0].RawState = PortKiller.Native.MibTcpState.TimeWait;
        Program.Assert(PortConnectionQueryService.Evaluate(Query("微信")).Count == 0, "TIME_WAIT 仍绑定存活进程应用名称。");
        var row = PortConnectionItem.FromSnapshot(rows[1]);
        row.ApplyMetadata(metadata[rows[1].ProcessIdentity]);
        Program.Assert(row.ApplicationNameDisplay == "Browser" && row.ProcessDisplay == "alpha.exe", "两个名称字段未独立。");
        row.ApplyMetadata(null);
        Program.Assert(row.ApplicationNameDisplay == "—" && row.ProcessDisplay == "alpha.exe", "缺失应用名称污染进程名。");
        var next = Row(19090, 6001, "alpha.exe"); next.ProcessCreationTime++;
        Program.Assert(next.Identity != rows[1].Identity, "同名 PID 复用未区分创建时间。");
    }

    internal static void SortingRemainsStableAcrossSnapshots()
    {
        PortConnection Binding(string protocol = "UDP", string version = "IPv4", string local = "10.0.0.1",
            string remote = "", string process = "mdns.exe", long created = 100) => new()
        {
            Protocol = protocol, IpVersion = version, LocalAddress = local, RemoteAddress = remote,
            LocalPort = 5353, RemotePort = 0, Pid = 5000, ProcessName = process, ProcessCreationTime = created,
            State = "LISTENING", RawState = PortKiller.Native.MibTcpState.Listen
        };
        var bindings = Enumerable.Range(1, 4).Select(i => Binding(local: $"10.0.0.{i}")).Concat(new[]
        {
            Binding(protocol: "TCP"), Binding(version: "IPv6"), Binding(remote: "10.0.1.1"),
            Binding(process: "MDNS.exe"), Binding(created: 101)
        });
        var source = Enumerable.Range(1, 50).Select(i => Row(1000 + i * 100, 6000, "other.exe"))
            .Concat(bindings).ToArray();
        var extra = Row(1500, 7000, "new.exe");
        var names = ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty
            .Add(new(5000, 100), new("应用", "应用", "", ProcessMetadataState.Available))
            .Add(new(5000, 101), new("应用", "应用", "", ProcessMetadataState.Available));
        foreach (var metadata in new[] { PortsProcessMetadataAdapter.Empty, names })
        foreach (int column in new[] { 0, 1, 2, 3, 4, 5, 7 })
        foreach (bool ascending in new[] { true, false })
        {
            PortConnection.ConnectionIdentity[] Ordered(IEnumerable<PortConnection> rows) =>
                PortConnectionQueryService.Evaluate(new(rows.ToArray(), metadata, "", "全部分类", "全部状态", column, ascending))
                    .Where(row => !ReferenceEquals(row, extra)).Select(row => row.Identity).ToArray();
            var expected = Ordered(source);
            Program.Assert(expected.SequenceEqual(Ordered(source.Append(extra)))
                && expected.SequenceEqual(Ordered(source.Reverse()))
                && expected.SequenceEqual(Ordered(source.Reverse().Prepend(extra))),
                $"第 {column} 列/{ascending} 排序随无关连接或采集顺序变化，未完整区分协议、地址和进程实例。");
        }
    }

    internal static async Task CacheAndLifetime()
    {
        var clock = new FakeClock();
        var reader = new FakeReader();
        using var provider = new WindowsProcessMetadataProvider(reader, clock);
        using var first = provider.CreateSession();
        var refs = new[] { new ProcessReference(8000, "fake.exe"), new ProcessReference(8001, "fake.exe") };
        var observed = first.Observe(refs);
        await WaitUntil(() => provider.Snapshot.Values.All(x => x.State == ProcessMetadataState.Available));
        Program.Assert(reader.Reads == 1 && provider.FileReads == 1, "同文件多个进程重复解析版本资源。");
        using (var second = provider.CreateSession())
        {
            second.Observe(refs);
            first.Observe(Array.Empty<ProcessReference>());
            Program.Assert(provider.Snapshot.Count == 2, "一个消费者退订删除了其他消费者的名称。");
            await Task.Delay(40);
            Program.Assert(reader.Reads == 1, "共享消费者导致重复文件读取。");
        }
        Program.Assert(provider.Snapshot.Count == 0, "最后消费者释放后进程实例映射未清理。");
        first.Observe(refs);
        reader.Birth++;
        var next = first.Observe(refs);
        Program.Assert(observed[8000].Identity != next[8000].Identity
            && !provider.Snapshot.ContainsKey(observed[8000].Identity), "同名新进程仍使用旧实例缓存。");
        reader.Stamp++;
        clock.Advance(TimeSpan.FromSeconds(31));
        first.Observe(refs);
        await WaitUntil(() => reader.Reads >= 2);
        Program.Assert(provider.Snapshot.Values.All(x => x.ApplicationName == "测试应用"), "文件变化后名称未重新发布。");
        first.Observe(Array.Empty<ProcessReference>());
        Program.Assert(provider.Snapshot.Count == 0, "空快照没有释放全部进程引用。");

        var failedReader = new FakeReader { Fail = true };
        using var failed = new WindowsProcessMetadataProvider(failedReader, clock);
        using var failedSession = failed.CreateSession();
        failedSession.Observe(refs);
        await WaitUntil(() => failed.Snapshot.Values.All(x => x.State == ProcessMetadataState.Unavailable));
        int reads = failedReader.Reads;
        failedSession.Observe(refs); await Task.Delay(30);
        Program.Assert(failedReader.Reads == reads, "失败结果未进行退避。");
        failedReader.Fail = false; clock.Advance(TimeSpan.FromSeconds(6)); failedSession.Observe(refs);
        await WaitUntil(() => failed.Snapshot.Values.All(x => x.State == ProcessMetadataState.Available));

        var overflowReader = new FakeReader { UniquePaths = true };
        using var overflow = new WindowsProcessMetadataProvider(overflowReader);
        using var overflowSession = overflow.CreateSession();
        overflowSession.Observe(Enumerable.Range(10000, 2100).Select(x => new ProcessReference(x, "fake.exe")).ToArray());
        await WaitUntil(() => overflow.Snapshot.Count == 2100 && overflow.Snapshot.Values.All(x => x.State == ProcessMetadataState.Available), 15000);
        Program.Assert(overflowReader.Reads == 2100, "有界队列溢出导致进程名称永久遗漏或重复读取。");

        using var readEntered = new ManualResetEventSlim();
        using var allowRead = new ManualResetEventSlim();
        var blockingReader = new FakeReader { ReadEntered = readEntered, ReadGate = allowRead };
        using var blocking = new WindowsProcessMetadataProvider(blockingReader);
        using var blockingSession = blocking.CreateSession();
        int notifications = 0;
        blocking.Changed += () => Interlocked.Increment(ref notifications);
        blockingSession.Observe(refs);
        await WaitUntil(() => readEntered.IsSet);
        blockingSession.Dispose(); blocking.Dispose(); allowRead.Set();
        await Task.Delay(60);
        Program.Assert(blocking.Snapshot.Count == 0 && notifications == 0, "退出后文件读取仍发布结果或保留进程引用。");

        using var observeEntered = new ManualResetEventSlim();
        using var allowObserve = new ManualResetEventSlim();
        using var observing = new WindowsProcessMetadataProvider(new FakeReader { ObserveEntered = observeEntered, ObserveGate = allowObserve });
        using var observingSession = observing.CreateSession();
        var observeTask = Task.Run(() => observingSession.Observe(refs));
        await WaitUntil(() => observeEntered.IsSet);
        observingSession.Dispose(); observing.Dispose(); allowObserve.Set();
        Program.Assert((await observeTask.WaitAsync(TimeSpan.FromSeconds(5))).Count == 0,
            "退出与原生身份观测并发时仍发布过期结果。");
    }

    internal static void RowsSurviveFilteringButNotExit()
    {
        var services = new PortKiller.Modules.Ports.PortToolServices(new NoSnapshotScanner(), new ProcessManagerService(),
            new ProcessDetailService(), new HttpSysDiagnosticService());
        var vm = new PortKiller.ViewModels.MainViewModel(services);
        try
        {
            var source = new[] { Row(18080, 6000, "first.exe"), Row(19090, 6001, "second.exe") };
            vm.ApplyScanResult(new ScanResult { Items = source }, true);
            var first = vm.FilteredItems[0]; first.IsChecked = true;
            vm.SearchText = "second"; vm.ApplyFilter();
            vm.SearchText = "first"; vm.ApplyFilter();
            Program.Assert(ReferenceEquals(first, vm.FilteredItems.Single()) && !first.IsChecked,
                "搜索往返未复用当前连接行，或恢复了被清除的隐藏选择。");
            vm.ApplyScanResult(new ScanResult { Items = Array.Empty<PortConnection>() }, true);
            vm.ApplyScanResult(new ScanResult { Items = source }, true);
            Program.Assert(!ReferenceEquals(first, vm.FilteredItems.Single()), "连接消失后仍保留历史显示行缓存。");
        }
        finally { vm.PrepareShutdown(); }
    }
    private sealed class NoSnapshotScanner : NetworkScannerService
    { public override Dictionary<int, ProcessSnapshotItem> GetProcessSnapshotMap() => new(); }

    internal static async Task LatestQueryWins()
    {
        using var queries = new PortConnectionQueryService();
        var rows = Enumerable.Range(10000, 10000).Select(x => Row(x, 5000, "test.exe")).ToArray();
        var completed = new TaskCompletionSource<PortQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.Completed += x => { if (x.Query.Search == "test") completed.TrySetResult(x); };
        for (int i = 0; i < 50; i++) queries.Enqueue(new(rows, PortsProcessMetadataAdapter.Empty, "old" + i, "全部分类", "全部状态", 0, true));
        queries.Enqueue(new(rows, PortsProcessMetadataAdapter.Empty, "test", "全部分类", "全部状态", 0, true));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Program.Assert(result.Version == queries.Version && result.Items.Count == 10000, "最新搜索未覆盖过时任务。");
    }

    internal static async Task WindowsIntegration()
    {
        using var provider = new WindowsProcessMetadataProvider();
        using var session = provider.CreateSession();
        var process = new ProcessReference(Environment.ProcessId, Path.GetFileName(Environment.ProcessPath!));
        var observations = session.Observe(new[] { process });
        Program.Assert(observations.TryGetValue(process.Pid, out var observation) && observation.Identity.IsVerified
            && observation.ProcessPath == Environment.ProcessPath, "真实 Win32 路径与创建时间查询失败。");
        await WaitUntil(() => provider.Snapshot.Values.All(x => x.State != ProcessMetadataState.Pending));
        Program.Assert(provider.Snapshot.TryGetValue(observation!.Identity, out var metadata)
            && metadata.State == ProcessMetadataState.Available && metadata.ApplicationName.Length > 0,
            "自有可执行文件的版本资源未提供应用名称。");
    }

    internal static async Task WaitUntil(Func<bool> predicate, int milliseconds = 5000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!predicate())
        {
            if (Environment.TickCount64 > deadline) throw new InvalidOperationException("等待应用名称结果超时。");
            await Task.Delay(10);
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class FakeReader : IProcessMetadataReader
    {
        public long Birth = 100, Stamp = 1;
        public int Reads;
        public bool Fail, UniquePaths;
        public ManualResetEventSlim? ReadEntered, ReadGate, ObserveEntered, ObserveGate;
        public ProcessObservation? Observe(ProcessReference process, ProcessObservation? previous)
        {
            ObserveEntered?.Set(); ObserveGate?.Wait(TimeSpan.FromSeconds(3));
            return new(new(process.Pid, Birth), process.ProcessName, UniquePaths ? $"C:\\fake\\{process.Pid}.exe" : @"C:\fake\fake.exe");
        }
        public ExecutableStamp GetStamp(string path) => new(100, Stamp);
        public ProcessMetadata Read(string path)
        {
            Interlocked.Increment(ref Reads);
            ReadEntered?.Set(); ReadGate?.Wait(TimeSpan.FromSeconds(3));
            if (Fail) throw new IOException("模拟暂时读取失败");
            return new("测试应用", "测试应用", "Test", ProcessMetadataState.Available);
        }
    }
}
