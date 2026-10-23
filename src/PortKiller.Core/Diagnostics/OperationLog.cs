using System;
using System.IO;
using System.Text;

namespace PortKiller.Services;

/// <summary>
/// 操作审计日志：所有终止 / 释放动作留痕到 %LocalAppData%\PortKiller\operations.log。
///
/// 强杀进程是破坏性操作，生产级工具必须可追溯「何时、对谁、做了什么、结果如何」——
/// 误杀排查、安全审计、用户扯皮都靠这份流水账。写入失败保持静默，绝不影响主流程。
/// </summary>
public static class OperationLog
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortKiller", "operations.log");

    public static string LogPathForDisplay => LogPath;

    private const long MaxLogBytes = 1_048_576;   // 超过 1MB 触发修剪
    private const int KeepTailBytes = 262_144;    // 修剪后保留最近 256KB

    public static void Record(string action, string detail)
    {
        try
        {
            lock (Gate)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                TrimIfNeeded(LogPath);

                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{action}] {detail}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 审计失败静默
        }
    }

    /// <summary>
    /// 日志修剪：超过 1MB 时只保留最近 256KB（从完整行开始），防止无界增长。
    /// error.log 也复用此入口。全程静默，修剪失败绝不影响主流程。
    /// </summary>
    public static void TrimIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= MaxLogBytes) return;

            byte[] tail = new byte[KeepTailBytes];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(-KeepTailBytes, SeekOrigin.End);
                int read = fs.Read(tail, 0, tail.Length);
                if (read < tail.Length) Array.Resize(ref tail, read);
            }

            // 从第一个换行符之后开始保留，避免开头是半行
            int start = Array.IndexOf(tail, (byte)'\n');
            if (start >= 0 && start < tail.Length - 1)
            {
                string kept = Encoding.UTF8.GetString(tail, start + 1, tail.Length - start - 1);
                File.WriteAllText(path, kept, Encoding.UTF8);
            }
            else
            {
                File.WriteAllBytes(path, tail);
            }
        }
        catch
        {
            // 修剪失败静默
        }
    }
}
