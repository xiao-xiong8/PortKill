using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PortKiller.Docker;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.Services;
using PortKiller.Features.Ports.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

internal static class DockerChecks
{
    private static readonly ContainerIdentity Identity = new("test-local-engine", new string('a', 64));
    private static HostEndpoint Endpoint(string address = "127.0.0.1", int port = 18080, PortTransport protocol = PortTransport.Tcp)
        => new(IPAddress.Parse(address), port, protocol);
    private static DockerContainer Container(string name = "any-service", HostEndpoint? host = null) => new(
        Identity, name, "arbitrary/image:tag", "sha256:test", null, null, "bridge", true, false,
        "2026-11-03T00:00:00Z", 0, "", new("always"), new[] { new PublishedBinding(host ?? Endpoint(), 80) });
    private static PortConnection ProxyRow(HostEndpoint? endpoint = null, int pid = 4321)
    {
        var row = DockerInventoryCoordinator.PublicationRow(endpoint ?? Endpoint());
        row.IsDockerPublication = false; row.Pid = pid; row.ProcessName = "com.docker.backend.exe";
        row.State = row.Protocol == "TCP" ? "LISTENING" : "UDP (无连接)";
        return row;
    }
    private static DockerInventory Inventory(params DockerContainer[] containers) => new(Identity.EngineId, DateTimeOffset.UtcNow, containers);
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }

    public static void EndpointIdentityAndOwnership()
    {
        foreach (string name in new[] { "java", "dotnet", "node", "python", "nginx", "postgres", "redis", "rabbitmq", "unrecognized" })
        {
            var owner = EndpointOwnershipResolver.Resolve(ProxyRow(), Inventory(Container(name)));
            Assert(owner.Kind == OwnershipKind.Docker && owner.Container?.Name == name, "服务名称影响了通用关联。");
        }
        var inventory = Inventory(Container());
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(Endpoint(protocol: PortTransport.Udp)), inventory).Container == null, "TCP 被关联到 UDP。");
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(Endpoint("192.0.2.1")), inventory).Container == null, "不同具体 IP 被关联。");
        Assert(!Endpoint("::1").Overlaps(Endpoint()), "IPv4 和 IPv6 混淆。");
        Assert(!Endpoint("fe80::1%3").Overlaps(Endpoint("fe80::1%4")), "IPv6 作用域丢失。");
        Assert(!Endpoint("::ffff:127.0.0.1").Overlaps(Endpoint()), "mapped IPv6 被擅自压平成 IPv4。");
        Assert(Endpoint("0.0.0.0").Overlaps(Endpoint()), "IPv4 通配覆盖未识别。");
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(Endpoint("0.0.0.0")), inventory).Kind == OwnershipKind.Ambiguous, "广域宿主监听冒充具体 Docker 映射。");
        var second = Container("other") with { Identity = new(Identity.EngineId, new string('b', 64)) };
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(), Inventory(Container(), second)).Kind == OwnershipKind.Ambiguous, "多个候选被自动选择。");
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(), inventory with { ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-10) }).Kind == OwnershipKind.Unknown, "过期快照仍可授权。");
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(), DockerInventory.Unavailable("denied")).Kind == OwnershipKind.Unknown, "查询失败伪装为原生。");
        Assert(EndpointOwnershipResolver.Resolve(ProxyRow(), Inventory(Container() with { NetworkMode = "host" })).Kind == OwnershipKind.Unsupported, "host 网络未经识别就允许停止。");
        var native = ProxyRow(); native.ProcessName = "native.exe";
        Assert(EndpointOwnershipResolver.Resolve(native, inventory).Kind == OwnershipKind.Ambiguous, "宿主原生持有者与 Engine 证据冲突未拦截。");
        native.State = "ESTABLISHED";
        Assert(EndpointOwnershipResolver.Resolve(native, inventory).Kind == OwnershipKind.Ambiguous, "原生存活连接被当作 Docker 服务。");
        Assert(SystemProtection.IsAbsolutelyProtected(100, "vmmemWSL") && SystemProtection.IsAbsolutelyProtected(101, "Docker Desktop.exe"), "承载进程未在执行边界保护。");
    }

    public static void PublicationAndQuery()
    {
        var container = Container() with { Bindings = new[] { new PublishedBinding(Endpoint(), 80), new PublishedBinding(Endpoint(port: 18081), 81) } };
        var host = ProxyRow();
        var enriched = DockerInventoryCoordinator.Enrich(new[] { host }, Inventory(container));
        Assert(enriched.Count == 3 && enriched.Count(r => r.IsDockerPublication) == 2, "发布记录与原生网络行未分离。");
        var plans = EndpointOwnershipResolver.CreatePlans(enriched.Where(r => r.IsDockerPublication).ToArray(), Inventory(container), new[] { host, ProxyRow(Endpoint(port: 18081)) });
        Assert(plans.Count == 1 && plans[0].Targets.Count == 2, "同容器的批量操作未去重。");
        var unrelated = Container("other", Endpoint(port: 18082)) with { Identity = new(Identity.EngineId, new string('b', 64)) };
        var shared = DockerInventoryCoordinator.Enrich(new[] { ProxyRow(), ProxyRow(Endpoint(port: 18082)) }, Inventory(container, unrelated));
        Assert(shared.Where(r => r.IsDockerPublication).Select(r => r.DockerOwnership?.Container?.Identity).Distinct().Count() == 2, "共享 PID 的容器被合并。");
        var native = new PortConnection { Protocol = "TCP", IpVersion = "IPv4", LocalAddress = "127.0.0.1", LocalPort = 7, RemotePort = 18080, Pid = 18080, ProcessName = "local.exe" };
        var items = new[] { native, enriched[1] };
        var query = new PortQuery(items, new Dictionary<ProcessIdentity, ProcessMetadata>(), "", "全部分类", "全部状态", 0, true,
            EndpointFilter: new("18080", "127.0.0.1", "TCP", "IPv4", "Docker 容器"));
        Assert(PortConnectionQueryService.Evaluate(query).Count == 1, "精确查询命中了远端端口/PID。");
        Assert(PortConnectionQueryService.Evaluate(query with { EndpointFilter = new("invalid", "", "全部协议", "全部地址族", "全部占用者") }).Count == 0, "无效精确端口回退到宽泛查询。");
        var row = PortConnectionItem.FromSnapshot(enriched[1]); row.IsChecked = true;
        var replacement = enriched[1]; replacement.DockerOwnership = new(OwnershipKind.Docker, "new", container with { Identity = unrelated.Identity });
        row.ApplySnapshot(replacement);
        Assert(!row.IsChecked && row.ApplicationName == "any-service" && row.ActionLabel == "停止容器", "容器重建后旧选择或动作未更新。");
    }

    public static void InspectParsingAndLocality()
    {
        var dto = new ContainerInspectDto
        {
            Id = Identity.Id, Name = "/redis", Config = new() { Image = "custom:tag" }, State = new() { Running = true },
            NetworkSettings = new() { Ports = new() { ["6379/tcp"] = new[] { new BindingDto { HostIp = "::1", HostPort = "12345" } }, ["80/tcp"] = null } }
        };
        var converted = DockerEngineClient.Convert(dto, Identity.EngineId);
        Assert(converted.Bindings.Count == 1 && converted.Bindings[0].Host == Endpoint("::1", 12345)
            && converted.ComposeProject == null, "实际映射、地址族或非 Compose 容器解析错误。");
        dto.NetworkSettings.Ports["6379/tcp"] = new[] { new BindingDto { HostIp = "", HostPort = "12345" } };
        Assert(DockerEngineClient.Convert(dto, Identity.EngineId).HasUnresolvedBindings, "空 HostIp 被猜测为双栈。");
        Assert(DockerEngineClient.ParseLocalPipe("npipe:////./pipe/docker_engine") == "docker_engine", "本机管道解析错误。");
        foreach (string remote in new[] { "tcp://localhost:2375", "ssh://host", "npipe:////remote/pipe/docker_engine" })
        { bool rejected = false; try { DockerEngineClient.ParseLocalPipe(remote); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "远程端点被当作本机 Engine。"); }
    }

    public static async Task StopAndForceConfirmation()
    {
        var fixture = new Fixture();
        var plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        var session = await fixture.Coordinator.BeginAsync(plan, false, default);
        var result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.Released && fixture.Engine.StopCalls == 1 && fixture.Engine.KillCalls == 0 && fixture.Engine.Current.RestartPolicy.Name == "always", "优雅停止或双重验证结果错误：" + result);

        fixture = new Fixture(); fixture.Engine.AutoExit = false;
        plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        session = await fixture.Coordinator.BeginAsync(plan, false, default);
        result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.State == ContainerStopState.AwaitingForceConfirmation && fixture.Engine.Current.Running && fixture.Engine.KillCalls == 0, "提醒时间触发了自动强杀。");
        bool rejected = false;
        try { await fixture.Coordinator.ForceAsync(session, false, default); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && fixture.Engine.KillCalls == 0, "未确认就发送强杀。");
        result = await fixture.Coordinator.ForceAsync(session, true, default);
        Assert(result.Released && fixture.Engine.KillCalls == 1 && fixture.Engine.StopCalls == 1, "确认强杀后目标或停止次数错误。");

        foreach (string signal in new[] { "9", "09", "+9", "0009", "KILL", "SIGKILL", "sigkill", "kill", "  +0009  " })
        {
            fixture = new Fixture(); fixture.Engine.Current = fixture.Engine.Current with { StopSignal = signal };
            plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
            session = await fixture.Coordinator.BeginAsync(plan, false, default);
            result = await fixture.Coordinator.ObserveAsync(session, default);
            Assert(result.State == ContainerStopState.AwaitingForceConfirmation && fixture.Engine.StopCalls == 0 && fixture.Engine.KillCalls == 0,
                "强制信号绕过二次确认：" + signal);
            result = await fixture.Coordinator.ForceAsync(session, true, default);
            Assert(result.Released && fixture.Engine.KillCalls == 1 && fixture.Engine.StopCalls == 0, "强制信号确认后未执行唯一目标：" + signal);
        }
        foreach (string signal in new[] { "15", "+15", "SIGTERM" })
        {
            fixture = new Fixture(); fixture.Engine.Current = fixture.Engine.Current with { StopSignal = signal };
            plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
            session = await fixture.Coordinator.BeginAsync(plan, false, default);
            result = await fixture.Coordinator.ObserveAsync(session, default);
            Assert(result.Released && fixture.Engine.StopCalls == 1 && fixture.Engine.KillCalls == 0, "普通信号被误判为强制信号：" + signal);
        }
    }

    public static void IndexedEnrichmentScaleAndAccuracy()
    {
        var rows = Enumerable.Range(0, 2000).Select(i =>
        { var row = ProxyRow(); row.State = "ESTABLISHED"; row.RemotePort = 20000 + i; return row; }).ToArray();
        var inventory = Inventory(Container(host: Endpoint("0.0.0.0")));
        DockerInventoryCoordinator.Enrich(new[] { rows[0] }, inventory);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var enriched = DockerInventoryCoordinator.Enrich(rows, inventory);
        double milliseconds = timer.Elapsed.TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(enriched.Count == 2001 && enriched.All(r => r.DockerOwnership is { Kind: OwnershipKind.Docker, Container.Identity: var id } && id == Identity),
            "大量同端口连接未统一匹配同一容器。");
        Assert(allocated < 32 * 1024 * 1024, $"2000 条连接分配过高：{allocated / 1024.0 / 1024:F1} MiB，可能退化为逐行全表关联。");
        Assert(rows.All(r => r.DockerOwnership == null) && !ReferenceEquals(rows[0], enriched[0]), "后台关联修改了原始 Windows 快照。");
        Console.WriteLine($"Docker 索引：2000 条同端口连接 {milliseconds:F1} ms，{allocated / 1024.0 / 1024:F2} MiB");

        var native = ProxyRow(); native.ProcessName = "native.exe";
        var noCandidates = DockerInventoryCoordinator.Enrich(new[] { ProxyRow(), native }, Inventory());
        Assert(noCandidates[0].DockerOwnership?.Kind == OwnershipKind.Unknown && noCandidates[1].DockerOwnership?.Kind == OwnershipKind.Native,
            "同端点缓存把无候选的原生进程与平台进程混为一类。");
        var conflict = ProxyRow(Endpoint("127.0.0.2")); conflict.ProcessName = "native.exe";
        var conflicts = DockerInventoryCoordinator.Enrich(new[] { ProxyRow(), conflict }, inventory);
        Assert(conflicts[0].DockerOwnership?.Kind == OwnershipKind.Docker && conflicts[1].DockerOwnership?.Kind == OwnershipKind.Ambiguous
            && conflicts[^1].DockerOwnership?.Kind == OwnershipKind.Ambiguous, "具体地址冲突或通配冲突索引不准确。");
        var scoped = Container(host: Endpoint("fe80::1%3"));
        var scopedRows = DockerInventoryCoordinator.Enrich(new[] { ProxyRow(Endpoint("fe80::1%3")), ProxyRow(Endpoint("fe80::1%4")),
            ProxyRow(Endpoint()), ProxyRow(Endpoint(protocol: PortTransport.Udp)), ProxyRow(Endpoint("::ffff:127.0.0.1")) }, Inventory(scoped));
        Assert(scopedRows[0].DockerOwnership?.Kind == OwnershipKind.Docker
            && scopedRows.Skip(1).Take(4).All(r => r.DockerOwnership?.Container == null), "索引丢失协议、IPv6 作用域或地址族边界。");
        var duplicate = Container() with { Bindings = new[] { new PublishedBinding(Endpoint(), 80), new PublishedBinding(Endpoint("0.0.0.0"), 80) } };
        Assert(DockerInventoryCoordinator.Enrich(new[] { ProxyRow() }, Inventory(duplicate))[0].DockerOwnership?.Kind == OwnershipKind.Docker,
            "同容器重叠映射被误判为多个容器。");
    }

    public static async Task BackgroundEnrichmentLatestWins()
    {
        using var coordinator = new DockerEnrichmentCoordinator();
        using var hosts = new BlockingHosts(ProxyRow());
        int caller = Environment.CurrentManagedThreadId, completions = 0;
        var completed = new TaskCompletionSource<DockerEnrichmentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Completed += result => { Interlocked.Increment(ref completions); completed.TrySetResult(result); };
        try
        {
            coordinator.Enqueue(hosts, Inventory(Container("old")));
            await hosts.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(hosts.EnumerationThread != caller, "Docker 关联仍在调用线程执行。");
            for (int i = 0; i < 20; i++) coordinator.Enqueue(new[] { ProxyRow() }, Inventory(Container("new-" + i)));
            hosts.Release.Set();
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(result.Version == coordinator.Version && result.Items.All(r => r.DockerOwnership?.Container?.Name == "new-19")
                && completions == 1, "过期后台关联覆盖了最新快照，或取消未生效。");
        }
        finally { hosts.Release.Set(); }
    }

    private sealed class BlockingHosts(PortConnection row) : IReadOnlyList<PortConnection>, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int EnumerationThread;
        public int Count => 1;
        public PortConnection this[int index] => index == 0 ? row : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<PortConnection> GetEnumerator()
        {
            EnumerationThread = Environment.CurrentManagedThreadId; Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("后台关联测试未解除阻塞。");
            yield return row;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Release.Dispose();
    }

    public static async Task RacesAndVerification()
    {
        var fixture = new Fixture();
        var plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        fixture.Engine.Current = fixture.Engine.Current with { StartedAt = "new-instance", RestartCount = 1 };
        bool rejected = false; try { await fixture.Coordinator.BeginAsync(plan, false, default); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && fixture.Engine.StopCalls == 0, "确认期间重启的实例被停止。");

        fixture = new Fixture { NativeTakesOver = true };
        plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        var session = await fixture.Coordinator.BeginAsync(plan, false, default);
        var result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.State == ContainerStopState.PortHeld && result.Detail.Contains("native.exe"), "原生进程接管仍报告释放成功。");

        fixture = new Fixture(); fixture.Engine.OnStop = () => fixture.Engine.Others = new[] { Container("replacement") with { Identity = new(Identity.EngineId, new string('b', 64)) } };
        plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        session = await fixture.Coordinator.BeginAsync(plan, false, default);
        result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.State == ContainerStopState.Reoccupied && fixture.Engine.StopCalls == 1, "未识别替代容器或自动追杀。");

        fixture = new Fixture { NoHostBinding = true };
        var publication = DockerInventoryCoordinator.PublicationRow(Endpoint());
        plan = (await fixture.Coordinator.PrepareAsync(new[] { publication }, default))[0];
        session = await fixture.Coordinator.BeginAsync(plan, false, default);
        result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.State == ContainerStopState.Unknown && result.Detail.Contains("WinNAT"), "无宿主原始绑定时凭绑定探测误报成功。");

        fixture = new Fixture();
        plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        session = await fixture.Coordinator.BeginAsync(plan, false, default); fixture.Engine.FailInventory = true;
        result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.State == ContainerStopState.Unknown, "查询故障被当作空表成功。");
    }

    public static async Task RestartPoliciesAndJournal()
    {
        var fixture = new Fixture();
        var plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        var session = await fixture.Coordinator.BeginAsync(plan, true, default);
        var result = await fixture.Coordinator.ObserveAsync(session, default);
        Assert(result.Released && fixture.Journal.Record?.OriginalName == "always" && fixture.Engine.Current.RestartPolicy.Name == "no", "策略未记录或停止后立即恢复。");
        await fixture.Coordinator.RestorePolicyAsync(fixture.Journal.Record!, default);
        Assert(fixture.Engine.Current.RestartPolicy.Name == "always" && fixture.Journal.Record == null, "策略恢复未验证或记录未清理。");

        fixture = new Fixture(); fixture.Journal.FailSave = true;
        plan = (await fixture.Coordinator.PrepareAsync(new[] { ProxyRow() }, default))[0];
        bool rejected = false; try { await fixture.Coordinator.BeginAsync(plan, true, default); } catch (IOException) { rejected = true; }
        Assert(rejected && fixture.Engine.PolicyCalls == 0 && fixture.Engine.StopCalls == 0, "记录失败后仍修改策略或停止。");

        string directory = Path.Combine(Path.GetTempPath(), "PortKiller-Docker-Journal", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new RestartPolicyJournal(directory);
            var record = new RestartPolicyChange(Identity.EngineId, Identity.Id, "service", "on-failure", 4, DateTimeOffset.UtcNow);
            await journal.SaveAsync(record, default); await journal.SaveAsync(record with { ChangedAt = DateTimeOffset.UtcNow }, default);
            rejected = false;
            try { await journal.SaveAsync(record with { OriginalName = "unless-stopped" }, default); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "不同原策略静默复用了过期恢复记录。");
            var records = await journal.ReadAsync(default);
            Assert(records.Count == 1 && records[0].OriginalName == "on-failure" && records[0].OriginalMaximumRetryCount == 4, "恢复记录未原子保存或重试覆盖原策略。");
            await journal.RemoveAsync(record, default);
            Assert((await journal.ReadAsync(default)).Count == 0, "恢复记录未移除。");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public static async Task NamedPipeApiContract()
    {
        string pipe = "portkill-regression-" + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = new List<string>();
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        bool running = true;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await server.WaitForConnectionAsync(lifetime.Token);
                    using var reader = new StreamReader(server, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string request = await reader.ReadLineAsync(lifetime.Token) ?? "";
                    string? header; do { header = await reader.ReadLineAsync(lifetime.Token); } while (!string.IsNullOrEmpty(header));
                    requests.Add(request);
                    string path = request.Split(' ')[1];
                    string body = path switch
                    {
                        "/version" => "{\"ApiVersion\":\"1.52\",\"MinAPIVersion\":\"1.41\"}",
                        "/v1.51/info" => "{\"ID\":\"test-local-engine\"}",
                        "/v1.51/containers/json?all=false" => "[{\"Id\":\"" + Identity.Id + "\"}]",
                        _ => "{\"Id\":\"" + Identity.Id + "\",\"Name\":\"/test\",\"Config\":{\"Image\":\"arbitrary\"},\"State\":{\"Running\":" + (running ? "true" : "false") + "},\"NetworkSettings\":{\"Ports\":{\"80/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"18080\"}]}}}"
                    };
                    bool stop = path.EndsWith("/stop?t=-1", StringComparison.Ordinal);
                    if (stop) { running = false; body = ""; }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(stop ? "204 No Content" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await server.WriteAsync(head, lifetime.Token); await server.WriteAsync(bytes, lifetime.Token); await server.FlushAsync(lifetime.Token);
                    server.Disconnect();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        string? previousHost = Environment.GetEnvironmentVariable("DOCKER_HOST"), previousContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", null);
            Environment.SetEnvironmentVariable("DOCKER_HOST", "npipe:////./pipe/" + pipe);
            using var client = new DockerEngineClient();
            var inventory = await client.ReadInventoryAsync(lifetime.Token);
            Assert(inventory.Error == null && inventory.Containers.Count == 1 && inventory.Containers[0].Bindings[0].Host.Port == 18080, "Named Pipe/API JSON 链路未通过。");
            await client.StopGracefullyAsync(Identity, lifetime.Token);
            var after = await client.InspectAsync(Identity, lifetime.Token);
            Assert(after?.Running == false && requests.Any(r => r.Contains("/stop?t=-1", StringComparison.Ordinal))
                && !requests.Any(r => r.Contains("/kill", StringComparison.Ordinal)), "API 使用了有限停止超时或未经确认调用 kill。");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", previousHost); Environment.SetEnvironmentVariable("DOCKER_CONTEXT", previousContext);
            lifetime.Cancel(); await serve;
        }
    }

    public static async Task WindowAndIndependentMetadata()
    {
        var engine = new FakeEngine();
        using var services = new PortKiller.Modules.Ports.PortToolServices(new NetworkScannerService(), new ProcessManagerService(),
            new ProcessDetailService(), new HttpSysDiagnosticService(), dockerEngine: engine, dockerPolicies: new MemoryJournal());
        var vm = new MainViewModel(services);
        vm.EnableBackgroundConnectionQueries();
        var window = new MainWindow { Width = 1180, Height = 700, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
        try
        {
            window.Show(); window.DataContext = vm;
            vm.ApplyScanResult(new ScanResult { Items = new[] { ProxyRow() }, ListeningCount = 1 }, true);
            services.DockerInventory.Start();
            await Until(() => vm.FilteredItems.Any(r => r.IsDockerPublication));
            Assert(vm.TotalCount == 1 && vm.FilteredItems.Count == 2, "发布记录污染宿主计数或未展示。");
            var row = vm.FilteredItems.Single(r => r.IsDockerPublication); row.IsChecked = true;
            vm.FocusRow(row);
            Assert(vm.InspName == engine.Current.Name && vm.InspExePath == "—" && row.PidDisplay == "—"
                && row.ContextKillLabel.Contains("容器") && row.CopySummary.Contains(Identity.Id), "容器详情、菜单或复制仍冒用 Windows PID。");
            engine.Current = engine.Current with { Name = "renamed-service", Identity = new(Identity.EngineId, new string('c', 64)) };
            await Until(() => row.ApplicationName == "renamed-service");
            Assert(!row.IsChecked, "网络端点未变化时，旧容器选择未失效。");
            Assert(vm.InspName == "renamed-service" && vm.InspPorts.Count == 1, "独立元数据刷新未更新容器详情。");
            vm.ExactFamily = "IPv6";
            await Until(() => vm.FilteredItems.Count == 0);
            vm.ClearExactFiltersCommand.Execute(null);
            await Until(() => vm.FilteredItems.Count == 2);
            var oldRows = Enumerable.Range(0, 5000).Select(i => { var host = ProxyRow(); host.RemotePort = i + 20000; return host; }).ToArray();
            vm.ApplyScanResult(new ScanResult { Items = oldRows }, true);
            vm.ExactFamily = "IPv6";
            vm.ApplyScanResult(new ScanResult { Items = new[] { ProxyRow() } }, true);
            await Until(() => vm.FilteredItems.Count == 0);
            vm.ClearExactFiltersCommand.Execute(null);
            await Until(() => vm.FilteredItems.Count == 2 && vm.FilteredItems.All(r => r.RemotePort == 0));
            vm.DockerDialogTitle = "停止 Docker 容器";
            vm.DockerConfirmDetails = string.Join("\n", Enumerable.Repeat(engine.Current.Description, 8));
            vm.IsDockerConfirmVisible = true;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            var card = window.FindControl<ContentControl>("DockerCard")!;
            var body = window.FindControl<ScrollViewer>("DockerBody")!;
            var confirm = window.FindControl<Button>("DockerConfirmButton")!;
            Assert(card.IsEffectivelyVisible && confirm.IsEffectivelyVisible && body.Extent.Height > body.Viewport.Height,
                "容器确认面板未显示或长影响范围不能滚动。");
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
            Assert(engine.StopCalls == 0 && engine.KillCalls == 0, "确认面板期间快捷键穿透并执行停止。");
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert(!vm.IsDockerConfirmVisible && engine.StopCalls == 0, "Esc 未安全关闭容器确认。");
        }
        finally { window.PrepareClose(); window.Close(); vm.PrepareShutdown(); }
        Console.WriteLine("PASS Docker 窗口：独立元数据刷新、容器重建选择失效、精确过滤、长确认与快捷键隔离");
    }

    public static async Task BatchReportsPreserveProgress()
    {
        foreach (bool pendingSecond in new[] { false, true })
        {
            var fixture = new Fixture();
            fixture.Engine.Current = fixture.Engine.Current with { Name = "first-completed" };
            var second = Container("second-target", Endpoint(port: 18081)) with { Identity = new(Identity.EngineId, new string('b', 64)), StopSignal = "09" };
            var third = Container("third-unexecuted", Endpoint(port: 18082)) with { Identity = new(Identity.EngineId, new string('c', 64)) };
            fixture.Engine.Others = new[] { second, third };
            using var services = new PortKiller.Modules.Ports.PortToolServices(new NetworkScannerService(), new ProcessManagerService(),
                new ProcessDetailService(), new HttpSysDiagnosticService(), dockerEngine: fixture.Engine, dockerPolicies: fixture.Journal, containerStops: fixture.Coordinator);
            var vm = new MainViewModel(services);
            try
            {
                services.DockerInventory.Start();
                vm.ApplyScanResult(new ScanResult { Items = new[] { ProxyRow(), new PortConnection { Protocol = "TCP", IpVersion = "IPv4",
                    LocalAddress = "127.0.0.1", LocalPort = 18083, ProcessName = "native-unexecuted.exe", Pid = 9876, State = "LISTENING" } } }, true);
                foreach (var row in vm.FilteredItems) row.IsChecked = row.IsDockerPublication || row.ProcessName == "native-unexecuted.exe";
                await vm.KillCheckedAsync();
                Assert(vm.IsDockerConfirmVisible, "批量容器确认未建立。");
                // 确认后第一个目标完成；第二个在操作前消失，或进入单独强杀确认。
                fixture.Engine.OnStop = () => fixture.Engine.Others = pendingSecond ? new[] { second, third } : new[] { third };
                await vm.ConfirmDockerStopCommand.ExecuteAsync(null);
                string report = (string)typeof(MainViewModel).GetField("_lastOperationReport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                Assert(report.Contains("first-completed") && report.Contains("[Released]") && report.Contains(second.Identity.Id)
                    && report.Contains(third.Identity.Id) && report.Contains("[NotExecuted]") && report.Contains("native-unexecuted.exe")
                    && fixture.Engine.StopCalls == 1 && fixture.Engine.KillCalls == 0 && third.Running,
                    "批次中断/待确认时丢失已完成、当前或未执行项：" + report);
                if (pendingSecond)
                {
                    Assert(report.Contains("[AwaitingForceConfirmation]") && vm.HasPendingDockerStop && vm.DockerCanForce,
                        "后续等待强杀确认时未保留累计报告或观察入口。");
                    vm.CloseDockerDialogCommand.Execute(null);
                    await vm.ContinueDockerStopCommand.ExecuteAsync(null);
                    Assert(vm.IsDockerConfirmVisible && fixture.Engine.StopCalls == 1 && fixture.Engine.KillCalls == 0,
                        "关闭/继续观察重复发送停止或自动强杀。");
                }
                else
                {
                    Assert(report.Contains("[Failed]") && !vm.IsDockerConfirmVisible && !vm.HasPendingDockerStop,
                        "失效目标未终结本批次。");
                    await vm.ConfirmDockerStopCommand.ExecuteAsync(null);
                    Assert(!vm.IsDockerConfirmVisible && fixture.Engine.StopCalls == 1, "再次确认仍重试失效旧目标。");
                }
            }
            finally { vm.PrepareShutdown(); }
        }
        Console.WriteLine("PASS Docker 批次：已完成、失败/待确认及未执行报告保留，失效队列终结，继续观察不重复停止");
    }

    public static async Task ConnectionRecoveryPreservesStop()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = new PipeEngine("first-engine", holdStop: true);
        await using var second = new PipeEngine("second-engine");
        string? previousHost = Environment.GetEnvironmentVariable("DOCKER_HOST"), previousContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", null);
            Environment.SetEnvironmentVariable("DOCKER_HOST", first.Endpoint);
            using var client = new DockerEngineClient();
            Assert((await client.ReadInventoryAsync(lifetime.Token)).EngineId == "first-engine", "未连接第一个测试 Engine。");
            var stopping = client.StopGracefullyAsync(new("first-engine", Identity.Id), lifetime.Token);
            await first.StopStarted.Task.WaitAsync(lifetime.Token);
            Environment.SetEnvironmentVariable("DOCKER_HOST", second.Endpoint);
            Assert((await client.ReadInventoryAsync(lifetime.Token)).EngineId == "second-engine", "端点切换后仍使用旧管道。");
            Assert(!stopping.IsCompleted, "切换连接中断了正在等待的优雅停止请求。");
            bool rejected = false;
            try { await client.KillAsync(new("first-engine", Identity.Id), lifetime.Token); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && second.KillCount == 0, "旧 Engine 目标被发送给新 Engine。");
            first.ReleaseStop.TrySetResult();
            await stopping;
            second.InvalidNextInfo = 1;
            Assert((await client.ReadInventoryAsync(lifetime.Token)).Error != null, "损坏快照被当作正常空表或隐式重试。");
            Assert((await client.ReadInventoryAsync(lifetime.Token)).EngineId == "second-engine" && second.VersionCount >= 2,
                "查询失败后没有重新发现和协商连接。");
        }
        finally
        {
            first.ReleaseStop.TrySetResult();
            Environment.SetEnvironmentVariable("DOCKER_HOST", previousHost); Environment.SetEnvironmentVariable("DOCKER_CONTEXT", previousContext);
        }
    }

    public static async Task WindowsEndpointProbes()
    {
        var probe = new WindowsEndpointAvailabilityProbe();
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            foreach (var protocol in new[] { PortTransport.Tcp, PortTransport.Udp })
            {
                var socket = new Socket(address.AddressFamily, protocol == PortTransport.Tcp ? SocketType.Stream : SocketType.Dgram,
                    protocol == PortTransport.Tcp ? ProtocolType.Tcp : ProtocolType.Udp) { ExclusiveAddressUse = true };
                HostEndpoint endpoint;
                using (socket)
                {
                    if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
                    socket.Bind(new IPEndPoint(address, 0));
                    if (protocol == PortTransport.Tcp) socket.Listen(1);
                    endpoint = new(address, ((IPEndPoint)socket.LocalEndPoint!).Port, protocol);
                    Assert((await probe.ProbeAsync(endpoint, default)).Available == false, "独占探测未识别自有占用端点：" + endpoint);
                }
                Assert((await probe.ProbeAsync(endpoint, default)).Available == true, "独占探测未确认自有端点关闭：" + endpoint);
            }
    }

    private sealed class PipeEngine : IAsyncDisposable
    {
        private readonly string _name = "portkill-recovery-" + Guid.NewGuid().ToString("N");
        private readonly string _engine;
        private readonly bool _holdStop;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<Task> _requests = new();
        private readonly Task _accept;
        public string Endpoint => "npipe:////./pipe/" + _name;
        public int VersionCount, KillCount, InvalidNextInfo;
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PipeEngine(string engine, bool holdStop = false)
        { _engine = engine; _holdStop = holdStop; _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    try { await server.WaitForConnectionAsync(_lifetime.Token); }
                    catch { server.Dispose(); throw; }
                    _requests.Add(RespondAsync(server));
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        private async Task RespondAsync(NamedPipeServerStream server)
        {
            using (server)
            {
                try
                {
                    using var reader = new StreamReader(server, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string request = await reader.ReadLineAsync(_lifetime.Token) ?? "";
                    string? header; do { header = await reader.ReadLineAsync(_lifetime.Token); } while (!string.IsNullOrEmpty(header));
                    string path = request.Split(' ')[1]; string body = "[]"; bool stop = path.EndsWith("/stop?t=-1", StringComparison.Ordinal);
                    if (path == "/version") { Interlocked.Increment(ref VersionCount); body = "{\"ApiVersion\":\"1.51\",\"MinAPIVersion\":\"1.41\"}"; }
                    else if (path.EndsWith("/info", StringComparison.Ordinal)) body = Interlocked.Exchange(ref InvalidNextInfo, 0) == 1 ? "invalid-json" : "{\"ID\":\"" + _engine + "\"}";
                    else if (stop) { StopStarted.TrySetResult(); if (_holdStop) await ReleaseStop.Task.WaitAsync(_lifetime.Token); body = ""; }
                    else if (path.EndsWith("/kill", StringComparison.Ordinal)) Interlocked.Increment(ref KillCount);
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    await server.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(stop ? "204 No Content" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                    await server.WriteAsync(bytes, _lifetime.Token); await server.FlushAsync(_lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); ReleaseStop.TrySetResult(); await _accept; await Task.WhenAll(_requests); _lifetime.Dispose();
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Docker UI 状态未更新。"); await Task.Delay(20); }
    }

    private sealed class Fixture
    {
        public FakeEngine Engine { get; } = new();
        public MemoryJournal Journal { get; } = new();
        public bool NativeTakesOver, NoHostBinding;
        public ContainerStopCoordinator Coordinator { get; }
        public Fixture()
        {
            Coordinator = new(Engine, _ => Task.FromResult(new ScanResult
            {
                Items = Engine.Current.Running && !NoHostBinding ? new[] { ProxyRow() }
                    : !Engine.Current.Running && NativeTakesOver ? new[] { new PortConnection { Protocol = "TCP", IpVersion = "IPv4", LocalAddress = "127.0.0.1", LocalPort = 18080, ProcessName = "native.exe", Pid = 9999, State = "LISTENING" } }
                    : Array.Empty<PortConnection>()
            }), new FreeProbe(), Journal)
            { GraceReminder = TimeSpan.FromMilliseconds(1), PollInterval = TimeSpan.FromMilliseconds(1), VerificationWindow = TimeSpan.FromSeconds(1) };
        }
    }
    private sealed class FreeProbe : IEndpointAvailabilityProbe
    { public Task<(bool? Available, string Evidence)> ProbeAsync(HostEndpoint endpoint, CancellationToken token) => Task.FromResult<(bool?, string)>((true, "free")); }
    private sealed class MemoryJournal : IRestartPolicyJournal
    {
        public RestartPolicyChange? Record; public bool FailSave;
        public Task SaveAsync(RestartPolicyChange change, CancellationToken token) { if (FailSave) throw new IOException("test-write-failure"); Record = change; return Task.CompletedTask; }
        public Task<IReadOnlyList<RestartPolicyChange>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<RestartPolicyChange>>(Record == null ? Array.Empty<RestartPolicyChange>() : new[] { Record });
        public Task RemoveAsync(RestartPolicyChange change, CancellationToken token) { Record = null; return Task.CompletedTask; }
    }
    private sealed class FakeEngine : IDockerEngineClient
    {
        public DockerContainer Current = Container(); public DockerContainer[] Others = Array.Empty<DockerContainer>();
        public bool AutoExit = true, FailInventory; public int StopCalls, KillCalls, PolicyCalls; public Action? OnStop;
        public Task<DockerInventory> ReadInventoryAsync(CancellationToken token) => Task.FromResult(FailInventory ? DockerInventory.Unavailable("test-unavailable")
            : Inventory((Current.Running ? new[] { Current } : Array.Empty<DockerContainer>()).Concat(Others).ToArray()));
        public Task<DockerContainer?> InspectAsync(ContainerIdentity identity, CancellationToken token)
            => Task.FromResult<DockerContainer?>(Current.Identity == identity ? Current : Others.FirstOrDefault(c => c.Identity == identity));
        public Task StopGracefullyAsync(ContainerIdentity identity, CancellationToken token)
        {
            Assert(identity == Current.Identity, "停止了无关容器。"); StopCalls++;
            if (!AutoExit) return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            Current = Current with { Running = false }; OnStop?.Invoke(); return Task.CompletedTask;
        }
        public Task KillAsync(ContainerIdentity identity, CancellationToken token)
        { Assert(identity == Current.Identity, "强杀了无关容器。"); KillCalls++; Current = Current with { Running = false }; return Task.CompletedTask; }
        public Task UpdateRestartPolicyAsync(ContainerIdentity identity, RestartPolicy policy, CancellationToken token)
        { Assert(identity == Current.Identity, "修改了无关容器策略。"); PolicyCalls++; Current = Current with { RestartPolicy = policy }; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
