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
        int cleanupRetries = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            var attempt = await Task.Run(() => TryRead(root, cache, currentVersion), token).ConfigureAwait(false);
            if (!attempt.Busy)
            {
                // 放锁与安装器进程退出之间仍可能短暂占用 EXE；同一次启动有界重试，不阻塞 UI。
                if (!attempt.PendingCleanup || cleanupRetries == 3) return attempt.Notice;
                await Task.Delay(100 << cleanupRetries++, token).ConfigureAwait(false);
                continue;
            }
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
                string? currentOperation = UpdateResult.Read(root, currentVersion)?.OperationId
                    ?? LegacyUpdateResult.Read(root, currentVersion)?.OperationId;
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { UpdateLog.Write("UpdateMaintenanceAcknowledge", ex); return false; }
    }

    private static (bool Busy, bool PendingCleanup, UpdateCompletionNotice? Notice) TryRead(string root, string cache, string currentVersion)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return (false, false, null);
            string lockPath = Path.Combine(root, "lock");
            FileStream? lease;
            try
            {
                lease = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            }
            catch (IOException) { return (true, false, null); }
            using (lease)
            {
                if (File.Exists(Path.Combine(root, "transaction.json"))) return (true, false, null);
                UpdateResult? result = UpdateResult.Read(root, currentVersion);
                LegacyUpdateResult? legacy = result == null ? LegacyUpdateResult.Read(root, currentVersion) : null;
                bool pending = false;
                UpdateCleanup.BestEffort(() => pending = UpdateCleanup.Installation(Path.GetDirectoryName(root)!, cache, result,
                    currentVersion, legacy));
                string? message = result?.Message;
                string? operation = result?.OperationId;
                bool error = result != null && result.Outcome != "installed";
                // 有结构化回执时绝不回退到残留的旧文本（包括版本不匹配的结果）。
                if (legacy != null)
                {
                    message = legacy.Message;
                    operation = legacy.OperationId;
                    error = legacy.IsError;
                }
                if (string.IsNullOrWhiteSpace(message) || operation == null) return (false, pending, null);
                Directory.CreateDirectory(cache);
                string installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)));
                string consumed = Path.Combine(cache, "result-read-" + installation + ".txt");
                if (File.Exists(consumed) && File.ReadAllText(consumed) == operation) return (false, pending, null);
                return (false, pending, new(message, error, operation));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { UpdateLog.Write("UpdateMaintenance", ex); return (false, false, null); }
    }
}
