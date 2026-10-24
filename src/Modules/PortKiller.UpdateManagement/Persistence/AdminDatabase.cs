using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;

namespace PortKiller.UpdateManagement.Persistence;

public static class AdminDatabase
{
    public static string ConnectionString(AdminOptions options)
        => new SqliteConnectionStringBuilder
        { DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, DefaultTimeout = 5 }.ToString();

    public static IServiceCollection AddAdminDatabase(this IServiceCollection services, AdminOptions options)
        => services.AddDbContext<UpdateAdminDbContext>(builder => builder.UseSqlite(ConnectionString(options)));

    public static async Task MigrateAsync(UpdateAdminDbContext database, CancellationToken token = default)
    {
        string directory = Path.GetDirectoryName(database.Database.GetDbConnection().DataSource)!;
        AdminRuntimeLease.EnsureDataDirectory(directory);
        await database.Database.MigrateAsync(token);
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", token);
        await database.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", token);
        await database.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", token);
    }
}

public sealed class AdminDbContextFactory : IDesignTimeDbContextFactory<UpdateAdminDbContext>
{
    public UpdateAdminDbContext CreateDbContext(string[] args)
    {
        var options = new AdminOptions(); options.Validate();
        return new(new DbContextOptionsBuilder<UpdateAdminDbContext>().UseSqlite(AdminDatabase.ConnectionString(options)).Options);
    }
}
