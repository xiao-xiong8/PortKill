using PortKiller.UpdateAdmin.Api;
using PortKiller.UpdateAdmin.Api.Commands;

if (args.Length > 0 && args[0] is "db" or "admin")
    Environment.ExitCode = await AdminCli.RunAsync(args);
else if (args is ["export-contract", var output])
    await ContractExporter.ExportAsync(output);
else if (args is ["export-server-schema", var schemaOutput])
{
    var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PortKiller.UpdateManagement.Persistence.UpdateAdminDbContext>();
    Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(options, "Data Source=:memory:");
    using var database = new PortKiller.UpdateManagement.Persistence.UpdateAdminDbContext(options.Options);
    var migrations = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetMigrations(database.Database);
    await File.WriteAllTextAsync(schemaOutput, PortKiller.UpdateManagement.ServerUpdates.ServerPackageVerifier.Fingerprint(migrations));
}
else
    await AdminApiHost.Create(args).RunAsync();

public partial class Program;
