using PortKiller.UpdateAdmin.Api;
using PortKiller.UpdateAdmin.Api.Commands;

if (args.Length > 0 && args[0] is "db" or "admin")
    Environment.ExitCode = await AdminCli.RunAsync(args);
else if (args is ["export-contract", var output])
    await ContractExporter.ExportAsync(output);
else
    await AdminApiHost.Create(args).RunAsync();

public partial class Program;
