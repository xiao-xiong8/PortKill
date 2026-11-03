using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Docker;

public sealed class RestartPolicyJournal : IRestartPolicyJournal
{
    private readonly string _directory;
    public RestartPolicyJournal(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKiller", "docker-policies");
    private string PathFor(RestartPolicyChange change) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(change.EngineId + ":" + change.ContainerId))) + ".json");
    public async Task SaveAsync(RestartPolicyChange change, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        string target = PathFor(change);
        // 同一策略的重试保留第一次记录；不同策略不能悄悄复用旧恢复目标。
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length > 8192) throw new InvalidDataException("重启策略恢复记录长度无效。");
            var previous = JsonSerializer.Deserialize(await File.ReadAllTextAsync(target, token).ConfigureAwait(false), DockerJsonContext.Default.RestartPolicyChange);
            if (previous?.EngineId == change.EngineId && previous.ContainerId == change.ContainerId
                && previous.OriginalName == change.OriginalName && previous.OriginalMaximumRetryCount == change.OriginalMaximumRetryCount) return;
            throw new InvalidOperationException("该容器已有不同策略的恢复记录，请先处理旧记录；尚未修改重启策略。");
        }
        if (Directory.EnumerateFiles(_directory, "*.json").Take(513).Count() >= 512)
            throw new IOException("重启策略记录已达 512 条，请先处理已有恢复记录。");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(change, DockerJsonContext.Default.RestartPolicyChange), token).ConfigureAwait(false);
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<IReadOnlyList<RestartPolicyChange>> ReadAsync(CancellationToken token)
    {
        var result = new List<RestartPolicyChange>();
        if (!Directory.Exists(_directory)) return result;
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json").Take(512))
        {
            if (new FileInfo(file).Length > 8192) throw new InvalidDataException("重启策略恢复记录长度无效。");
            var record = JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, token).ConfigureAwait(false), DockerJsonContext.Default.RestartPolicyChange)
                ?? throw new InvalidDataException("重启策略恢复记录无效。");
            result.Add(record);
        }
        return result.OrderByDescending(r => r.ChangedAt).ToArray();
    }
    public Task RemoveAsync(RestartPolicyChange change, CancellationToken token)
    { token.ThrowIfCancellationRequested(); File.Delete(PathFor(change)); return Task.CompletedTask; }
}
