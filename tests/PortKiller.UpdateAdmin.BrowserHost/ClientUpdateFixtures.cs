using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.Updates;

internal static class ClientUpdateFixtures
{
    internal sealed record Fixture(string OldVersion, string NewVersion, string SuccessSandbox, string RollbackSandbox);

    public static Fixture? Create(string runtime, string fixtures, RSA signing, UpdateManifest previous)
    {
        string? oldDirectory = Environment.GetEnvironmentVariable("PORTKILL_BROWSER_OLD_PROBE_DIRECTORY");
        string? newDirectory = Environment.GetEnvironmentVariable("PORTKILL_BROWSER_NEW_PROBE_DIRECTORY");
        if (oldDirectory == null && newDirectory == null) return null;
        if (oldDirectory == null || newDirectory == null) throw new InvalidOperationException("Both Native AOT probe versions are required.");
        string oldVersion = FileVersionInfo.GetVersionInfo(Path.Combine(oldDirectory, "PortKiller.UpdateProbe.exe")).FileVersion!;
        string newVersion = FileVersionInfo.GetVersionInfo(Path.Combine(newDirectory, "PortKiller.UpdateProbe.exe")).FileVersion!;
        if (UpdateManifest.ParseVersion(newVersion) <= UpdateManifest.ParseVersion(oldVersion)
            || UpdateManifest.ParseVersion(newVersion) <= UpdateManifest.ParseVersion(previous.Version))
            throw new InvalidOperationException("Client probe versions must advance the actual publication.");
        string package = Path.Combine(fixtures, "client-package.zip");
        using (var zip = new ZipArchive(File.Create(package), ZipArchiveMode.Create))
            foreach (string name in ReleasePackage.Files)
                ZipFileExtensions.CreateEntryFromFile(zip, Path.Combine(newDirectory,
                    name == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : name), name);
        var manifest = new UpdateManifest
        {
            Version = newVersion, Sequence = previous.Sequence + 1,
            PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            PackageUrl = $"http://127.0.0.1:15082/stable/PortKiller-{newVersion}-win-x64.zip",
            Size = new FileInfo(package).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))),
            ReleaseNotes = ["Isolated Native AOT install and rollback integration"]
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
        File.WriteAllBytes(Path.Combine(fixtures, "client-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate
        {
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(signing.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        }, ProtocolJsonContext.Default.SignedUpdate));
        string success = Prepare("success"), rollback = Prepare("unhealthy");
        return new(oldVersion, newVersion, success, rollback);

        string Prepare(string mode)
        {
            string sandbox = Path.Combine(runtime, "client-" + mode + "-" + Guid.NewGuid().ToString("N"));
            string install = Path.Combine(sandbox, "install"); Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(sandbox, "probe-sandbox.marker"), "isolated");
            File.WriteAllText(Path.Combine(sandbox, "mode"), mode);
            foreach (string name in ReleasePackage.Files)
                File.Copy(Path.Combine(oldDirectory, name == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : name), Path.Combine(install, name));
            File.WriteAllBytes(Path.Combine(install, "update-settings.json"), JsonSerializer.SerializeToUtf8Bytes(new UpdateOptions
            {
                Enabled = true, AllowHttp = true, UseSystemProxy = false, RealtimeEnabled = false,
                ManifestUrl = "http://127.0.0.1:15082/stable/manifest.json", PublicKeyPem = signing.ExportSubjectPublicKeyInfoPem(),
                ClientReportUrl = "https://127.0.0.1:15081/api/v1/client-installations/"
            }));
            File.WriteAllText(Path.Combine(install, "user-settings.txt"), "preserved-settings");
            return sandbox;
        }
    }
}
