using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

public sealed record UpdateCompletionNotice(string Message, bool IsError, string OperationId);

/// <summary>安装进程放开锁后读取回执。已读标记放在当前用户缓存中，兼容只读安装目录。</summary>
public static class UpdateMaintenance
{
    public static async Task<UpdateCompletionNotice?> RunAsync(string install, string cache, string currentVersion,
        CancellationToken token, bool waitForInstaller = false)
    {
        string root = Path.Combine(Path.GetFullPath(install), ".portkiller-update");
        if (!Directory.Exists(root)) return null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        do
        {
            token.ThrowIfCancellationRequested();
            var attempt = await Task.Run(() => TryRead(root, cache, currentVersion), token).ConfigureAwait(false);
            if (!attempt.Busy) return attempt.Notice;
            if (!waitForInstaller) return null;
            await Task.Delay(100, token).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);
        return null;
    }

    /// <summary>确认提示已交给当前 UI 后才记为已读；失败时下次启动会重试显示。</summary>
    public static bool MarkConsumed(string install, string cache, UpdateCompletionNotice notice, string currentVersion)
    {
        string root = Path.Combine(Path.GetFullPath(install), ".portkiller-update");
        if (string.IsNullOrWhiteSpace(notice.OperationId) || notice.OperationId.Length > 256) return false;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            string lockPath = Path.Combine(root, "lock");
            FileStream? lease = null;
            try
            {
                if (File.Exists(lockPath)) lease = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException) { return false; }
            using (lease)
            {
                if (File.Exists(Path.Combine(root, "transaction.json"))) return false;
                string resultPath = Path.Combine(root, "result.json");
                string? currentOperation = UpdateResult.Read(root, currentVersion)?.OperationId;
                if (currentOperation == null && !File.Exists(resultPath))
                {
                    string legacy = Path.Combine(root, "result.txt");
                    if (File.Exists(legacy) && new FileInfo(legacy).Length <= 16384)
                        currentOperation = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(legacy).Trim())));
                }
                if (!string.Equals(currentOperation, notice.OperationId, StringComparison.Ordinal)) return false;

                Directory.CreateDirectory(cache);
                string installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)));
                string consumed = Path.Combine(cache, "result-read-" + installation + ".txt");
                string temporary = consumed + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Encoding.UTF8)) writer.Write(notice.OperationId);
                File.Move(temporary, consumed, true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { UpdateLog.Write("UpdateMaintenanceAcknowledge", ex); return false; }
    }

    private static (bool Busy, UpdateCompletionNotice? Notice) TryRead(string root, string cache, string currentVersion)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return (false, null);
            string lockPath = Path.Combine(root, "lock");
            FileStream? lease;
            try
            {
                lease = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            }
            catch (IOException) { return (true, null); }
            using (lease)
            {
                if (File.Exists(Path.Combine(root, "transaction.json"))) return (true, null);
                UpdateResult? result = UpdateResult.Read(root, currentVersion);
                UpdateCleanup.BestEffort(() => UpdateCleanup.Installation(Path.GetDirectoryName(root)!, cache, result));
                string? message = result?.Message;
                string? operation = result?.OperationId;
                bool error = result != null && result.Outcome != "installed";
                // 有结构化回执时绝不回退到残留的旧文本（包括版本不匹配的结果）。
                if (result == null && !File.Exists(Path.Combine(root, "result.json")))
                {
                    string legacy = Path.Combine(root, "result.txt");
                    if (File.Exists(legacy) && new FileInfo(legacy).Length <= 16384)
                    {
                        message = File.ReadAllText(legacy).Trim();
                        operation = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
                        error = !message.StartsWith("更新成功", StringComparison.Ordinal)
                            && !message.StartsWith("已更新至", StringComparison.Ordinal);
                    }
                }
                if (string.IsNullOrWhiteSpace(message) || operation == null) return (false, null);
                Directory.CreateDirectory(cache);
                string installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)));
                string consumed = Path.Combine(cache, "result-read-" + installation + ".txt");
                if (File.Exists(consumed) && File.ReadAllText(consumed) == operation) return (false, null);
                return (false, new(message, error, operation));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { UpdateLog.Write("UpdateMaintenance", ex); return (false, null); }
    }
}
