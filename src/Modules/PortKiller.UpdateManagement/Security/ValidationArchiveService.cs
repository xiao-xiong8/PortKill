using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record ArchivedValidationInput(string ArchiveId, string ManifestPath, string PackagePath,
    string ManifestSha256, string PackageSha256);

public sealed class ValidationArchiveService(AdminOptions options)
{
    public ArchivedValidationInput Archive(UploadSession upload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string source = Path.Combine(options.StagingPath, upload.StorageId);
        string archive = Path.Combine(options.ArchivePath, upload.StorageId);
        AdminRuntimeLease.RequireLocalPath(source); AdminRuntimeLease.RequireLocalPath(archive);
        if (!Directory.Exists(source)) throw new AdminFailure("upload_storage_missing", 409, "上传暂存文件不存在。");
        AdminRuntimeLease.EnsureDataDirectory(options.ArchivePath);
        if (Directory.Exists(archive))
        {
            AdminRuntimeLease.RequireLocalPath(archive);
            string existingManifest = Path.Combine(archive, "manifest.envelope");
            string existingPackage = Path.Combine(archive, "package.zip");
            if (File.Exists(existingManifest) && File.Exists(existingPackage))
            {
                string existingManifestHash = Hash(existingManifest);
                string existingPackageHash = Hash(existingPackage);
                if (existingManifestHash.Equals(upload.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                    && existingPackageHash.Equals(upload.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    return new(upload.StorageId, existingManifest, existingPackage, existingManifestHash, existingPackageHash);
                throw new AdminFailure("archive_conflict", 409, "封存副本与上传摘要不一致。");
            }
            Directory.Delete(archive, recursive: true);
        }
        Directory.CreateDirectory(archive);
        string manifestSource = Path.Combine(source, "manifest.envelope");
        string packageSource = Path.Combine(source, "package.zip");
        string manifestTarget = Path.Combine(archive, "manifest.envelope");
        string packageTarget = Path.Combine(archive, "package.zip");
        CopyImmutable(manifestSource, manifestTarget, token);
        CopyImmutable(packageSource, packageTarget, token);
        string manifestHash = Hash(manifestTarget);
        string packageHash = Hash(packageTarget);
        if (!manifestHash.Equals(upload.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            || !packageHash.Equals(upload.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new AdminFailure("archive_source_changed", 409, "上传源文件在封存期间发生变化。");
        MakePrivate(archive);
        return new(upload.StorageId, manifestTarget, packageTarget, manifestHash, packageHash);
    }

    public string PathFor(string archiveId, string fileName)
    {
        if (archiveId.Length is < 1 or > 64 || archiveId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new AdminFailure("invalid_storage_path", 400, "封存标识无效。");
        if (fileName is not ("manifest.envelope" or "package.zip"))
            throw new AdminFailure("invalid_storage_path", 400, "封存文件无效。");
        string path = Path.Combine(options.ArchivePath, archiveId, fileName);
        AdminRuntimeLease.RequireLocalPath(path);
        string root = Path.GetFullPath(options.ArchivePath) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new AdminFailure("invalid_storage_path", 400, "封存位置无效。");
        return path;
    }

    private static void CopyImmutable(string source, string target, CancellationToken token)
    {
        AdminRuntimeLease.RequireLocalPath(source); AdminRuntimeLease.RequireLocalPath(target);
        if (!File.Exists(source)) throw new AdminFailure("upload_storage_missing", 409, "上传暂存文件不存在。");
        string temporary = target + ".copy-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                input.CopyTo(output, 64 * 1024); token.ThrowIfCancellationRequested(); output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
            foreach (string file in Directory.EnumerateFiles(path)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
