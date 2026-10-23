using System.Threading;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Services;

/// <summary>统一串行采集：刷新、释放复检共用入口，取消时不发布过期快照。</summary>
public sealed class ScanCoordinator
{
    /// <summary>原生扫描器；只在后台线程调用。</summary>
    private readonly PortKiller.Modules.Ports.Abstractions.IPortScanner _scanner;
    /// <summary>同一时刻只允许一份原生扫描在执行。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _completedScans;
    private double _lastScanMilliseconds;
    internal long CompletedScans => Interlocked.Read(ref _completedScans);
    internal double LastScanMilliseconds => Volatile.Read(ref _lastScanMilliseconds);

    /// <summary>保存扫描依赖，不在构造期间访问系统。</summary>
    public ScanCoordinator(PortKiller.Modules.Ports.Abstractions.IPortScanner scanner) => _scanner = scanner;

    /// <summary>等待前一份扫描完成，再读取完整网络表。</summary>
    public async Task<ScanResult> ScanAsync(ulong fingerprint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => _scanner.ScanAllPorts(fingerprint), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _lastScanMilliseconds, result.ElapsedMilliseconds);
            Interlocked.Increment(ref _completedScans);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
