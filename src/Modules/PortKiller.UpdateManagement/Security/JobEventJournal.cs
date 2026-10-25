using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

/// <summary>All event writes share the transaction that changes the task. Retention never resets its counter.</summary>
public sealed class JobEventJournal(UpdateAdminDbContext database, AdminOptions options, TimeProvider clock)
{
    public async Task AppendAsync(string id, string stage, string code, CancellationToken token)
    {
        ValidateIdentifier(stage, 40); ValidateIdentifier(code, 80);
        if (database.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Task events require the state transition's transaction.");
        long sequence;
        var added = database.ChangeTracker.Entries<ReleaseJob>()
            .SingleOrDefault(entry => entry.Entity.Id == id && entry.State == EntityState.Added);
        if (added != null) sequence = added.Entity.LastEventSequence = 1;
        else
        {
            int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.LastEventSequence < long.MaxValue)
                .ExecuteUpdateAsync(update => update.SetProperty(job => job.LastEventSequence, job => job.LastEventSequence + 1), token);
            if (changed != 1) throw new InvalidOperationException("Task event counter is unavailable.");
            sequence = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == id)
                .Select(job => job.LastEventSequence).SingleAsync(token);
        }
        database.JobEvents.Add(new JobEvent { JobId = id, Sequence = sequence, Stage = stage, Message = code,
            CreatedUtcMs = clock.GetUtcNow().ToUnixTimeMilliseconds() });
        await database.SaveChangesAsync(token);
        await PruneAsync(token);
    }

    public async Task PruneAsync(CancellationToken token)
    {
        await database.JobEvents.Where(item => database.ReleaseJobs.Any(job => job.Id == item.JobId
            && item.Sequence <= job.LastEventSequence - options.JobEventMaximumRowsPerJob)).ExecuteDeleteAsync(token);
        long cutoff = clock.GetUtcNow().AddDays(-options.JobEventRetentionDays).ToUnixTimeMilliseconds();
        await database.JobEvents.Where(item => item.CreatedUtcMs < cutoff).ExecuteDeleteAsync(token);
        await database.JobEvents.OrderByDescending(item => item.CreatedUtcMs).ThenBy(item => item.JobId)
            .ThenByDescending(item => item.Sequence).Skip(options.JobEventMaximumRows).ExecuteDeleteAsync(token);
    }

    private static void ValidateIdentifier(string value, int maximum)
    {
        if (value.Length < 1 || value.Length > maximum || value.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Task events accept bounded identifiers only.");
    }
}
