using System;
using System.IO;
using System.Linq;

namespace PortKiller.Updates;

internal static class UpdateCleanup
{
    internal static bool IsOrdinaryDirectory(string path)
        => Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    internal static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsWorker(string name) => (name.StartsWith("worker-", StringComparison.Ordinal)
        && Guid.TryParseExact(name[7..], "N", out _)) || (name.StartsWith("recovery-", StringComparison.Ordinal)
        && Guid.TryParseExact(name[9..], "N", out _));

    /// <summary>调用者持有安装锁；没有事务日志才允许执行。保留最新结果和诊断日志。</summary>
    internal static void Installation(string install, string cache, UpdateResult? result)
    {
        string root = Path.Combine(Path.GetFullPath(install), ".portkiller-update");
        if (!IsOrdinaryDirectory(root) || File.Exists(Path.Combine(root, "transaction.json"))) return;
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(path);
            if (!IsWorker(name)) continue;
            if (name == result?.WorkerName || name == result?.RecoveryWorkerName
                || Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                BestEffort(() => DeleteTree(path));
        }
        if (result != null)
        {
            // 成功或恢复已经完成，下一轮更新将创建新备份。
            string backup = Path.Combine(root, "backup");
            BestEffort(() => DeleteTree(backup));
            foreach (string file in UpdateInstaller.Files)
                foreach (string suffix in new[] { ".update-new", ".update-restore" })
                    DeleteFile(Path.Combine(install, file + suffix));
            string health = "healthy-" + result.OperationId;
            DeleteFile(Path.Combine(root, health));
            // 本地回执不能提供任意删除路径，只接受固定缓存目录内的 64 位摘要名。
            if (result.Outcome == "installed" && IsDigest(result.PackageSha256) && IsOrdinaryDirectory(cache))
            {
                string package = Path.Combine(cache, result.PackageSha256.ToLowerInvariant() + ".zip");
                DeleteFile(package);
                DeleteFile(package + ".manifest");
                DeleteFile(package + ".partial");
            }
        }
    }

    internal static void Cache(string cache, string? protectedPackage)
    {
        if (!IsOrdinaryDirectory(cache)) return;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var packages = new DirectoryInfo(cache).EnumerateFiles("*.zip")
            .Where(file => IsDigest(Path.GetFileNameWithoutExtension(file.Name))
                && !file.FullName.Equals(protectedPackage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        for (int i = 0; i < packages.Length; i++)
            if (i > 0 || packages[i].LastWriteTimeUtc < cutoff)
            {
                DeleteFile(packages[i].FullName);
                if (!File.Exists(packages[i].FullName)) DeleteFile(packages[i].FullName + ".manifest");
            }
        foreach (var file in new DirectoryInfo(cache).EnumerateFiles("*.zip.partial"))
        {
            string package = file.FullName[..^8];
            if (IsDigest(Path.GetFileNameWithoutExtension(package)) && file.LastWriteTimeUtc < cutoff
                && !package.Equals(protectedPackage, StringComparison.OrdinalIgnoreCase)) DeleteFile(file.FullName);
        }
        foreach (var file in new DirectoryInfo(cache).EnumerateFiles("*.zip.manifest"))
            if (IsDigest(Path.GetFileNameWithoutExtension(file.Name[..^9])) && file.LastWriteTimeUtc < cutoff
                && !File.Exists(file.FullName[..^9])
                && !file.FullName[..^9].Equals(protectedPackage, StringComparison.OrdinalIgnoreCase)) DeleteFile(file.FullName);
    }

    // 不使用 Directory.Delete(..., true)：逐层拒绝链接/重解析点，避免越过安装目录。
    internal static void DeleteTree(string path)
    {
        if (!IsOrdinaryDirectory(path)) return;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0) DeleteTree(entry.FullName);
            else DeleteFile(entry.FullName);
        }
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    internal static void DeleteFile(string path) => BestEffort(() =>
    {
        if (File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
            File.Delete(path);
    });

    internal static void BestEffort(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { UpdateLog.Write("UpdateCleanup", ex); }
    }
}
