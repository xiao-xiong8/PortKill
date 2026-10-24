using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateManagement.Security;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PackageValidationTests
{
    [Fact]
    public void ReadsWindowsPeVersionAndActualZipOutputOnTheHostPlatform()
    {
        string repository = FindRepositoryRoot();
        string executable = Path.Combine(repository, "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0",
            "PortKiller.UpdateTool" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable), $"UpdateTool apphost was not built: {executable}");
        string? expectedVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion;
        Assert.False(string.IsNullOrWhiteSpace(expectedVersion));

        string root = Path.Combine(Path.GetTempPath(), "PortKill Package Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "PortKiller-package.zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (string name in ReleasePackage.Files)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name);
                    using Stream output = entry.Open();
                    if (name == "PortKiller.exe") using (FileStream input = File.OpenRead(executable)) input.CopyTo(output);
                    else output.Write([1, 2, 3, 4]);
                }
            }
            byte[] bytes = File.ReadAllBytes(package);
            var manifest = new UpdateManifest
            {
                Version = UpdateManifest.ParseVersion(expectedVersion!).ToString(),
                PackageUrl = $"https://updates.example/stable/PortKiller-{UpdateManifest.ParseVersion(expectedVersion!).ToString()}-win-x64.zip",
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Sequence = 1,
                PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            };
            ReleasePackage.Verify(package, manifest);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsUnsafePackageUrlsBeforeAnyDownload()
    {
        using var key = RSA.Create(2048);
        var options = new ManifestVerificationOptions
        {
            ManifestUrl = "https://updates.example/stable/manifest.json",
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };
        foreach (string packageUrl in new[]
        {
            "https://updates.example/stable/",
            "https://updates.example/stable/../payload.zip",
            "https://updates.example/stable/%2e%2e/payload.zip",
            "https://updates.example/stable/a%2fb.zip",
            "https://updates.example/stable/a\\b.zip"
        })
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest
            {
                Version = "1.0.0", Runtime = "win-x64", PackageUrl = packageUrl, Size = 1,
                Sha256 = new string('A', 64), Sequence = 1, PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
            }, ProtocolJsonContext.Default.UpdateManifest);
            byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate
            {
                Payload = Convert.ToBase64String(payload),
                Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            }, ProtocolJsonContext.Default.SignedUpdate);
            Assert.Throws<InvalidDataException>(() => UpdateManifest.Verify(envelope, options));
        }
    }

    [Fact]
    public void RejectsUnexpectedZipEntryNamesBeforeExtraction()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortKill Package Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (string name in new[] { "PortKiller.exe", "av_libglesv2.dll", "nested/libHarfBuzzSharp.dll", "libSkiaSharp.dll" })
                {
                    using Stream output = archive.CreateEntry(name).Open();
                    output.Write([1, 2, 3]);
                }
            }
            byte[] bytes = File.ReadAllBytes(package);
            var manifest = new UpdateManifest
            {
                Version = "1.0.0", PackageUrl = "https://updates.example/stable/PortKiller-1.0.0-win-x64.zip",
                Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Sequence = 1,
                PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
            };
            Assert.Throws<InvalidDataException>(() => ReleasePackage.Verify(package, manifest, verifyExecutable: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root could not be located from test output.");
    }
}
