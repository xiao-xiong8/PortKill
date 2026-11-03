using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PortKiller.Models;
using PortKiller.Services;

namespace PortKiller.Docker;

public interface IEndpointAvailabilityProbe
{
    Task<(bool? Available, string Evidence)> ProbeAsync(HostEndpoint endpoint, CancellationToken token);
}

public sealed class ContainerStopSession
{
    internal ContainerStopSession(ContainerStopPlan plan) => Plan = plan;
    public ContainerStopPlan Plan { get; }
    internal Task? StopTask { get; set; }
    internal bool ForceSent { get; set; }
    public bool DisabledRestart { get; internal set; }
    internal readonly SemaphoreSlim Gate = new(1, 1);
}

public sealed class ContainerStopCoordinator
{
    private readonly IDockerEngineClient _engine;
    private readonly Func<CancellationToken, Task<ScanResult>> _scan;
    private readonly IEndpointAvailabilityProbe _probe;
    private readonly IRestartPolicyJournal _journal;
    private readonly ConcurrentDictionary<ContainerIdentity, ContainerStopSession> _active = new();
    public TimeSpan GraceReminder { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan VerificationWindow { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public ContainerStopCoordinator(IDockerEngineClient engine, Func<CancellationToken, Task<ScanResult>> scan,
        IEndpointAvailabilityProbe probe, IRestartPolicyJournal journal)
    { _engine = engine; _scan = scan; _probe = probe; _journal = journal; }

    public async Task<IReadOnlyList<ContainerStopPlan>> PrepareAsync(IReadOnlyList<PortConnection> rows, CancellationToken token)
    {
        var inventory = await _engine.ReadInventoryAsync(token).ConfigureAwait(false);
        var hosts = await _scan(token).ConfigureAwait(false);
        var plans = EndpointOwnershipResolver.CreatePlans(rows, inventory, hosts.Items);
        foreach (var row in rows)
            if (row.DockerOwnership?.Container is { } expected && !plans.Any(p => p.Container.SameInstance(expected)))
                throw new InvalidOperationException("选中记录的容器实例或映射已变化，请刷新后重新选择。");
        return plans;
    }

    public async Task<ContainerStopSession> BeginAsync(ContainerStopPlan plan, bool disableRestart, CancellationToken token)
    {
        var session = new ContainerStopSession(plan);
        if (!_active.TryAdd(plan.Container.Identity, session)) throw new InvalidOperationException("该容器已有停止操作，请继续观察现有操作。");
        try
        {
            await ValidateAsync(plan, token).ConfigureAwait(false);
            if (disableRestart && plan.Container.RestartPolicy.Name is not ("no" or ""))
            {
                var change = new RestartPolicyChange(plan.Container.Identity.EngineId, plan.Container.Identity.Id,
                    plan.Container.Name, plan.Container.RestartPolicy.Name, plan.Container.RestartPolicy.MaximumRetryCount, DateTimeOffset.UtcNow);
                await _journal.SaveAsync(change, token).ConfigureAwait(false);
                await _engine.UpdateRestartPolicyAsync(plan.Container.Identity, new("no"), token).ConfigureAwait(false);
                var updated = await _engine.InspectAsync(plan.Container.Identity, token).ConfigureAwait(false);
                if (updated == null || !updated.Running || updated.Restarting || updated.RestartPolicy.Name != "no" || !plan.Container.SameInstance(updated))
                    throw new InvalidOperationException("禁用重启策略后状态未确认，尚未发送停止请求。策略恢复记录已保留。");
                session.DisabledRestart = true;
            }
            string signal = plan.Container.StopSignal.Trim();
            if (!IsForceSignal(signal))
            {
                // 任务可以跨越提醒窗口；不以客户端计时器触发 kill，也不重复提交 stop。
                session.StopTask = _engine.StopGracefullyAsync(plan.Container.Identity, token);
                _ = session.StopTask.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            OperationLog.Record("DOCKER_STOP", $"engine={plan.Container.Identity.EngineId} id={plan.Container.Identity.Id} disableRestart={disableRestart}");
            return session;
        }
        catch { _active.TryRemove(plan.Container.Identity, out _); throw; }
    }

    public async Task<ContainerStopResult> ObserveAsync(ContainerStopSession session, CancellationToken token)
    {
        await session.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (session.StopTask == null && !session.ForceSent)
                return new(ContainerStopState.AwaitingForceConfirmation, "容器配置的停止信号具有强制终止语义，尚未发送停止请求。必须单独确认强制终止。");
            if (session.StopTask is { IsCompleted: false } stop)
                await Task.WhenAny(stop, Task.Delay(GraceReminder, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var current = await _engine.InspectAsync(session.Plan.Container.Identity, token).ConfigureAwait(false);
            if (current is { Running: true } or { Restarting: true })
            {
                if (!session.Plan.Container.SameInstance(current)) return Finish(session, new(ContainerStopState.Reoccupied, "容器运行实例或映射已变化，禁止继续操作旧目标。"));
                if (session.ForceSent) return new(ContainerStopState.Unknown, "已发送强制请求，但容器退出尚未确认。");
                if (session.StopTask?.IsFaulted == true || session.StopTask?.IsCanceled == true)
                    return new(ContainerStopState.AwaitingForceConfirmation, "停止请求中断，但已重新确认同一容器实例仍在运行；请求可能已经到达 Engine。可继续观察，或单独确认强制终止；尚未自动强杀。");
                return new(ContainerStopState.AwaitingForceConfirmation, "优雅停止等待时间已到，容器仍在运行；尚未强制终止。可继续等待，或单独确认强制终止。");
            }
            return Finish(session, await VerifyAsync(session.Plan, token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { return new(ContainerStopState.Unknown, "无法确认停止结果：" + e.Message); }
        finally { session.Gate.Release(); }
    }

    public async Task<ContainerStopResult> ForceAsync(ContainerStopSession session, bool userConfirmed, CancellationToken token)
    {
        if (!userConfirmed) throw new InvalidOperationException("未确认强制终止，未发送请求。");
        await session.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await _engine.InspectAsync(session.Plan.Container.Identity, token).ConfigureAwait(false);
            if (current is { Running: true } or { Restarting: true })
            {
                await ValidateAsync(session.Plan, token, session.DisabledRestart).ConfigureAwait(false);
                await _engine.KillAsync(session.Plan.Container.Identity, token).ConfigureAwait(false);
                session.ForceSent = true;
                OperationLog.Record("DOCKER_KILL", $"engine={current.Identity.EngineId} id={current.Identity.Id} confirmed=true");
            }
            return Finish(session, await VerifyAsync(session.Plan, token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { return new(ContainerStopState.Unknown, "强制请求或验证未完成：" + e.Message); }
        finally { session.Gate.Release(); }
    }

    public async Task RestorePolicyAsync(RestartPolicyChange change, CancellationToken token)
    {
        var identity = new ContainerIdentity(change.EngineId, change.ContainerId);
        if (_active.ContainsKey(identity)) throw new InvalidOperationException("该容器仍有待核验停止操作，暂不能恢复策略。");
        var current = await _engine.InspectAsync(identity, token).ConfigureAwait(false);
        if (current == null || current.RestartPolicy.Name != "no")
            throw new InvalidOperationException("容器已消失或策略已被其他操作修改，未覆盖当前策略。");
        await _engine.UpdateRestartPolicyAsync(identity, new(change.OriginalName, change.OriginalMaximumRetryCount), token).ConfigureAwait(false);
        var after = await _engine.InspectAsync(identity, token).ConfigureAwait(false);
        if (after?.RestartPolicy != new RestartPolicy(change.OriginalName, change.OriginalMaximumRetryCount))
            throw new InvalidOperationException("重启策略恢复结果尚未确认，记录已保留。");
        await _journal.RemoveAsync(change, token).ConfigureAwait(false);
    }

    private async Task ValidateAsync(ContainerStopPlan plan, CancellationToken token, bool disabledRestart = false)
    {
        var current = await _engine.InspectAsync(plan.Container.Identity, token).ConfigureAwait(false);
        if (current == null || !current.Running || current.Restarting || !plan.Container.SameInstance(current))
            throw new InvalidOperationException("容器已停止、重启、重建或映射变化，请重新确认。");
        if (current.RestartPolicy != (disabledRestart ? new RestartPolicy("no") : plan.Container.RestartPolicy))
            throw new InvalidOperationException("重启策略已被其他操作改变，请重新确认，未覆盖当前策略。");
        var inventory = await _engine.ReadInventoryAsync(token).ConfigureAwait(false);
        var hosts = await _scan(token).ConfigureAwait(false);
        var index = EndpointOwnershipResolver.CreateIndex(inventory, hosts.Items, token);
        foreach (var target in plan.Targets)
        {
            var row = DockerInventoryCoordinator.PublicationRow(target);
            var owner = index.Resolve(row);
            if (owner.Kind != OwnershipKind.Docker || owner.Container == null || !plan.Container.SameInstance(owner.Container)
                || owner.Container.RestartPolicy != current.RestartPolicy)
                throw new InvalidOperationException("操作前端点归属未通过核验：" + owner.Evidence);
        }
    }
    private async Task<ContainerStopResult> VerifyAsync(ContainerStopPlan plan, CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); int stable = 0;
        string reason = "释放状态尚未确认。"; ContainerStopState state = ContainerStopState.Unknown;
        do
        {
            token.ThrowIfCancellationRequested();
            var container = await _engine.InspectAsync(plan.Container.Identity, token).ConfigureAwait(false);
            var inventory = await _engine.ReadInventoryAsync(token).ConfigureAwait(false);
            if (!inventory.IsFresh(DateTimeOffset.UtcNow) || inventory.EngineId != plan.Container.Identity.EngineId)
                return new(ContainerStopState.Unknown, inventory.Error ?? "Engine 身份变化或快照失效。");
            if (container is { Running: true } or { Restarting: true })
            {
                if (!plan.Container.SameInstance(container)) return new(ContainerStopState.Reoccupied, "原容器已重启或映射变化；未确认释放。");
                stable = 0; state = ContainerStopState.Unknown; reason = "请求已发送，但原容器退出尚未确认。";
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                continue;
            }
            var holders = inventory.Containers.Where(c => c.Running || c.Restarting)
                .Where(c => c.Bindings.Any(b => plan.Targets.Any(t => t.Overlaps(b.Host)))).ToArray();
            if (holders.Length > 0) return new(ContainerStopState.Reoccupied,
                "端点已被容器发布：" + string.Join("、", holders.Select(c => $"{c.Name} ({c.ShortId})")));
            var hosts = await _scan(token).ConfigureAwait(false);
            var remaining = hosts.Items.FirstOrDefault(row => !row.IsTimeWait && (row.IsListening || row.IsEstablished || row.Protocol == "UDP")
                && plan.Targets.Any(t => t.Overlaps(HostEndpoint.From(row))));
            bool clear = remaining == null;
            state = clear ? ContainerStopState.Unknown : ContainerStopState.PortHeld;
            reason = remaining == null ? "宿主端口仍需验证。" : $"容器已停止，{remaining.LocalEndpoint} 仍由 {remaining.ProcessName} (PID {remaining.Pid}) 持有。";
            if (clear)
                foreach (var target in plan.Targets)
                {
                    var probe = await _probe.ProbeAsync(target, token).ConfigureAwait(false);
                    if (probe.Available != true) { clear = false; state = probe.Available == false ? ContainerStopState.PortHeld : ContainerStopState.Unknown; reason = probe.Evidence; break; }
                }
            if (clear && !plan.HadHostBinding)
                return new(ContainerStopState.Unknown, "容器已停止、Engine 发布已消失且绑定探测通过，但原发布未体现为 Windows 监听行；缺少 HNS/WinNAT 转发消失证据，不能确认完全释放。请继续通过网络诊断核查转发规则。", Pending: false);
            stable = clear ? stable + 1 : 0;
            if (stable >= 3) return new(ContainerStopState.Released, "容器已停止，Engine 发布已消失，Windows 端点及独占绑定连续三次验证通过。");
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        } while (clock.Elapsed < VerificationWindow);
        return new(state, reason);
    }
    private ContainerStopResult Finish(ContainerStopSession session, ContainerStopResult result)
    {
        if (!result.Pending || result.State is ContainerStopState.Released or ContainerStopState.PortHeld or ContainerStopState.Reoccupied)
            _active.TryRemove(session.Plan.Container.Identity, out _);
        OperationLog.Record("DOCKER_VERIFY", $"id={session.Plan.Container.Identity.Id} result={result.State} {result.Detail}");
        return result;
    }
    internal static bool IsForceSignal(string signal)
        => signal.Equals("SIGKILL", StringComparison.OrdinalIgnoreCase) || signal.Equals("KILL", StringComparison.OrdinalIgnoreCase)
            // Docker 以十进制整数解析信号；09、+9 与 9 均为 SIGKILL。
            || int.TryParse(signal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) && number == 9;
}
