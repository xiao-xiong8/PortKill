using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

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
    internal static bool Installation(string install, string cache, UpdateResult? result,
        string? currentVersion = null, LegacyUpdateResult? legacy = null)
    {
        string root = Path.Combine(Path.GetFullPath(install), ".portkiller-update");
        if (!IsOrdinaryDirectory(root) || File.Exists(Path.Combine(root, "transaction.json"))) return false;
        bool pending = false;
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(path);
            if (!IsWorker(name) || !IsOrdinaryDirectory(path)) continue;
            // 覆盖历史安装器残留和连续升级；签名清单仅用于定位已安装/已被取代的包，不接受未来候选。
            var manifest = currentVersion == null ? null : WorkerManifest(install, path);
            var installed = manifest != null && UpdateManifest.ParseVersion(manifest.Version)
                <= UpdateManifest.ParseVersion(currentVersion!) ? manifest : null;
            if (installed != null && !DeletePackage(cache, installed.Sha256)) { pending = true; continue; }
            if (name == result?.WorkerName || name == result?.RecoveryWorkerName || installed != null
                || legacy is { Completed: true } && Directory.GetCreationTimeUtc(path) <= legacy.CompletedUtc
                    && (manifest == null || installed != null || legacy.IsError)
                || manifest == null && Directory.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
            {
                BestEffort(() => DeleteWorker(path));
                pending |= Directory.Exists(path);
            }
        }
        if (result != null || legacy is { Completed: true })
        {
            // 成功或恢复已经完成，下一轮更新将创建新备份。
            string backup = Path.Combine(root, "backup");
            BestEffort(() => DeleteTree(backup));
            pending |= Directory.Exists(backup);
            foreach (string file in UpdateInstaller.Files)
                foreach (string suffix in new[] { ".update-new", ".update-restore" })
                {
                    DeleteFile(Path.Combine(install, file + suffix));
                    pending |= File.Exists(Path.Combine(install, file + suffix));
                }
            if (result != null) DeleteFile(Path.Combine(root, "healthy-" + result.OperationId));
            // 本地回执不能提供任意删除路径，只接受固定缓存目录内的 64 位摘要名。
            if (result?.Outcome == "installed") pending |= !DeletePackage(cache, result.PackageSha256);
        }
        return pending;
    }

    private static UpdateManifest? WorkerManifest(string install, string worker)
    {
        string path = Path.Combine(worker, "manifest.json");
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(path).Length > 65536) return null;
            // 过期只限制安装授权；已经完成的更新仍需能在数月后清理。
            return UpdateManifest.Verify(File.ReadAllBytes(path), UpdateOptions.Load(install), ignoreExpiry: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or CryptographicException)
        { UpdateLog.Write("UpdateCleanupManifest", ex); return null; }
    }

    private static bool DeletePackage(string cache, string digest)
    {
        if (!IsDigest(digest) || !IsOrdinaryDirectory(cache)) return true;
        string package = Path.Combine(cache, digest.ToLowerInvariant() + ".zip");
        DeleteFile(package);
        DeleteFile(package + ".manifest");
        DeleteFile(package + ".partial");
        return !File.Exists(package) && !File.Exists(package + ".manifest") && !File.Exists(package + ".partial");
    }

    private static void DeleteWorker(string path)
    {
        // EXE/DLL 或缓存仍被占用时保留清单，下一次启动仍能关联摘要并重试，不受提示已读影响。
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name == "manifest.json") continue;
            if ((entry.Attributes & FileAttributes.Directory) != 0) DeleteTree(entry.FullName);
            else DeleteFile(entry.FullName);
        }
        if (Directory.EnumerateFileSystemEntries(path).All(entry => Path.GetFileName(entry) == "manifest.json"))
            DeleteFile(Path.Combine(path, "manifest.json"));
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
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
