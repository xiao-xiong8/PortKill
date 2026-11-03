using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class ServerPackageVerifier
{
    public static string Fingerprint(IEnumerable<string> migrations) => Convert.ToHexStringLower(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', migrations))));

    public static Task<ServerPackageManifest> InspectAsync(string zip, string publicKey, ServerUpdateOptions options, string databaseFingerprint, CancellationToken token) =>
        ReadAsync(zip, "", publicKey, options, databaseFingerprint, token, false);
    public static Task<ServerPackageManifest> ExtractAsync(string zip, string destination, string publicKey,
        ServerUpdateOptions options, string databaseFingerprint, CancellationToken token) => ReadAsync(zip, destination, publicKey, options, databaseFingerprint, token, true);
    private static async Task<ServerPackageManifest> ReadAsync(string zip, string destination, string publicKey,
        ServerUpdateOptions options, string databaseFingerprint, CancellationToken token, bool extract)
    {
        if (extract) AdminRuntimeLease.RequireLocalPath(destination);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is < 4 or > 10000) throw new InvalidDataException("更新包文件数量无效。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!SafePath(entry.FullName) || !entries.TryAdd(entry.FullName, entry)
                || ((entry.ExternalAttributes >> 16) & 0xF000) is 0xA000 or 0x4000)
                throw new InvalidDataException("更新包含不安全、重复或非普通文件路径。");
            total = checked(total + entry.Length);
            if (total > options.MaximumExpandedBytes) throw new InvalidDataException("更新包展开体积超限。");
        }
        if (!entries.TryGetValue("server-manifest.json", out var manifestEntry) || manifestEntry.Length > 1024 * 1024
            || !entries.TryGetValue("server-manifest.sig", out var signatureEntry) || signatureEntry.Length > 8192)
            throw new InvalidDataException("缺少签名服务器清单。");
        byte[] payload = await BytesAsync(manifestEntry, token), signature = await BytesAsync(signatureEntry, token);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("服务器更新包签名不可信。");
        // Required properties are checked before deserialization: a missing false flag is not an approval.
        using (var json = JsonDocument.Parse(payload))
            foreach (string name in new[] { "schema", "kind", "version", "runtime", "databaseChanges", "databaseFingerprint", "applicationImage", "runnerImage", "files", "dockerBytes" })
                if (!json.RootElement.TryGetProperty(name, out _)) throw new InvalidDataException("服务器清单缺少必要字段。");
        var manifest = JsonSerializer.Deserialize<ServerPackageManifest>(payload, ServerUpdateFiles.Json)
            ?? throw new InvalidDataException("服务器清单为空。");
        _ = PortKiller.Updates.UpdateManifest.ParseVersion(manifest.Version);
        if (manifest.Schema != 1 || manifest.Kind != "portkill-server-update" || manifest.Runtime != "linux-x64"
            || manifest.DatabaseChanges || manifest.DatabaseFingerprint != databaseFingerprint || manifest.DockerBytes is < 1 or > 24L * 1024 * 1024 * 1024
            || !Image(manifest.ApplicationImage, options.ApplicationRepository) || !Image(manifest.RunnerImage, options.RunnerRepository)
            || manifest.Files == null || manifest.Files.Length != entries.Count - 2)
            throw new InvalidDataException("平台、数据库版本、镜像或文件清单不兼容；本期不支持数据库迁移。");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (extract) ServerUpdateStore.EnsureSharedDirectory(destination);
        foreach (var file in manifest.Files)
        {
            if (!SafePath(file.Path) || !listed.Add(file.Path) || !entries.TryGetValue(file.Path, out var entry)
                || file.Bytes < 1 || entry.Length != file.Bytes || !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$")
                || !(file.Path is "images/application.tar.gz" or "images/runner.tar.gz" || file.Path.StartsWith("web/", StringComparison.Ordinal)))
                throw new InvalidDataException("更新文件清单无效。");
            if (!extract) continue;
            string output = Path.Combine(destination, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string directory = destination;
            foreach (string segment in file.Path.Split('/')[..^1])
            { directory = Path.Combine(directory, segment); ServerUpdateStore.EnsureSharedDirectory(directory); }
            AdminRuntimeLease.RequireLocalPath(output);
            await using var input = entry.Open();
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(input, target, file.Bytes, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            await using var stored = File.OpenRead(output);
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stored, token)), file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("服务器更新文件摘要不符。");
        }
        if (!listed.Contains("images/application.tar.gz") || !listed.Contains("images/runner.tar.gz") || !listed.Contains("web/index.html"))
            throw new InvalidDataException("更新包缺少镜像或 Web 首页。");
        if (!extract) return manifest;
        File.WriteAllBytes(Path.Combine(destination, "server-manifest.json"), payload);
        File.WriteAllBytes(Path.Combine(destination, "server-manifest.sig"), signature);
        await DockerArchiveVerifier.VerifyAsync(Path.Combine(destination, "images/application.tar.gz"), manifest.ApplicationImage, options.MaximumExpandedBytes, token);
        await DockerArchiveVerifier.VerifyAsync(Path.Combine(destination, "images/runner.tar.gz"), manifest.RunnerImage, options.MaximumExpandedBytes, token);
        return manifest;
    }

    public static bool Image(string value, string repository) => value != null && value.StartsWith(repository + ":", StringComparison.Ordinal)
        && Regex.IsMatch(value[(repository.Length + 1)..], "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$");
    public static bool SafePath(string? value) => value is { Length: > 0 and <= 240 } && !value.Contains('\\')
        && !value.Contains(':') && !value.StartsWith('/') && value.Split('/').All(s => s.Length > 0 && s is not ("." or "..")
            && Regex.IsMatch(s, "^[a-zA-Z0-9._-]+$"));
    private static async Task<byte[]> BytesAsync(ZipArchiveEntry entry, CancellationToken token)
    { await using var source = entry.Open(); using var output = new MemoryStream(); await CopyBoundedAsync(source, output, entry.Length, token); return output.ToArray(); }
    private static async Task CopyBoundedAsync(Stream source, Stream target, long expected, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024]; long count = 0; int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            count = checked(count + read);
            if (count > expected) throw new InvalidDataException("更新文件展开体积超限。");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (count != expected) throw new InvalidDataException("更新文件长度不符。");
    }
}
