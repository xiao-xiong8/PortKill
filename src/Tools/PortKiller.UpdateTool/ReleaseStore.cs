using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.UpdateTool;

public sealed record ReleaseReceipt(string Version, long Sequence, string ManifestSha256, string PackageSha256,
    string PackageName, DateTimeOffset ExpiresUtc, DateTimeOffset VerifiedUtc);

/// <summary>The signed public manifest is the commit record. Recovery never rolls it back.</summary>
public sealed class ReleaseStore(string root, ManifestVerificationOptions options)
{
    private string Root => Path.GetFullPath(root);
    private string State => Path.Combine(Path.GetDirectoryName(Root)!, "." + Path.GetFileName(Root) + "-update-state");
    private string Manifest => Path.Combine(Root, "manifest.json");
    private FileStream Lock()
    {
        Directory.CreateDirectory(Root); Directory.CreateDirectory(State);
        return new FileStream(Path.Combine(State, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public ReleaseReceipt? Inspect(bool repair = false)
    {
        using var lease = Lock();
        var receipt = ReadCurrent();
        if (repair && receipt != null) SaveReceipt(receipt);
        return receipt;
    }
    public ReleaseReceipt Activate(string manifestPath, string package, string expected, Action<string>? fault = null)
    {
        using var lease = Lock();
        byte[] envelope = ReadManifest(manifestPath);
        var manifest = UpdateManifest.Verify(envelope, options);
        if (manifest.Schema != 2) throw new InvalidDataException("服务端发布要求 schema 2。");
        var candidate = Receipt(envelope, manifest);
        ReleasePackage.Verify(package, manifest);
        var current = ReadCurrent();
        if (current?.ManifestSha256 == candidate.ManifestSha256) { SaveReceipt(current); return current; }
        if ((current?.ManifestSha256 ?? "none") != expected) throw new InvalidOperationException("发布冲突：当前清单与预期不一致，请检查服务器状态。");
        if (current != null && (manifest.Sequence <= current.Sequence
            || UpdateManifest.ParseVersion(manifest.Version) < UpdateManifest.ParseVersion(current.Version)
                    || UpdateManifest.ParseVersion(manifest.Version) == UpdateManifest.ParseVersion(current.Version) && !manifest.Sha256.Equals(current.PackageSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("拒绝版本/序号倒退或覆盖同版本包。");
        DurableWrite(Path.Combine(State, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(new { Expected = expected, Candidate = candidate }));
        fault?.Invoke("staged");
        string target = Path.Combine(Root, candidate.PackageName);
        if (File.Exists(target))
        {
            ReleasePackage.Verify(target, manifest);
        }
        else
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".uploading";
            try
            {
                using (var source = File.OpenRead(package))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { source.CopyTo(output); output.Flush(true); }
                ReleasePackage.Verify(temporary, manifest);
                MakePublic(temporary);
                File.Move(temporary, target);
            }
            finally { File.Delete(temporary); }
        }
        fault?.Invoke("package-published");
        DurableWrite(Manifest, envelope, publicAccess: true);
        fault?.Invoke("manifest-activated");
        SaveReceipt(candidate);
        fault?.Invoke("receipt-written");
        return candidate;
    }
    private ReleaseReceipt? ReadCurrent()
    {
        if (!File.Exists(Manifest)) return null;
        byte[] bytes = ReadManifest(Manifest);
        // Allow an expired current release to be renewed, but never accept a malformed signature.
        var manifest = UpdateManifest.Verify(bytes, options, ignoreExpiry: true);
        var receipt = Receipt(bytes, manifest);
        ReleasePackage.Verify(Path.Combine(Root, receipt.PackageName), manifest);
        return receipt;
    }
    public static byte[] ReadManifest(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 65536) throw new InvalidDataException("清单超限。");
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096]; int read;
        while ((read = input.Read(buffer)) != 0)
        { if (output.Length + read > 65536) throw new InvalidDataException("清单超限。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static ReleaseReceipt Receipt(byte[] envelope, UpdateManifest manifest)
    {
        string name = "PortKiller-" + manifest.Version + "-win-x64.zip";
        if (Uri.UnescapeDataString(new Uri(manifest.PackageUrl).AbsolutePath.Split('/').Last()) != name)
            throw new InvalidDataException("版本包文件名不匹配。");
        return new(manifest.Version, manifest.Sequence, Convert.ToHexString(SHA256.HashData(envelope)), manifest.Sha256,
            name, manifest.ExpiresUtc, DateTimeOffset.UtcNow);
    }
    private void SaveReceipt(ReleaseReceipt receipt)
    {
        DurableWrite(Path.Combine(State, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt));
        File.Delete(Path.Combine(State, "transaction.json"));
    }
    private static void MakePublic(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
    public static void DurableWrite(string path, byte[] bytes, bool publicAccess = false)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (publicAccess) MakePublic(temporary);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
