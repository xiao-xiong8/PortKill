using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Docker;

public sealed record DockerEnrichmentResult(long Version, IReadOnlyList<PortConnection> Items, string? Error = null);

/// <summary>在单一后台消费者中关联快照；新请求替换待处理请求并取消旧计算。</summary>
public sealed class DockerEnrichmentCoordinator : IDisposable
{
    private sealed record Request(long Version, IReadOnlyList<PortConnection> Hosts, DockerInventory Inventory);
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _running;
    private bool _started, _disposed;
    private long _version;
    public long Version => Interlocked.Read(ref _version);
    public event Action<DockerEnrichmentResult>? Completed;

    public void Enqueue(IReadOnlyList<PortConnection> hosts, DockerInventory inventory)
    {
        lock (_sync)
        {
            if (_disposed) return;
            long version = Interlocked.Increment(ref _version);
            _running?.Cancel();
            _requests.Writer.TryWrite(new(version, hosts, inventory));
            if (!_started) { _started = true; _ = Task.Run(RunAsync); }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lock (_sync)
                {
                    if (_disposed) return;
                    if (request.Version != Version) continue;
                    _running = cancellation;
                }
                try
                {
                    DockerEnrichmentResult result;
                    try { result = new(request.Version, DockerInventoryCoordinator.Enrich(request.Hosts, request.Inventory, cancellation.Token)); }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        string reason = "Docker 端点关联失败：" + error.Message;
                        result = new(request.Version, DockerInventoryCoordinator.Enrich(request.Hosts, DockerInventory.Unavailable(reason), cancellation.Token), reason);
                    }
                    if (request.Version == Version && !cancellation.IsCancellationRequested) Completed?.Invoke(result);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally { lock (_sync) if (ReferenceEquals(_running, cancellation)) _running = null; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; Interlocked.Increment(ref _version);
            _requests.Writer.TryComplete(); _running?.Cancel(); _stop.Cancel();
        }
    }
}
