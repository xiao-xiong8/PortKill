using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Services;

/// <summary>处理行释放、一键释放和批量释放，保留失败明细并统一复检。</summary>
public sealed class PortReleaseService
{
    /// <summary>原生连接重置操作。</summary>
    private readonly PortKiller.Modules.Ports.Abstractions.ITcpConnectionResetter _processes;
    /// <summary>与刷新共用的扫描队列。</summary>
    private readonly ScanCoordinator _scans;

    /// <summary>保存操作与采集依赖。</summary>
    public PortReleaseService(PortKiller.Modules.Ports.Abstractions.ITcpConnectionResetter processes, ScanCoordinator scans)
    {
        _processes = processes;
        _scans = scans;
    }

    /// <summary>只允许清理 TIME_WAIT；IPv6、UDP 和活跃连接不进入重置调用。</summary>
    public static bool CanRelease(PortConnection item)
        => item.IsTimeWait && item.Protocol == "TCP" && item.IpVersion == "IPv4";

    /// <summary>完整连接身份，不用单独端口号判断某条连接是否存在。</summary>
    public static string ConnectionKey(PortConnection item)
        => $"{item.Protocol}|{item.IpVersion}|{item.LocalAddress}|{item.LocalPort}|{item.RemoteAddress}|{item.RemotePort}";

    /// <summary>先重新采集，再执行，最后复检；取消后不继续下发操作。</summary>
    public async Task<ReleaseReport> ReleaseAsync(IReadOnlyList<PortConnection> requested,
        CancellationToken cancellationToken)
    {
        var report = new ReleaseReport();
        var before = await _scans.ScanAsync(0, cancellationToken).ConfigureAwait(false);
        var current = before.Items.Where(CanRelease).Select(ConnectionKey).ToHashSet();
        var accepted = await Task.Run(() => ResetTargets(requested, current, report, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (accepted.Count == 0) return report;
        var after = await _scans.ScanAsync(0, cancellationToken).ConfigureAwait(false);
        var remaining = after.Items.Select(ConnectionKey).ToHashSet();
        foreach (var item in accepted)
        {
            if (!remaining.Contains(ConnectionKey(item))) report.Released++;
            else report.Failures.Add($"{item.LocalEndpoint} → {item.RemoteEndpoint}：系统接受请求，但复检连接仍存在。");
        }
        return report;
    }

    /// <summary>操作仅执行一次；过期条目和不支持的条目分别统计。</summary>
    private List<PortConnection> ResetTargets(IReadOnlyList<PortConnection> requested,
        HashSet<string> current, ReleaseReport report, CancellationToken cancellationToken)
    {
        var accepted = new List<PortConnection>();
        foreach (var item in requested.DistinctBy(ConnectionKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRelease(item)) { report.Unsupported++; continue; }
            if (!current.Contains(ConnectionKey(item))) { report.AlreadyGone++; continue; }
            var result = _processes.ResetTcpConnection(item);
            if (result.Success) accepted.Add(item);
            else report.Failures.Add($"{item.LocalEndpoint} → {item.RemoteEndpoint}：{result.Message}");
        }
        return accepted;
    }
}
