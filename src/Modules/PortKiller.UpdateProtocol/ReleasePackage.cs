using System.Diagnostics;
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
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count != Files.Length || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length
            || zip.Entries.Any(x => !Files.Contains(x.FullName, StringComparer.Ordinal) || x.Length is <= 0 or > 134217728)
            || zip.Entries.Sum(x => x.Length) > 268435456)
            throw new InvalidDataException("更新包必须只包含四个 AOT 根目录文件。");
        if (!verifyExecutable) return;
        string temporary = Path.Combine(Path.GetTempPath(), "portkiller-verify-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            zip.GetEntry("PortKiller.exe")!.ExtractToFile(temporary);
            string? version = FileVersionInfo.GetVersionInfo(temporary).FileVersion;
            if (version == null || UpdateManifest.ParseVersion(version) != UpdateManifest.ParseVersion(manifest.Version))
                throw new InvalidDataException("EXE 版本与签名清单不一致。");
        }
        finally { File.Delete(temporary); }
    }
}
