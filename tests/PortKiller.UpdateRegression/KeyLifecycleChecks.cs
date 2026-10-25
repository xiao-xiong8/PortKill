using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class KeyLifecycleChecks
{
    internal static async Task Run()
    {
        using var fixture = new KeyLifecycleFixture();
        var clock = new TestClock();
        var state = new DistributionState
        {
            Manifest = fixture.Envelope(fixture.OldKey, "1.1.0", 1, fixture.PackageA),
            Package = fixture.PackageA
        };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Bytes(state.Package));
            return Task.FromResult(Bytes(state.Manifest));
        }));
        var oldOptions = fixture.Options(fixture.OldKey);
        var oldClient = new UpdateService(oldOptions, fixture.CacheOld, http, "1.0.0", clock: clock, trustPath: fixture.TrustOld);

        await oldClient.CheckAsync(default);
        That(oldClient.Ready?.Sequence == 1 && oldClient.Ready.Version == "1.1.0", "旧客户端未接受初始签名发布。");
        Console.WriteLine("PASS key lifecycle: old client accepted initial release");

        // Same package, higher sequence: this is the supported offline renewal path.
        state.Manifest = fixture.Envelope(fixture.OldKey, "1.1.0", 2, fixture.PackageA);
        clock.Advance();
        await oldClient.CheckAsync(default, download: false);
        That(oldClient.Ready?.Sequence == 2 && oldClient.Ready.Sha256 == Convert.ToHexString(SHA256.HashData(fixture.PackageA)),
            "同钥续签没有复用已验证包或刷新发布身份。");
        Console.WriteLine("PASS key lifecycle: old client accepted same-key renewal");

        // A remediation is a new forward release with a new package; replacing an old ZIP in place is not used.
        state.Package = fixture.PackageB;
        state.Manifest = fixture.Envelope(fixture.OldKey, "1.2.0", 3, fixture.PackageB);
        clock.Advance();
        await oldClient.CheckAsync(default);
        That(oldClient.Ready?.Version == "1.2.0" && oldClient.Ready.Sequence == 3
            && oldClient.Ready.Sha256 == Convert.ToHexString(SHA256.HashData(fixture.PackageB)),
            "问题版本修复没有通过更高版本/更高序号前进。");
        Console.WriteLine("PASS key lifecycle: higher-version remediation accepted without downgrade");

        // A lower version remains rejected even when its sequence is newer.
        state.Manifest = fixture.Envelope(fixture.OldKey, "1.1.9", 4, fixture.PackageA);
        clock.Advance();
        await oldClient.CheckAsync(default, download: false);
        That(oldClient.Snapshot.Stage == UpdateStage.Failed && oldClient.Snapshot.Failure == UpdateFailure.Integrity,
            "版本倒退没有被客户端信任记录拒绝。");
        using (var trust = JsonDocument.Parse(File.ReadAllText(fixture.TrustOld)))
            That(ReadLong(trust.RootElement, "Sequence", "sequence") == 3, "版本倒退改变了本地信任记录。");
        Console.WriteLine("PASS key lifecycle: rollback and trust transition rejected");

        // Current clients pin one public key. Rotation therefore requires a coordinated configuration migration.
        state.Package = fixture.PackageC;
        state.Manifest = fixture.Envelope(fixture.NewKey, "1.3.0", 5, fixture.PackageC);
        clock.Advance();
        await oldClient.CheckAsync(default, download: false);
        That(oldClient.Snapshot.Stage == UpdateStage.Failed && oldClient.Snapshot.Failure == UpdateFailure.Integrity
            && oldClient.Ready?.Sequence == 3, "旧公钥客户端错误接受了轮换后的清单或丢失了最后可用版本。");
        using (var trust = JsonDocument.Parse(File.ReadAllText(fixture.TrustOld)))
            That(ReadLong(trust.RootElement, "Sequence", "sequence") == 3, "轮换失败时旧客户端信任记录被替换。");

        var newClient = new UpdateService(fixture.Options(fixture.NewKey), fixture.CacheNew, http, "1.2.0", clock: clock, trustPath: fixture.TrustNew);
        await newClient.CheckAsync(default);
        That(newClient.Ready?.Sequence == 5 && newClient.Ready.Version == "1.3.0", "迁移到新公钥后的新客户端未接受新清单。");
        Console.WriteLine("PASS key lifecycle: old key rejected rotated manifest; new key accepted after coordinated config migration");

        // The four-file update verifier must not silently treat a five-file first-install setup archive as an update.
        string setup = Path.Combine(fixture.Root, "first-install-setup.zip");
        using (var zip = ZipFile.Open(setup, ZipArchiveMode.Create))
        {
            foreach (string name in ReleasePackage.Files.Append("update-settings.json"))
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("fixture");
            }
        }
        var setupManifest = new UpdateManifest
        {
            Version = "1.3.0", Runtime = "win-x64", PackageUrl = "https://updates.example/stable/PortKiller-1.3.0-win-x64.zip",
            Size = new FileInfo(setup).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup))), Sequence = 5,
            PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        };
        bool setupRejected = false;
        try { ReleasePackage.Verify(setup, setupManifest, verifyExecutable: false); }
        catch (InvalidDataException) { setupRejected = true; }
        That(setupRejected, "五文件首装包被错误当作四文件更新包接受。");
        Console.WriteLine("PASS first-install boundary: five-file setup archive rejected by update verifier");
    }

    private sealed class DistributionState
    {
        internal byte[] Manifest { get; set; } = [];
        internal byte[] Package { get; set; } = [];
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        foreach (string name in names)
            if (element.TryGetProperty(name, out JsonElement value)) return value.GetInt64();
        throw new InvalidDataException("信任记录缺少序号字段。");
    }

    private sealed class KeyLifecycleFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PortKiller-KeyLifecycle-" + Guid.NewGuid().ToString("N"));
        internal RSA OldKey { get; } = RSA.Create(2048);
        internal RSA NewKey { get; } = RSA.Create(2048);
        internal byte[] PackageA { get; }
        internal byte[] PackageB { get; }
        internal byte[] PackageC { get; }
        internal string CacheOld => Path.Combine(Root, "old-cache");
        internal string CacheNew => Path.Combine(Root, "new-cache");
        internal string TrustOld => Path.Combine(Root, "old-trust.json");
        internal string TrustNew => Path.Combine(Root, "new-trust.json");

        internal KeyLifecycleFixture()
        {
            Directory.CreateDirectory(Root);
            PackageA = Package(1);
            PackageB = Package(2);
            PackageC = Package(3);
        }

        internal UpdateOptions Options(RSA key) => new()
        {
            Enabled = true, AllowHttp = false, RealtimeEnabled = false,
            ManifestUrl = "https://updates.example/stable/manifest.json",
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem()
        };

        internal byte[] Envelope(RSA key, string version, long sequence, byte[] package)
        {
            var manifest = new UpdateManifest
            {
                Schema = 2, Version = version, Runtime = "win-x64", Sequence = sequence,
                PackageUrl = $"https://updates.example/stable/PortKiller-{version}-win-x64.zip",
                Size = package.Length, Sha256 = Convert.ToHexString(SHA256.HashData(package)),
                PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
                ReleaseNotes = ["key lifecycle fixture"]
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
            return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate
            {
                Payload = Convert.ToBase64String(payload),
                Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            }, ProtocolJsonContext.Default.SignedUpdate);
        }

        private static byte[] Package(byte marker)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string name in ReleasePackage.Files)
                {
                    using Stream stream = zip.CreateEntry(name).Open();
                    stream.Write([marker, 2, 3, 4]);
                }
            }
            return output.ToArray();
        }

        public void Dispose()
        {
            OldKey.Dispose(); NewKey.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
