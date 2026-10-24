using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Persistence;

public sealed class UpdateAdminDbContext(DbContextOptions<UpdateAdminDbContext> options)
    : IdentityDbContext<AdminUser, IdentityRole, string>(options)
{
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();
    public DbSet<TotpConsumption> TotpConsumptions => Set<TotpConsumption>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<ReleaseDraft> ReleaseDrafts => Set<ReleaseDraft>();
    public DbSet<ReleaseJob> ReleaseJobs => Set<ReleaseJob>();
    public DbSet<PublishApproval> PublishApprovals => Set<PublishApproval>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<IdempotencyRequest> IdempotencyRequests => Set<IdempotencyRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AdminSession>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.Phase).HasConversion<string>();
            entity.HasIndex(x => new { x.UserId, x.RevokedUtcMs, x.AbsoluteExpiresUtcMs });
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<TotpConsumption>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.Fingerprint }); entity.HasIndex(x => x.ExpiresUtcMs);
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UploadSession>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.State).HasConversion<string>();
            entity.HasIndex(x => x.StorageId).IsUnique(); entity.HasIndex(x => new { x.State, x.ExpiresUtcMs });
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReleaseDraft>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.ValidationState).HasConversion<string>();
            entity.HasIndex(x => x.UploadId).IsUnique(); entity.HasIndex(x => x.ArchiveId).IsUnique(); entity.HasIndex(x => new { x.CreatedUtcMs, x.Id });
            entity.HasOne<UploadSession>().WithMany().HasForeignKey(x => x.UploadId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ReleaseJob>(entity =>
        {
            entity.HasKey(x => x.Id); entity.Property(x => x.State).HasConversion<string>();
            entity.HasIndex(x => new { x.State, x.LeaseExpiresUtcMs, x.CreatedUtcMs });
            entity.HasIndex(x => new { x.Application, x.Channel }).IsUnique()
                .HasFilter("\"Kind\" = 'publish' AND \"State\" IN ('Queued','Publishing','ManifestActivated','PublicVerifying')");
            entity.HasOne<ReleaseDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PublishApproval>().WithMany().HasForeignKey(x => x.PublishApprovalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.PublishApprovalId).IsUnique();
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.RequestedById).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<PublishApproval>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => x.ExpiresUtcMs);
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AdminSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReleaseDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobEvent>(entity =>
        {
            entity.HasKey(x => new { x.JobId, x.Sequence });
            entity.HasOne<ReleaseJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.Message).HasMaxLength(1000);
        });
        builder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.CreatedUtcMs, x.Id });
            entity.Property(x => x.Action).HasMaxLength(80); entity.Property(x => x.Result).HasMaxLength(80);
            entity.Property(x => x.CorrelationId).HasMaxLength(128); entity.Property(x => x.RemoteAddress).HasMaxLength(64);
        });
        builder.Entity<IdempotencyRequest>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.Operation, x.Key }); entity.HasIndex(x => x.ExpiresUtcMs);
            entity.HasOne<AdminUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var type in builder.Model.GetEntityTypes().Where(x => typeof(IVersionedEntity).IsAssignableFrom(x.ClrType)))
            builder.Entity(type.ClrType).Property(nameof(IVersionedEntity.Revision)).IsConcurrencyToken();
    }

    private void AdvanceRevisions()
    {
        foreach (var entry in ChangeTracker.Entries<IVersionedEntity>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.Revision = entry.State == EntityState.Added ? 1 : entry.Property(x => x.Revision).OriginalValue + 1;
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess) { AdvanceRevisions(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken token = default)
    { AdvanceRevisions(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, token); }
}
