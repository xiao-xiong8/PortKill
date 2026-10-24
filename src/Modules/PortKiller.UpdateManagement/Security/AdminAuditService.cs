using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class AdminAuditService(UpdateAdminDbContext database, IHttpContextAccessor accessor, TimeProvider clock, AdminOptions options)
{
    // Call inside the same transaction as a security change. No request bodies, query strings or exception text are accepted.
    public void Stage(string action, string? resourceId, string result = "success", string? actorUserId = null)
    {
        if (!SafeLabel(action, 80) || !SafeLabel(result, 80)) throw new InvalidOperationException("Audit labels must be bounded identifiers.");
        var context = accessor.HttpContext;
        database.AuditEvents.Add(new AuditEvent
        {
            Action = action, ResourceId = Bounded(resourceId, 128), Result = result,
            ActorUserId = Bounded(actorUserId ?? context?.User.FindFirstValue(ClaimTypes.NameIdentifier), 128),
            CorrelationId = Bounded(Activity.Current?.Id ?? context?.TraceIdentifier ?? Guid.NewGuid().ToString("N"), 128)!,
            RemoteAddress = Bounded(context?.Connection.RemoteIpAddress?.ToString(), 64),
            CreatedUtcMs = clock.GetUtcNow().ToUnixTimeMilliseconds()
        });
    }
    private static string? Bounded(string? value, int maximum) => value == null ? null : value[..Math.Min(value.Length, maximum)];
    private static bool SafeLabel(string value, int maximum) => value.Length is > 0 && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || "._-".Contains(character));

    public async Task RecordRequestAsync(string operation, string? resourceId, int status, string? actor, CancellationToken token)
    {
        Stage("http." + operation, resourceId, "status_" + status, actor);
        await database.SaveChangesAsync(token);
        await PruneAsync(token);
    }

    public async Task<PageResponse<AuditEvent>> ListAsync(int page, int pageSize, string? action, CancellationToken token)
    {
        if (page is < 1 or > 10000 || pageSize is < 1 or > 100 || action != null && !SafeLabel(action, 80))
            throw new AdminFailure("invalid_pagination", 400, "分页或筛选参数超出范围。");
        var query = database.AuditEvents.AsNoTracking();
        if (action != null) query = query.Where(x => x.Action == action);
        long total = await query.LongCountAsync(token);
        var records = await query.OrderByDescending(x => x.CreatedUtcMs).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        return new(records, page, pageSize, total);
    }

    public async Task PruneAsync(CancellationToken token)
    {
        long cutoff = clock.GetUtcNow().AddDays(-options.AuditRetentionDays).ToUnixTimeMilliseconds();
        await database.AuditEvents.Where(x => x.CreatedUtcMs < cutoff).OrderBy(x => x.CreatedUtcMs).ThenBy(x => x.Id).Take(500).ExecuteDeleteAsync(token);
        long excess = await database.AuditEvents.LongCountAsync(token) - options.AuditMaximumRows;
        if (excess > 0)
            await database.AuditEvents.OrderBy(x => x.CreatedUtcMs).ThenBy(x => x.Id).Take((int)Math.Min(excess, 500)).ExecuteDeleteAsync(token);
    }
}
