using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.Updates;

namespace PortKiller.UpdateManagement.Security;

public sealed record PublishedRelease(string Version, long Sequence, string ManifestSha256, string PackageSha256,
    long ExpiresUtcMs);

public interface IPublishedReleaseReader
{
    Task<PublishedRelease?> ReadAsync(CancellationToken token);
}

/// <summary>API reads the signed commit record with read-only access to the distribution source.</summary>
public sealed class PublishedReleaseReader(UpdateToolOptions options) : IPublishedReleaseReader
{
    public async Task<PublishedRelease?> ReadAsync(CancellationToken token)
    {
        try
        {
            options.ValidateSource();
            AdminRuntimeLease.RequireLocalPath(options.ReleaseRoot);
            AdminRuntimeLease.RequireLocalPath(options.VerificationConfigPath);
            if (!File.Exists(options.VerificationConfigPath)) throw new IOException();
            var verification = JsonSerializer.Deserialize(await File.ReadAllTextAsync(options.VerificationConfigPath, token),
                ProtocolJsonContext.Default.ManifestVerificationOptions) ?? throw new InvalidDataException();
            verification.Validate();
            string path = Path.Combine(options.ReleaseRoot, "manifest.json");
            AdminRuntimeLease.RequireLocalPath(path);
            if (!File.Exists(path)) return null;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > 65_536) throw new InvalidDataException();
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096]; int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                if (output.Length + count > 65_536) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            byte[] bytes = output.ToArray();
            var manifest = UpdateManifest.Verify(bytes, verification, ignoreExpiry: true);
            return new(manifest.Version, manifest.Sequence, Convert.ToHexString(SHA256.HashData(bytes)),
                manifest.Sha256.ToUpperInvariant(), manifest.ExpiresUtc.ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or CryptographicException or FormatException or AdminFailure)
        { throw new AdminFailure("release_source_unavailable", 503, "无法核对正式分发源，请检查发布配置或稍后重试。"); }
    }
}
