using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortKiller.Updates;

internal sealed record UpdateOperationEntry(string OperationId, string? Version, long? Sequence,
    string Phase, double DurationMs, int Attempt, string Failure, DateTimeOffset Utc);

[JsonSerializable(typeof(UpdateOperationEntry))]
internal partial class OperationJsonContext : JsonSerializerContext { }

/// <summary>Stage boundaries only, two files of at most about 1 MiB each.</summary>
internal sealed class UpdateOperationLog(string cache)
{
    private readonly string _operation = Guid.NewGuid().ToString("N");
    private UpdateStage? _stage;
    private long _started = Stopwatch.GetTimestamp();
    internal void Record(UpdateSnapshot snapshot)
    {
        if (_stage == snapshot.Stage) return;
        long now = Stopwatch.GetTimestamp();
        var entry = new UpdateOperationEntry(_operation, snapshot.Manifest?.Version, snapshot.Manifest?.Sequence,
            snapshot.Stage.ToString(), Stopwatch.GetElapsedTime(_started, now).TotalMilliseconds,
            snapshot.Attempt, snapshot.Failure.ToString(), DateTimeOffset.UtcNow);
        _stage = snapshot.Stage; _started = now;
        try
        {
            Directory.CreateDirectory(cache);
            string path = Path.Combine(cache, "operations.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 1048576) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, OperationJsonContext.Default.UpdateOperationEntry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
