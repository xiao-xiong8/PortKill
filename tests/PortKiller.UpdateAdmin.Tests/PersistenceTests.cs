using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PersistenceTests
{
    private static UpdateAdminDbContext Open(string root)
        => new(new DbContextOptionsBuilder<UpdateAdminDbContext>().UseSqlite(AdminDatabase.ConnectionString(new AdminOptions { DataRoot = root })).Options);

    [Fact]
    public async Task MigrationEnablesWalForeignKeysAndBoundedWaiting()
    {
        await using var sandbox = new TestApplication();
        await using var database = Open(sandbox.Root);
        await AdminDatabase.MigrateAsync(database);
        await database.Database.OpenConnectionAsync();
        foreach (var (pragma, expected) in new[] { ("journal_mode", "wal"), ("foreign_keys", "1"), ("busy_timeout", "5000") })
        {
            using var command = database.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA " + pragma;
            Assert.Equal(expected, Convert.ToString(await command.ExecuteScalarAsync()));
        }
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        using var versionCommand = database.Database.GetDbConnection().CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version();";
        var nativeVersion = Version.Parse((string)(await versionCommand.ExecuteScalarAsync())!);
        Assert.True(nativeVersion >= new Version(3, 51, 3), $"Native SQLite {nativeVersion} predates the WAL-reset fix");
    }

    [Fact]
    public async Task ForeignKeysRejectUnknownOwners()
    {
        await using var sandbox = new TestApplication();
        await using var database = Open(sandbox.Root);
        await AdminDatabase.MigrateAsync(database);
        database.UploadSessions.Add(new UploadSession { OwnerId = "missing", State = UploadState.Uploading });
        Assert.IsType<SqliteException>((await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync())).InnerException);
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteNewerUploadState()
    {
        await using var sandbox = new TestApplication();
        await using var first = Open(sandbox.Root);
        await AdminDatabase.MigrateAsync(first);
        first.Users.Add(new AdminUser { Id = "owner", UserName = "owner" });
        var upload = new UploadSession { OwnerId = "owner", State = UploadState.Uploading };
        first.UploadSessions.Add(upload); await first.SaveChangesAsync();
        await using var second = Open(sandbox.Root);
        var stale = await second.UploadSessions.SingleAsync();
        upload.State = UploadState.Uploaded; await first.SaveChangesAsync();
        stale.State = UploadState.Cancelled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task DatabaseRejectsTwoActivePublicationsForTheSameChannel()
    {
        await using var sandbox = new TestApplication();
        await using var database = Open(sandbox.Root);
        await AdminDatabase.MigrateAsync(database);
        database.Users.Add(new AdminUser { Id = "owner", UserName = "owner" });
        var upload = new UploadSession { OwnerId = "owner" };
        var draft = new ReleaseDraft { OwnerId = "owner", UploadId = upload.Id };
        database.UploadSessions.Add(upload); database.ReleaseDrafts.Add(draft);
        await database.SaveChangesAsync();
        database.ReleaseJobs.Add(new ReleaseJob { DraftId = draft.Id, RequestedById = "owner", Kind = "publish", State = PublishJobState.Publishing });
        await database.SaveChangesAsync();
        database.ReleaseJobs.Add(new ReleaseJob { DraftId = draft.Id, RequestedById = "owner", Kind = "publish", State = PublishJobState.Publishing });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        await using var verifier = Open(sandbox.Root);
        Assert.Equal(1, await verifier.ReleaseJobs.CountAsync());
    }
}
