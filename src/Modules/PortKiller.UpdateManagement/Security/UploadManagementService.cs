using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class UploadManagementService(UpdateAdminDbContext database, AdminOptions options, TimeProvider clock, ValidationTaskQueue tasks)
{
    private const long ManifestLimit = 65_536;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private string DirectoryFor(UploadSession upload)
    {
        string directory = Path.GetFullPath(Path.Combine(options.StagingPath, upload.StorageId));
        AdminRuntimeLease.RequireLocalPath(directory);
        if (!directory.StartsWith(Path.GetFullPath(options.StagingPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AdminFailure("invalid_storage_path", 500, "上传存储位置无效。");
        return directory;
    }
    private static bool IsAdministrator(ClaimsPrincipal principal) => principal.IsInRole("Administrator");
    private static string UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
    private async Task<UploadSession> OwnedAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        string ownerId = UserId(principal);
        var upload = await database.UploadSessions.SingleOrDefaultAsync(item => item.Id == id
            && (item.OwnerId == ownerId || IsAdministrator(principal)), token);
        if (upload == null) throw new AdminFailure("resource_not_found", 404, "上传会话不存在。");
        return upload;
    }
    private static UploadSessionResponse View(UploadSession upload) => new(upload.Id, upload.State,
        upload.ManifestExpectedBytes, upload.ManifestReceivedBytes, upload.PackageExpectedBytes,
        upload.PackageReceivedBytes, upload.ExpiresUtcMs);

    public async Task<UploadSessionResponse> CreateAsync(ClaimsPrincipal principal, CreateUploadRequest request, CancellationToken token)
    {
        if (request.ManifestBytes is <= 0 or > ManifestLimit || request.PackageBytes is <= 0
            || request.PackageBytes > options.MaxUploadPackageBytes)
            throw new AdminFailure("upload_size_invalid", 400, "清单或更新包大小超出允许范围。");
        long reservation = checked(request.ManifestBytes + request.PackageBytes);
        if (reservation > options.MaxReservedUploadBytesPerUser || reservation > options.MaxReservedUploadBytesTotal)
            throw new AdminFailure("upload_quota_exceeded", 429, "上传预留空间超出账号或服务配额。");
        string userId = UserId(principal); long now = Now;
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        // Only release a completed upload's reservation after its staging payload was removed.
        // Rejected/failed validation records continue to consume quota until their session expires.
        var activeUploads = database.UploadSessions.Where(x => x.ExpiresUtcMs > now)
            .Where(x => x.State == UploadState.Uploading || x.State == UploadState.Uploaded
                && (x.StorageCleanedUtcMs == null || !database.ReleaseDrafts.Any(draft => draft.UploadId == x.Id
                    && draft.ValidationState == ValidationState.ReadyForApproval)));
        long userReserved = await activeUploads.Where(x => x.OwnerId == userId)
            .SumAsync(x => (long?)(x.ManifestExpectedBytes + x.PackageExpectedBytes), token) ?? 0;
        int userCount = await activeUploads.CountAsync(x => x.OwnerId == userId, token);
        long totalReserved = await activeUploads
            .SumAsync(x => (long?)(x.ManifestExpectedBytes + x.PackageExpectedBytes), token) ?? 0;
        int totalCount = await activeUploads.CountAsync(token);
        if (userCount >= options.MaxActiveUploadsPerUser || totalCount >= options.MaxActiveUploadsTotal)
            throw new AdminFailure("upload_quota_exceeded", 429,
                "活动上传数量已达到上限。未完成或校验失败的上传会继续占用名额至取消或 24 小时会话过期。");
        if (userReserved + reservation > options.MaxReservedUploadBytesPerUser
            || totalReserved + reservation > options.MaxReservedUploadBytesTotal)
            throw new AdminFailure("upload_quota_exceeded", 429,
                "活动上传预留空间已达到上限。校验成功且临时文件清理后释放空间；校验失败的上传会占用至会话过期。");
        var upload = new UploadSession
        {
            OwnerId = userId, StorageId = Guid.NewGuid().ToString("N"), State = UploadState.Uploading,
            ManifestExpectedBytes = request.ManifestBytes, PackageExpectedBytes = request.PackageBytes,
            CreatedUtcMs = now, LastActivityUtcMs = now,
            ExpiresUtcMs = now + (long)TimeSpan.FromHours(options.UploadExpirationHours).TotalMilliseconds
        };
        AdminRuntimeLease.EnsureDataDirectory(options.StagingPath);
        string directory = DirectoryFor(upload);
        AdminRuntimeLease.EnsureSharedDirectory(directory);
        try
        {
            database.UploadSessions.Add(upload); await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return View(upload);
        }
        catch { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); throw; }
    }

    public async Task<UploadSessionResponse> GetAsync(ClaimsPrincipal principal, string id, CancellationToken token)
        => View(await OwnedAsync(principal, id, token));

    public async Task<UploadSessionResponse> WritePartAsync(ClaimsPrincipal principal, string id, UploadPart part,
        Stream input, long? contentLength, CancellationToken token)
    {
        var initial = await OwnedAsync(principal, id, token);
        if (initial.State != UploadState.Uploading || initial.ExpiresUtcMs <= Now)
            throw new AdminFailure("upload_closed", 409, "上传会话已关闭或过期。");
        string directory = DirectoryFor(initial);
        string target = Path.Combine(directory, part == UploadPart.Manifest ? "manifest.envelope" : "package.zip");
        string lockPath = Path.Combine(directory, ".session.lock"); AdminRuntimeLease.RequireLocalPath(lockPath);
        await using var sessionLock = AcquireSessionLock(lockPath);
        var upload = await OwnedAsync(principal, id, token);
        if (upload.State != UploadState.Uploading || upload.ExpiresUtcMs <= Now)
            throw new AdminFailure("upload_closed", 409, "上传会话已关闭或过期。");
        long expected = part == UploadPart.Manifest ? upload.ManifestExpectedBytes : upload.PackageExpectedBytes;
        long received = part == UploadPart.Manifest ? upload.ManifestReceivedBytes : upload.PackageReceivedBytes;
        if (received != 0) throw new AdminFailure("upload_part_exists", 409, "该上传文件已接收，请创建新的上传会话。");
        if (File.Exists(target)) { AdminRuntimeLease.RequireLocalPath(target); File.Delete(target); }
        foreach (string orphan in Directory.EnumerateFiles(directory, ".incoming-*.part"))
        { AdminRuntimeLease.RequireLocalPath(orphan); File.Delete(orphan); }
        if (contentLength is < 0 || contentLength > expected)
            throw new AdminFailure("upload_too_large", 413, "上传字节数超过预留大小。");
        string temporary = Path.Combine(directory, ".incoming-" + Guid.NewGuid().ToString("N") + ".part");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            await using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                BufferSize = buffer.Length, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    total = checked(total + count);
                    if (total > expected) throw new AdminFailure("upload_too_large", 413, "上传字节数超过预留大小。");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (total != expected) throw new AdminFailure("upload_incomplete", 400, "上传字节数与预留大小不一致。");
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(token);
                var current = await OwnedAsync(principal, id, token);
                if (current.State != UploadState.Uploading || current.ExpiresUtcMs <= Now)
                    throw new AdminFailure("upload_closed", 409, "上传会话已关闭或过期。");
                if (part == UploadPart.Manifest)
                { current.ManifestReceivedBytes = total; current.ManifestSha256 = Convert.ToHexString(digest.GetHashAndReset()); }
                else
                { current.PackageReceivedBytes = total; current.PackageSha256 = Convert.ToHexString(digest.GetHashAndReset()); }
                current.ReceivedBytes = checked(current.ManifestReceivedBytes + current.PackageReceivedBytes);
                current.LastActivityUtcMs = Now;
                await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
            }
            catch { File.Delete(target); throw; }
            return View(await OwnedAsync(principal, id, token));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<UploadCompletionResponse> CompleteAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        var before = await OwnedAsync(principal, id, token); string directory = DirectoryFor(before);
        string lockPath = Path.Combine(directory, ".session.lock");
        await using var sessionLock = AcquireSessionLock(lockPath);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var upload = await OwnedAsync(principal, id, token);
        if (upload.State == UploadState.Uploaded)
        {
            var existing = await database.ReleaseJobs.AsNoTracking().Where(job => job.Kind == "validate")
                .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { Job = job, Draft = draft })
                .Where(item => item.Draft.UploadId == upload.Id)
                .Select(item => new { DraftId = item.Draft.Id, JobId = item.Job.Id })
                .FirstOrDefaultAsync(token);
            if (existing != null)
            {
                await transaction.CommitAsync(token);
                return new(upload.Id, upload.State, existing.DraftId, existing.JobId);
            }
            throw new AdminFailure("upload_task_missing", 409, "上传状态与验证任务记录不一致。");
        }
        if (upload.State != UploadState.Uploading || upload.ExpiresUtcMs <= Now)
            throw new AdminFailure("upload_closed", 409, "上传会话已关闭或过期。");
        string manifestPath = Path.Combine(directory, "manifest.envelope");
        string packagePath = Path.Combine(directory, "package.zip");
        if (upload.ManifestReceivedBytes != upload.ManifestExpectedBytes || upload.PackageReceivedBytes != upload.PackageExpectedBytes
            || upload.ManifestSha256 == null || upload.PackageSha256 == null
            || !File.Exists(manifestPath) || !File.Exists(packagePath)
            || new FileInfo(manifestPath).Length != upload.ManifestExpectedBytes
            || new FileInfo(packagePath).Length != upload.PackageExpectedBytes)
            throw new AdminFailure("upload_parts_missing", 409, "清单和更新包尚未完整接收。");
        upload.State = UploadState.Uploaded; upload.LastActivityUtcMs = Now;
        var queued = await tasks.EnqueueAsync(upload, token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(upload.Id, upload.State, queued.DraftId, queued.JobId);
    }

    public async Task CancelAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        var before = await OwnedAsync(principal, id, token); string directory = DirectoryFor(before);
        await using var sessionLock = AcquireSessionLock(Path.Combine(directory, ".session.lock"));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var upload = await OwnedAsync(principal, id, token);
        if (upload.State != UploadState.Uploading) throw new AdminFailure("upload_closed", 409, "只有未完成的上传可以取消。");
        upload.State = UploadState.Cancelled; upload.LastActivityUtcMs = Now;
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        DeletePayloadFiles(directory);
    }

    internal static FileStream AcquireSessionLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new AdminFailure("upload_busy", 409, "上传会话正在处理另一个请求。"); }
    }
    internal static void DeletePayloadFiles(string directory)
    {
        foreach (string name in new[] { "manifest.envelope", "package.zip" })
        {
            string path = Path.Combine(directory, name); AdminRuntimeLease.RequireLocalPath(path);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (string path in Directory.EnumerateFiles(directory, ".incoming-*.part"))
        { AdminRuntimeLease.RequireLocalPath(path); File.Delete(path); }
    }
}
