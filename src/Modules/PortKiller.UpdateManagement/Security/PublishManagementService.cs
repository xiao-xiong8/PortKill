using System.Data;
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

/// <summary>Creates one-shot approval challenges and durable publish jobs.</summary>
public sealed class PublishManagementService(
    UpdateAdminDbContext database,
    AdminOptions options,
    TimeProvider clock,
    AdminSessionService sessions,
    AdminAuditService audit)
{
    private const string Operation = "publish.confirm";
    private const string None = "none";
    private const int MaximumIdempotencyKeyLength = 128;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<PublishChallengeResponse> CreateChallengeAsync(
        ClaimsPrincipal principal, string draftId, CancellationToken token)
    {
        SessionTicket ticket = await RequireFreshPublisherAsync(principal, token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        ReleaseDraft draft = await RequireDraftAsync(principal, ticket.User.Id, draftId, token);
        ValidateDraft(draft, Now);
        string baseline = await CurrentBaselineAsync(draft.Application, draft.Channel, token);
        long now = Now;
        var approval = new PublishApproval
        {
            UserId = ticket.User.Id, SessionId = ticket.Session.Id, DraftId = draft.Id,
            ManifestSha256 = NormalizeDigest(draft.ManifestSha256!), PackageSha256 = NormalizeDigest(draft.PackageSha256!),
            Version = draft.Version!, Sequence = draft.Sequence!.Value, ExpectedManifestSha256 = baseline,
            ExpiresUtcMs = checked(now + (long)TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds)
        };
        database.PublishApprovals.Add(approval);
        audit.Stage("publish.challenge_created", draft.Id, actorUserId: ticket.User.Id);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Challenge(approval, draft);
    }

    public async Task<PublishJobResponse> ConfirmAsync(
        ClaimsPrincipal principal, string draftId, PublishConfirmationRequest request,
        string? headerIdempotencyKey, CancellationToken token)
    {
        SessionTicket ticket = await RequireFreshPublisherAsync(principal, token);
        string key = request.IdempotencyKey ?? headerIdempotencyKey ?? "";
        ValidateIdempotencyKey(key);
        string requestHash = Digest(draftId + "\n" + request.ChallengeId);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await ConfirmCoreAsync(ticket, draftId, request.ChallengeId, key, requestHash, token);
            }
            catch (Exception exception) when (IsRetryableConcurrency(exception) && attempt < 2)
            {
                database.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), token);
            }
            catch (Exception exception) when (IsRetryableConcurrency(exception))
            {
                throw new AdminFailure("publish_busy", 409, "发布正在被另一个请求处理，请稍后重试。");
            }
        }
        throw new AdminFailure("publish_busy", 409, "发布正在被另一个请求处理，请稍后重试。");
    }

    public async Task<PublishJobResponse> GetJobAsync(
        ClaimsPrincipal principal, string id, CancellationToken token)
    {
        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        bool administrator = principal.IsInRole("Administrator");
        var row = await database.ReleaseJobs.AsNoTracking()
            .Where(job => job.Id == id && job.Kind == "publish")
            .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
            .SingleOrDefaultAsync(token);
        if (row == null || (!administrator && row.job.RequestedById != userId && row.draft.OwnerId != userId))
            throw new AdminFailure("resource_not_found", 404, "发布任务不存在。");
        return Response(row.job, row.draft);
    }

    internal async Task<string> CurrentBaselineAsync(string application, string channel, CancellationToken token)
        => await CurrentBaselineAsyncCore(application, channel, token);

    private async Task<PublishJobResponse> ConfirmCoreAsync(
        SessionTicket ticket, string draftId, string challengeId, string key, string requestHash,
        CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        long now = Now;
        var existingRequest = await database.IdempotencyRequests.SingleOrDefaultAsync(item => item.UserId == ticket.User.Id
            && item.Operation == Operation && item.Key == key, token);
        if (existingRequest != null)
        {
            if (!existingRequest.RequestSha256.Equals(requestHash, StringComparison.OrdinalIgnoreCase))
                throw new AdminFailure("idempotency_key_reused", 409, "幂等键已经用于另一个发布请求。");
            if (existingRequest.ExpiresUtcMs <= now)
                throw new AdminFailure("idempotency_key_expired", 409, "幂等键已过期，请使用新的幂等键。");
            if (existingRequest.ResultResourceId == null)
                throw new AdminFailure("idempotency_in_progress", 409, "幂等请求正在处理，请稍后重试。");
            var existingJob = await database.ReleaseJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == existingRequest.ResultResourceId
                && job.Kind == "publish", token);
            if (existingJob == null) throw new AdminFailure("idempotency_result_missing", 409, "幂等请求结果不可用，请重新确认发布。");
            var existingDraft = await database.ReleaseDrafts.AsNoTracking().SingleAsync(draft => draft.Id == existingJob.DraftId, token);
            await transaction.CommitAsync(token);
            return Response(existingJob, existingDraft);
        }

        var approval = await database.PublishApprovals.SingleOrDefaultAsync(item => item.Id == challengeId, token);
        if (approval == null || approval.DraftId != draftId || approval.UserId != ticket.User.Id || approval.SessionId != ticket.Session.Id)
            throw new AdminFailure("publish_challenge_binding_mismatch", 409, "发布确认与当前会话或产物不匹配。");
        if (approval.ConsumedUtcMs != null)
            throw new AdminFailure("publish_challenge_used", 409, "发布确认已经使用。");
        if (approval.ExpiresUtcMs <= now)
            throw new AdminFailure("publish_challenge_expired", 409, "发布确认已经过期，请重新确认。");

        var draft = await RequireDraftAsync(principal: null, ticket.User.Id, draftId, token, allowAdministrator: true);
        ValidateDraft(draft, now);
        if (!DigestEquals(draft.ManifestSha256, approval.ManifestSha256)
            || !DigestEquals(draft.PackageSha256, approval.PackageSha256)
            || !string.Equals(draft.Version, approval.Version, StringComparison.Ordinal)
            || draft.Sequence != approval.Sequence)
            throw new AdminFailure("publish_artifact_changed", 409, "已验证产物发生变化，请重新生成发布确认。");

        string currentBaseline = await CurrentBaselineAsyncCore(draft.Application, draft.Channel, token);
        if (!currentBaseline.Equals(approval.ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new AdminFailure("publish_baseline_changed", 409, "当前正式清单已变化，请刷新后重新确认发布。");

        bool active = await database.ReleaseJobs.AnyAsync(job => job.Kind == "publish"
            && job.Application == draft.Application && job.Channel == draft.Channel
            && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing
                || job.State == PublishJobState.ManifestActivated || job.State == PublishJobState.PublicVerifying),
            token);
        if (active) throw new AdminFailure("publish_in_progress", 409, "同一应用和渠道已有发布任务正在处理。");

        approval.ConsumedUtcMs = now;
        var job = new ReleaseJob
        {
            DraftId = draft.Id, RequestedById = ticket.User.Id, Kind = "publish",
            Application = draft.Application, Channel = draft.Channel, State = PublishJobState.Queued,
            ExpectedManifestSha256 = approval.ExpectedManifestSha256, CreatedUtcMs = now
        };
        database.ReleaseJobs.Add(job);
        database.JobEvents.Add(new JobEvent { JobId = job.Id, Sequence = 1, Stage = "queued", Message = "publish_queued", CreatedUtcMs = now });
        database.IdempotencyRequests.Add(new IdempotencyRequest
        {
            UserId = ticket.User.Id, Operation = Operation, Key = key, RequestSha256 = requestHash,
            ResultResourceId = job.Id, CreatedUtcMs = now,
            ExpiresUtcMs = checked(now + (long)TimeSpan.FromDays(7).TotalMilliseconds)
        });
        audit.Stage("publish.confirmed", job.Id, actorUserId: ticket.User.Id);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Response(job, draft);
    }

    private async Task<SessionTicket> RequireFreshPublisherAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        if (!principal.IsInRole("Publisher") && !principal.IsInRole("Administrator")
            && !principal.HasClaim(AdminPermissions.Claim, AdminPermissions.Publish))
            throw new AdminFailure("permission_denied", 403, "当前账号没有发布权限。");
        SessionTicket ticket = await sessions.ValidateAsync(principal, token)
            ?? throw new AdminFailure("authentication_required", 401, "会话已失效。");
        if (ticket.Session.Phase != SessionPhase.Full)
            throw new AdminFailure("authentication_required", 401, "会话尚未完成强认证。");
        long? strongAt = ticket.Session.StrongAuthenticationUtcMs;
        long age = strongAt is null ? long.MaxValue : Now - strongAt.Value;
        if (age < 0 || age >= TimeSpan.FromMinutes(options.StrongAuthenticationMinutes).TotalMilliseconds)
            throw new AdminFailure("recent_authentication_required", 403, "发布需要近期的多因素强认证。");
        return ticket;
    }

    private async Task<ReleaseDraft> RequireDraftAsync(
        ClaimsPrincipal? principal, string userId, string draftId, CancellationToken token, bool allowAdministrator = false)
    {
        bool administrator = allowAdministrator || principal?.IsInRole("Administrator") == true;
        var draft = await database.ReleaseDrafts.SingleOrDefaultAsync(item => item.Id == draftId
            && (item.OwnerId == userId || administrator), token);
        if (draft == null) throw new AdminFailure("resource_not_found", 404, "发布草稿不存在。");
        return draft;
    }

    private static void ValidateDraft(ReleaseDraft draft, long now)
    {
        if (draft.ValidationState != ValidationState.ReadyForApproval)
            throw new AdminFailure("publish_not_ready", 409, "发布草稿尚未通过验证。");
        if (draft.ManifestExpiresUtcMs is not { } expiry || expiry <= now)
            throw new AdminFailure("publish_manifest_expired", 409, "签名清单已过期，请重新准备产物。");
        if (string.IsNullOrWhiteSpace(draft.Version) || draft.Sequence is not > 0
            || !IsDigest(draft.ManifestSha256) || !IsDigest(draft.PackageSha256))
            throw new AdminFailure("publish_metadata_invalid", 409, "发布草稿元数据无效。");
    }

    private async Task<string> CurrentBaselineAsyncCore(string application, string channel, CancellationToken token)
    {
        string? current = await database.ReleaseJobs.AsNoTracking()
            .Where(job => job.Kind == "publish" && job.Application == application && job.Channel == channel
                && (job.State == PublishJobState.ManifestActivated || job.State == PublishJobState.PublicVerifying
                    || job.State == PublishJobState.Completed || job.State == PublishJobState.ActiveUnverified))
            .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft.ManifestSha256 })
            .OrderByDescending(item => item.job.CreatedUtcMs).ThenByDescending(item => item.job.Id)
            .Select(item => item.ManifestSha256)
            .FirstOrDefaultAsync(token);
        return IsDigest(current) ? NormalizeDigest(current!) : None;
    }

    private static PublishChallengeResponse Challenge(PublishApproval approval, ReleaseDraft draft)
        => new(approval.Id, draft.Id, draft.Application, draft.Channel, approval.Version, approval.Sequence,
            approval.ManifestSha256, approval.PackageSha256, Notes(draft), approval.ExpectedManifestSha256, approval.ExpiresUtcMs);

    private static PublishJobResponse Response(ReleaseJob job, ReleaseDraft draft)
        => new(job.Id, draft.Id, job.Application, job.Channel, job.State, draft.Version!, draft.Sequence!.Value,
            NormalizeDigest(draft.ManifestSha256!), NormalizeDigest(draft.PackageSha256!), job.ExpectedManifestSha256 ?? None,
            job.CreatedUtcMs, job.ErrorCode);

    private static string[] Notes(ReleaseDraft draft)
    {
        try { return JsonSerializer.Deserialize<string[]>(draft.ReleaseNotesJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private static void ValidateIdempotencyKey(string key)
    {
        if (key.Length is < 1 or > MaximumIdempotencyKeyLength || key.Any(character => character < 0x21 || character > 0x7e))
            throw new AdminFailure("idempotency_key_invalid", 400, "幂等键格式无效。");
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string NormalizeDigest(string value) => value.ToUpperInvariant();
    private static bool DigestEquals(string? left, string right)
        => left != null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryableConcurrency(Exception exception)
    {
        SqliteException? sqlite = exception switch
        {
            SqliteException direct => direct,
            DbUpdateException { InnerException: SqliteException inner } => inner,
            _ => null
        };
        return sqlite?.SqliteErrorCode is 5 or 6 or 19;
    }
}
