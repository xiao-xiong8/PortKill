using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PortKiller.Updates;

// 旧安装器先于新程序运行，只写 result.txt。兼容逻辑必须随读取端保留，不能只修改新安装器。
internal sealed record LegacyUpdateResult(string Message, string OperationId, bool IsError,
    bool Completed, DateTime CompletedUtc)
{
    internal static LegacyUpdateResult? Read(string root, string currentVersion)
    {
        string path = Path.Combine(root, "result.txt");
        if (File.Exists(Path.Combine(root, "result.json")) || !File.Exists(path)
            || new FileInfo(path).Length > 16384) return null;
        string message = File.ReadAllText(path).Trim();
        if (message.Length == 0) return null;
        string? version = message.StartsWith("更新成功：", StringComparison.Ordinal) ? message[5..]
            : message.StartsWith("已更新至 v", StringComparison.Ordinal) ? message[6..] : null;
        bool success = version != null, completed = false;
        if (success)
        {
            // 不能用旧成功提示为另一个版本清理或重复报成功。
            if (!TryVersion(version!, out var target) || target != UpdateManifest.ParseVersion(currentVersion)) return null;
            completed = true;
        }
        else if (message == "检测到中断的更新，已恢复可用版本。") completed = true;
        else
        {
            const string suffix = " 安装失败，已恢复旧版本。";
            if (message.StartsWith("版本 ", StringComparison.Ordinal) && message.EndsWith(suffix, StringComparison.Ordinal))
                completed = TryVersion(message[3..^suffix.Length], out _);
        }
        DateTime time = File.GetLastWriteTimeUtc(path);
        return new(message, "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message))),
            !success, completed && time <= DateTime.UtcNow.AddMinutes(5), time);
    }

    private static bool TryVersion(string text, out Version? version)
    {
        try { version = UpdateManifest.ParseVersion(text); return true; }
        catch (InvalidDataException) { version = null; return false; }
    }
}
