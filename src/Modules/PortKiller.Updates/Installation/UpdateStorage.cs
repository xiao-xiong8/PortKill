using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PortKiller.Updates;

internal static class UpdateStorage
{
    private const long Margin = 64L * 1024 * 1024;
    internal const long MaximumExpandedSize = 256L * 1024 * 1024;

    internal static void Download(string cache, string? install, long size, long partial,
        Func<string, long>? available = null)
    {
        var requirements = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Add(cache, Math.Max(0, size - partial) + Margin);
        if (install != null)
        {
            long current = UpdateInstaller.Files.Sum(file => File.Exists(Path.Combine(install, file))
                ? new FileInfo(Path.Combine(install, file)).Length : 0);
            // 清单不声明解压大小；下载前按协议允许的上限保守估算，安装前再按 ZIP 实际大小检查。
            Add(install, checked(size + current * 2 + MaximumExpandedSize + 128L * 1024 * 1024 + Margin));
        }
        foreach (var entry in requirements) Ensure(entry.Key, entry.Value, available);
        void Add(string path, long bytes)
        {
            string volume = Volume(path);
            requirements.TryGetValue(volume, out long previous);
            requirements[volume] = checked(previous + bytes);
        }
    }

    internal static void Installation(string install, string package)
    {
        using var zip = ZipFile.OpenRead(package);
        long expanded = zip.Entries.Sum(entry => entry.Length);
        if (zip.Entries.Count != UpdateInstaller.Files.Length || expanded > MaximumExpandedSize
            || zip.Entries.Any(entry => entry.Length is <= 0 or > 134217728))
            throw new InvalidDataException("更新包解压大小或文件数量不符合要求。");
        long current = UpdateInstaller.Files.Sum(file => new FileInfo(Path.Combine(install, file)).Length);
        Ensure(Volume(install), checked(new FileInfo(package).Length + current * 2 + expanded
            + zip.Entries.Max(entry => entry.Length) + Margin), null);
    }

    private static string Volume(string path)
    {
        string full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(drive => full.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(drive => drive.Name.Length).FirstOrDefault()?.Name
            ?? Path.GetPathRoot(full) ?? throw new IOException("无法确定更新目录所在磁盘。");
    }

    private static void Ensure(string volume, long required, Func<string, long>? available)
    {
        long free = available?.Invoke(volume) ?? new DriveInfo(volume).AvailableFreeSpace;
        if (free < required)
            throw new IOException($"磁盘 {volume} 空间不足：本次更新预计需要约 {Math.Ceiling(required / 1048576d):0} MB，"
                + $"当前可用 {Math.Floor(free / 1048576d):0} MB。清理空间后可重试。");
    }
}
