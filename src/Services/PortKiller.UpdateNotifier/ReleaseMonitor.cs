using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using PortKiller.Updates;

namespace PortKiller.UpdateNotifier;

public sealed class ReleaseMonitor(string root, ManifestVerificationOptions options) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<ReleaseHint>> _connections = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _sync = new();
    private UpdateManifest? _current;
    private string? _digest;
    private (long Length, DateTime Modified)? _identity;
    private string? _error = "No verified manifest";
    public int Connections => _connections.Count;
    public object Health()
    {
        lock (_sync) return new { Running = true, ManifestValid = _error == null && _current?.ExpiresUtc > DateTimeOffset.UtcNow,
            Sequence = _current?.Sequence, Version = _current?.Version, Connections, Failure = _error,
            DaysRemaining = _current == null ? (double?)null : (_current.ExpiresUtc - DateTimeOffset.UtcNow).TotalDays };
    }
    public (Guid Id, ChannelReader<ReleaseHint> Reader) Subscribe()
    {
        lock (_sync)
        {
            var channel = Channel.CreateBounded<ReleaseHint>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            var id = Guid.NewGuid(); _connections[id] = channel;
            if (_error == null && _current?.ExpiresUtc > DateTimeOffset.UtcNow) channel.Writer.TryWrite(Hint(_current));
            return (id, channel.Reader);
        }
    }
    public void Unsubscribe(Guid id) { if (_connections.TryRemove(id, out var channel)) channel.Writer.TryComplete(); }
    private static ReleaseHint Hint(UpdateManifest manifest) => new(1, "stable", manifest.Version, manifest.Sequence);
    private void Wake() { try { _changed.Release(); } catch (SemaphoreFullException) { } }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(root);
        using var watcher = new FileSystemWatcher(root, "manifest.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, _) => Wake(); watcher.Created += (_, _) => Wake(); watcher.Renamed += (_, _) => Wake(); watcher.Error += (_, _) => Wake();
        watcher.EnableRaisingEvents = true;
        bool force = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            Refresh(force);
            try { force = await _changed.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
    private void Refresh(bool force)
    {
        try
        {
            var file = new FileInfo(Path.Combine(root, "manifest.json"));
            if (!file.Exists) throw new FileNotFoundException("No active manifest");
            var identity = (file.Length, file.LastWriteTimeUtc);
            if (!force && _identity == identity) return;
            if (file.Length > 65536) throw new InvalidDataException("Manifest too large");
            byte[] envelope = File.ReadAllBytes(file.FullName);
            string digest = Convert.ToHexString(SHA256.HashData(envelope));
            lock (_sync)
            {
                if (digest == _digest)
                {
                    bool recovered = _error != null;
                    _identity = identity; _error = null;
                    if (recovered && _current?.ExpiresUtc > DateTimeOffset.UtcNow)
                        foreach (var channel in _connections.Values) channel.Writer.TryWrite(Hint(_current));
                    return;
                }
                var candidate = UpdateManifest.Verify(envelope, options);
                if (candidate.Schema != 2 || _current != null && (candidate.Sequence <= _current.Sequence
                    || UpdateManifest.ParseVersion(candidate.Version) < UpdateManifest.ParseVersion(_current.Version)
                    || UpdateManifest.ParseVersion(candidate.Version) == UpdateManifest.ParseVersion(_current.Version)
                    && !candidate.Sha256.Equals(_current.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Non-monotonic release");
                _current = candidate; _digest = digest; _identity = identity; _error = null;
                foreach (var channel in _connections.Values) channel.Writer.TryWrite(Hint(candidate));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { lock (_sync) _error = ex.GetType().Name + ": " + ex.Message; }
    }
}
