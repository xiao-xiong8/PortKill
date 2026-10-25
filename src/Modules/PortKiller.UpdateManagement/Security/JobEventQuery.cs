using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class JobEventQuery(UpdateAdminDbContext database, AdminSessionService sessions,
    AdminOptions options, TimeProvider clock)
{
    public async Task<JobSnapshotResponse> SnapshotAsync(ClaimsPrincipal principal, string id, CancellationToken token)
        => (await ReadAsync(principal, id, null, 1, token)).Snapshot;

    public async Task<JobEventPageResponse> ReadAsync(ClaimsPrincipal principal, string id, string? after,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new AdminFailure("invalid_pagination", 400, "事件批次大小必须为 1–100。");
        // A deferred, read-only WAL snapshot pairs the task and event watermark without taking the writer lock.
        await database.Database.OpenConnectionAsync(token);
        await using var sqliteTransaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var transaction = await database.Database.UseTransactionAsync(sqliteTransaction, token);
        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        string sessionId = principal.FindFirstValue(AdminIdentity.SessionClaim) ?? "";
        if (!await sessions.IsActiveAsync(sessionId, userId, SessionPhase.Full, token))
            throw new AdminFailure("authentication_required", 401, "会话已失效。");
        string[] roles = await database.UserRoles.Where(link => link.UserId == userId)
            .Join(database.Roles, link => link.RoleId, role => role.Id, (link, role) => role.Name!)
            .ToArrayAsync(token);
        if (!AdminPermissions.ForRoles(roles).Contains(AdminPermissions.Read))
            throw new AdminFailure("permission_denied", 403, "当前账号无权读取任务。");
        var row = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == id
                && (job.Kind == "publish" || job.Kind == "validate") && job.Application == "PortKill" && job.Channel == "stable")
            .Join(database.ReleaseDrafts.AsNoTracking(), job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
            .SingleOrDefaultAsync(token);
        if (row == null || !roles.Contains("Administrator") && row.job.RequestedById != userId && row.draft.OwnerId != userId)
            throw new AdminFailure("resource_not_found", 404, "任务不存在。");
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        PublishJobResponse? publication = null;
        if (row.job.Kind == "publish")
        {
            var approval = await database.PublishApprovals.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == row.job.PublishApprovalId, token)
                ?? throw new AdminFailure("resource_not_found", 404, "任务不存在。");
            publication = PublishManagementService.Response(row.job, approval, now);
        }
        var snapshot = new JobSnapshotResponse(id, row.job.Kind, EventId(id, row.job.LastEventSequence), now,
            publication, row.job.Kind == "validate" ? ValidationTaskQueue.Response(row.job, row.draft) : null);
        long? cursor = ParseEventId(id, after);
        JobEventPageResponse result;
        if (cursor == null || cursor > row.job.LastEventSequence)
            result = Reset(snapshot);
        else
        {
            long cutoff = clock.GetUtcNow().AddDays(-options.JobEventRetentionDays).ToUnixTimeMilliseconds();
            var items = await database.JobEvents.AsNoTracking().Where(item => item.JobId == id
                    && item.Sequence > cursor.Value && item.CreatedUtcMs >= cutoff
                    && item.Sequence > row.job.LastEventSequence - options.JobEventMaximumRowsPerJob)
                .OrderBy(item => item.Sequence).Take(limit).ToArrayAsync(token);
            long next = cursor.Value;
            bool gap = false;
            foreach (var item in items)
            {
                if (next == long.MaxValue || item.Sequence != next + 1) { gap = true; break; }
                next = item.Sequence;
            }
            result = gap || items.Length == 0 && next < row.job.LastEventSequence
                ? Reset(snapshot)
                : new(snapshot, false, EventId(id, next), next < row.job.LastEventSequence,
                    items.Select(item => new JobEventResponse(EventId(id, item.Sequence), item.Stage, item.Message, item.CreatedUtcMs)).ToArray());
        }
        await transaction!.CommitAsync(token);
        return result;
    }

    private static JobEventPageResponse Reset(JobSnapshotResponse snapshot) => new(snapshot, true, snapshot.EventId, false, []);
    public static string EventId(string id, long sequence) => id + ":" + sequence.ToString(CultureInfo.InvariantCulture);

    private static long? ParseEventId(string id, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string prefix = id + ":";
        if (value.Length > prefix.Length + 19 || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            || value != EventId(id, sequence))
            throw new AdminFailure("event_cursor_invalid", 400, "事件标识无效或不属于当前任务。");
        return sequence;
    }
}
