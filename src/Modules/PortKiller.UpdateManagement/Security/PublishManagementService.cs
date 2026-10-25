using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class PublishManagementService(UpdateAdminDbContext database, AdminOptions options, TimeProvider clock,
    AdminSessionService sessions, AdminAuditService audit, IPublishedReleaseReader source, JobEventJournal events)
{
    private const string Operation = "publish.confirm";
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<PublishChallengeResponse> CreateChallengeAsync(ClaimsPrincipal principal, string draftId, CancellationToken token)
    {
        string baseline = (await source.ReadAsync(token))?.ManifestSha256 ?? "none";
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        SessionTicket actor = await RequirePublisherAsync(principal, token);
        var draft = await DraftAsync(draftId, token);
        ValidateDraft(draft, Now);
        long strongUntil = actor.Session.StrongAuthenticationUtcMs!.Value
            + (long)TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds;
        var approval = new PublishApproval
        {
            UserId = actor.User.Id, SessionId = actor.Session.Id, DraftId = draft.Id,
            Application = draft.Application, Channel = draft.Channel, Runtime = draft.Runtime, ArchiveId = draft.ArchiveId,
            ManifestSha256 = draft.ManifestSha256!.ToUpperInvariant(), PackageSha256 = draft.PackageSha256!.ToUpperInvariant(),
            Version = draft.Version!, Sequence = draft.Sequence!.Value, ExpectedManifestSha256 = baseline,
            ExpiresUtcMs = Math.Min(Now + 120_000, strongUntil)
        };
        database.PublishApprovals.Add(approval);
        audit.Stage("publish.challenge_created", draft.Id, actorUserId: actor.User.Id);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(approval.Id, draft.Id, draft.Application, draft.Channel, approval.Version, approval.Sequence,
            approval.ManifestSha256, approval.PackageSha256, JsonSerializer.Deserialize<string[]>(draft.ReleaseNotesJson) ?? [],
            baseline, approval.ExpiresUtcMs);
    }

    public async Task<PublishJobResponse> ConfirmAsync(ClaimsPrincipal principal, string draftId,
        PublishConfirmationRequest request, string? headerIdempotencyKey, CancellationToken token)
    {
        string key = request.IdempotencyKey ?? headerIdempotencyKey ?? "";
        if (key.Length is < 1 or > 128 || key.Any(c => c < 0x21 || c > 0x7e)
            || request.IdempotencyKey != null && headerIdempotencyKey != null && request.IdempotencyKey != headerIdempotencyKey)
            throw new AdminFailure("idempotency_key_invalid", 400, "幂等键格式无效或与请求头不一致。");
        if (!Guid.TryParseExact(request.ChallengeId, "N", out _) || !Guid.TryParseExact(draftId, "N", out _))
            throw new AdminFailure("invalid_request", 400, "发布确认标识无效。");
        string requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draftId + "\n" + request.ChallengeId)));
        for (int attempt = 0; ; attempt++)
        {
            try { return await ConfirmCoreAsync(principal, draftId, request.ChallengeId, key, requestHash, token); }
            catch (Exception exception) when (Busy(exception))
            {
                if (attempt >= 2)
                    throw new AdminFailure(UniqueViolation(exception) ? "publish_in_progress" : "publish_busy", 409,
                        UniqueViolation(exception) ? "同一应用和渠道已有发布任务正在处理。" : "发布正在被另一个请求处理，请稍后重试。");
                database.ChangeTracker.Clear();
                await Task.Delay(25 * (attempt + 1), token);
            }
        }
    }

    private async Task<PublishJobResponse> ConfirmCoreAsync(ClaimsPrincipal principal, string draftId, string challengeId,
        string key, string requestHash, CancellationToken token)
    {
        // Read the real signed commit record, including releases made with the original SSH/CLI publisher.
        string baseline = (await source.ReadAsync(token))?.ManifestSha256 ?? "none";
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var actor = await RequirePublisherAsync(principal, token);
        var approval = await database.PublishApprovals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == challengeId, token);
        if (approval == null || approval.DraftId != draftId || approval.UserId != actor.User.Id || approval.SessionId != actor.Session.Id)
            throw new AdminFailure("publish_challenge_binding_mismatch", 409, "发布确认与当前会话或产物不匹配。");

        var previous = await database.IdempotencyRequests.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == actor.User.Id
            && x.Operation == Operation && x.Key == key, token);
        if (previous != null)
        {
            if (previous.RequestSha256 != requestHash)
                throw new AdminFailure("idempotency_key_reused", 409, "幂等键已经用于另一个发布请求。");
            if (previous.ExpiresUtcMs <= Now)
                throw new AdminFailure("idempotency_key_expired", 409, "幂等键已过期，请使用新的幂等键。");
            var result = await database.ReleaseJobs.AsNoTracking().SingleAsync(x => x.Id == previous.ResultResourceId, token);
            return Response(result, approval);
        }

        var job = await database.ReleaseJobs.AsNoTracking().SingleOrDefaultAsync(x => x.PublishApprovalId == approval.Id, token);
        if (job == null)
        {
            if (approval.ConsumedUtcMs != null)
                throw new AdminFailure("publish_challenge_used", 409, "发布确认已经使用。");
            if (approval.ExpiresUtcMs <= Now)
                throw new AdminFailure("publish_challenge_expired", 409, "发布确认已经过期，请重新确认。");
            var draft = await DraftAsync(draftId, token);
            ValidateDraft(draft, Now);
            if (!Matches(draft, approval))
                throw new AdminFailure("publish_artifact_changed", 409, "已验证产物发生变化，请重新生成发布确认。");
            if (baseline != approval.ExpectedManifestSha256)
                throw new AdminFailure("publish_baseline_changed", 409, "当前正式清单已变化，请刷新后重新确认发布。");
            if (await database.ReleaseJobs.AnyAsync(x => x.Kind == "publish" && x.Application == approval.Application
                && x.Channel == approval.Channel && (x.State == PublishJobState.Queued || x.State == PublishJobState.Publishing
                    || x.State == PublishJobState.ManifestActivated || x.State == PublishJobState.PublicVerifying), token))
                throw new AdminFailure("publish_in_progress", 409, "同一应用和渠道已有发布任务正在处理。");
            int consumed = await database.PublishApprovals.Where(x => x.Id == approval.Id && x.ConsumedUtcMs == null && x.ExpiresUtcMs > Now)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedUtcMs, Now), token);
            if (consumed != 1) throw new AdminFailure("publish_challenge_used", 409, "发布确认已经使用。");
            job = new ReleaseJob
            {
                DraftId = draft.Id, PublishApprovalId = approval.Id, RequestedById = actor.User.Id, Kind = "publish",
                Application = approval.Application, Channel = approval.Channel, State = PublishJobState.Queued,
                ExpectedManifestSha256 = baseline, CreatedUtcMs = Now
            };
            database.ReleaseJobs.Add(job);
            await events.AppendAsync(job.Id, "queued", "publish_queued", token);
            audit.Stage("publish.confirmed", job.Id, actorUserId: actor.User.Id);
        }
        database.IdempotencyRequests.Add(new IdempotencyRequest
        {
            UserId = actor.User.Id, Operation = Operation, Key = key, RequestSha256 = requestHash,
            ResultResourceId = job.Id, CreatedUtcMs = Now, ExpiresUtcMs = Now + (long)TimeSpan.FromDays(7).TotalMilliseconds
        });
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return Response(job, approval);
    }

    public async Task<PublishJobResponse> GetJobAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        bool administrator = principal.IsInRole("Administrator");
        var row = await database.ReleaseJobs.AsNoTracking().Where(x => x.Id == id && x.Kind == "publish"
                && x.Application == "PortKill" && x.Channel == "stable")
            .Join(database.PublishApprovals, job => job.PublishApprovalId, approval => approval.Id, (job, approval) => new { job, approval })
            .Join(database.ReleaseDrafts, item => item.job.DraftId, draft => draft.Id,
                (item, draft) => new { item.job, item.approval, OwnerId = draft.OwnerId })
            .SingleOrDefaultAsync(token);
        if (row == null || !administrator && row.job.RequestedById != userId && row.OwnerId != userId)
            throw new AdminFailure("resource_not_found", 404, "发布任务不存在。");
        return Response(row.job, row.approval);
    }

    public async Task<PublishJobResponse> RetryPublicCheckAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await RetryPublicCheckCoreAsync(principal, id, token); }
            catch (Exception exception) when (Busy(exception))
            {
                if (attempt >= 2) throw new AdminFailure("publish_in_progress", 409, "发布任务正在处理，请稍后重试。");
                database.ChangeTracker.Clear();
                await Task.Delay(25 * (attempt + 1), token);
            }
        }
    }

    private async Task<PublishJobResponse> RetryPublicCheckCoreAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        SessionTicket actor = await RequirePublisherAsync(principal, token);
        bool administrator = await database.UserRoles.Where(link => link.UserId == actor.User.Id)
            .Join(database.Roles, link => link.RoleId, role => role.Id, (link, role) => role.Name)
            .AnyAsync(name => name == "Administrator", token);
        var row = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == id && job.Kind == "publish"
                && job.Application == "PortKill" && job.Channel == "stable")
            .Join(database.PublishApprovals, job => job.PublishApprovalId, approval => approval.Id, (job, approval) => new { job, approval })
            .Join(database.ReleaseDrafts, item => item.job.DraftId, draft => draft.Id,
                (item, draft) => new { item.job, item.approval, OwnerId = draft.OwnerId }).SingleOrDefaultAsync(token);
        if (row == null || !administrator && row.job.RequestedById != actor.User.Id && row.OwnerId != actor.User.Id)
            throw new AdminFailure("resource_not_found", 404, "发布任务不存在。");
        // Concurrent clicks and responses lost in transit all return the same original job.
        if (row.job.State is PublishJobState.ManifestActivated or PublishJobState.PublicVerifying or PublishJobState.Completed)
        { await transaction.CommitAsync(token); return Response(row.job, row.approval); }
        if (row.job.State != PublishJobState.ActiveUnverified)
            throw new AdminFailure("public_check_not_retryable", 409, "当前任务不支持重试下载检查。");
        PublishedRelease? current = await source.ReadAsync(token);
        if (current?.ManifestSha256 != row.approval.ManifestSha256)
            throw new AdminFailure("public_check_baseline_changed", 409, "正式清单已经变化，请查看当前版本。");
        if (await database.ReleaseJobs.AnyAsync(job => job.Id != id && job.Kind == "publish"
            && job.Application == row.job.Application && job.Channel == row.job.Channel
            && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing
                || job.State == PublishJobState.ManifestActivated || job.State == PublishJobState.PublicVerifying), token))
            throw new AdminFailure("publish_in_progress", 409, "同一应用和渠道已有发布任务正在处理。");
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.State == PublishJobState.ActiveUnverified)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.PublicVerifying)
                .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                .SetProperty(job => job.ErrorCode, (string?)null).SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) throw new AdminFailure("publish_busy", 409, "任务状态已经变化，请刷新后重试。");
        await events.AppendAsync(id, "public_check_queued", "public_check_retry_requested", token);
        audit.Stage("publish.public_check_retried", id, actorUserId: actor.User.Id);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        row.job.State = PublishJobState.PublicVerifying;
        row.job.LeaseOwner = null; row.job.ErrorCode = null;
        return Response(row.job, row.approval);
    }

    private async Task<SessionTicket> RequirePublisherAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        var actor = await sessions.ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        await database.Entry(actor.User).ReloadAsync(token); await database.Entry(actor.Session).ReloadAsync(token);
        if (!await sessions.IsActiveAsync(actor.Session.Id, actor.User.Id, SessionPhase.Full, token))
            throw new AdminFailure("authentication_required", 401, "会话已失效。");
        if (actor.Session.StrongAuthenticationUtcMs is not { } at || Now < at
            || Now - at >= TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds)
            throw new AdminFailure("recent_authentication_required", 403, "发布需要近期的多因素强认证。");
        bool authorized = await database.UserRoles.Where(x => x.UserId == actor.User.Id)
            .Join(database.Roles, link => link.RoleId, role => role.Id, (link, role) => role.Name)
            .AnyAsync(name => name == "Publisher" || name == "Administrator", token);
        if (!authorized) throw new AdminFailure("permission_denied", 403, "当前账号没有发布权限。");
        return actor;
    }

    private async Task<ReleaseDraft> DraftAsync(string id, CancellationToken token)
        => await database.ReleaseDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.Application == "PortKill" && x.Channel == "stable" && x.Runtime == "win-x64", token)
            ?? throw new AdminFailure("resource_not_found", 404, "发布草稿不存在。");

    private static void ValidateDraft(ReleaseDraft draft, long now)
    {
        if (draft.ValidationState != ValidationState.ReadyForApproval)
            throw new AdminFailure("publish_not_ready", 409, "发布草稿尚未通过验证。");
        if (draft.ManifestExpiresUtcMs is not { } expiry || expiry <= now)
            throw new AdminFailure("publish_manifest_expired", 409, "签名清单已过期，请重新准备产物。");
        if (draft.ArchiveCleanedUtcMs != null || draft.ArchiveCleanupLeaseUtcMs > now)
            throw new AdminFailure("publish_archive_unavailable", 409, "封存产物已清理或正在清理。");
        if (string.IsNullOrWhiteSpace(draft.Version) || draft.Sequence is not > 0
            || !IsDigest(draft.ManifestSha256) || !IsDigest(draft.PackageSha256))
            throw new AdminFailure("publish_metadata_invalid", 409, "发布草稿元数据无效。");
    }

    internal static bool Matches(ReleaseDraft draft, PublishApproval approval)
        => draft.Application == approval.Application && draft.Channel == approval.Channel && draft.Runtime == approval.Runtime
            && draft.ArchiveId == approval.ArchiveId && draft.Version == approval.Version && draft.Sequence == approval.Sequence
            && string.Equals(draft.ManifestSha256, approval.ManifestSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(draft.PackageSha256, approval.PackageSha256, StringComparison.OrdinalIgnoreCase);

    private PublishJobResponse Response(ReleaseJob job, PublishApproval approval)
        => Response(job, approval, Now);

    internal static PublishJobResponse Response(ReleaseJob job, PublishApproval approval, long now)
        => new(job.Id, job.DraftId, job.Application, job.Channel, job.State, approval.Version, approval.Sequence,
            approval.ManifestSha256, approval.PackageSha256, approval.ExpectedManifestSha256, job.CreatedUtcMs, job.ErrorCode,
            job.ActivatedUtcMs, CurrentReleaseService.Check(job, now));

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Busy(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 }
            or SqliteException { SqliteExtendedErrorCode: 2067 }
            or DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 or 19 } };

    private static bool UniqueViolation(Exception exception)
        => exception is SqliteException { SqliteExtendedErrorCode: 2067 }
            or DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: 2067 } };
}
