using System.IO.Compression;
using System.Security.Cryptography;

namespace PortKiller.Updates;

public static class ReleasePackage
{
    public static readonly string[] Files = ["PortKiller.exe", "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll"];

    public static void Verify(string package, UpdateManifest manifest, bool verifyExecutable = true)
    {
        using var input = File.OpenRead(package);
        if (input.Length != manifest.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包长度或 SHA-256 不匹配。");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        string expectedPackageName = $"PortKiller-{manifest.Version}-win-x64.zip";
        string packageName = Uri.UnescapeDataString(new Uri(manifest.PackageUrl).AbsolutePath.Split('/').Last());
        if (!packageName.Equals(expectedPackageName, StringComparison.Ordinal))
            throw new InvalidDataException("更新包分发路径与签名版本不匹配。");
        if (zip.Entries.Count != Files.Length || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length
            || zip.Entries.Any(x => !Files.Contains(x.FullName, StringComparer.Ordinal) || x.Length is <= 0 or > 134217728)
            || zip.Entries.Sum(x => x.Length) > 268435456)
            throw new InvalidDataException("更新包必须只包含四个 AOT 根目录文件。");
        string temporary = Path.Combine(Path.GetTempPath(), "portkiller-verify-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            long actualTotal = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                bool executable = entry.FullName == "PortKiller.exe";
                using Stream source = entry.Open();
                using Stream target = executable ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None) : Stream.Null;
                byte[] buffer = new byte[64 * 1024]; long actual = 0; int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    actual = checked(actual + read); actualTotal = checked(actualTotal + read);
                    if (actual > 134217728 || actualTotal > 268435456) throw new InvalidDataException("更新包实际解压大小超限。");
                    target.Write(buffer, 0, read);
                }
                if (actual != entry.Length) throw new InvalidDataException("更新包解压长度与 ZIP 声明不一致。");
                if (executable) ((FileStream)target).Flush(flushToDisk: true);
            }
            if (!verifyExecutable) return;
            string? version;
            using (var executableStream = File.OpenRead(temporary)) version = PeVersionReader.ReadFileVersion(executableStream);
            if (version == null || UpdateManifest.ParseVersion(version) != UpdateManifest.ParseVersion(manifest.Version))
                throw new InvalidDataException("EXE 版本与签名清单不一致。");
        }
        finally { File.Delete(temporary); }
    }
}
