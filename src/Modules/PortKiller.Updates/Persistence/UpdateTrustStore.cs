using System;
using System.IO;
using System.Text.Json;

namespace PortKiller.Updates;

internal static class UpdateTrustStore
{
    private static readonly object Sync = new();

    // Set only by the separate, non-shipping regression probe before starting any worker.
    internal static string? ProbePath { get; set; }
    internal static string DefaultPath => ProbePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortKiller", "update-trust.json");

    internal static void Accept(UpdateManifest manifest, string path)
    {
        if (manifest.Schema < 2) return;
        lock (Sync)
        {
            TrustedRelease? current = null;
            if (File.Exists(path))
                current = JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.TrustedRelease)
                    ?? throw new InvalidDataException("本地更新信任记录为空。");

            Version nextVersion = UpdateManifest.ParseVersion(manifest.Version);
            if (current != null)
            {
                Version trustedVersion = UpdateManifest.ParseVersion(current.Version);
                if (manifest.Sequence < current.Sequence || nextVersion < trustedVersion)
                    throw new InvalidDataException("更新清单低于本机已接受的发布序号或版本。");
                if (manifest.Sequence == current.Sequence)
                {
                    if (manifest.Version != current.Version
                        || !manifest.PayloadSha256.Equals(current.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("同一发布序号对应了不同的签名内容。");
                    return;
                }
            }

            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporary = fullPath + ".tmp";
            string json = JsonSerializer.Serialize(new TrustedRelease
            {
                Sequence = manifest.Sequence,
                Version = manifest.Version,
                PayloadSha256 = manifest.PayloadSha256
            }, UpdateJsonContext.Default.TrustedRelease);
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullPath, true);
        }
    }
}
