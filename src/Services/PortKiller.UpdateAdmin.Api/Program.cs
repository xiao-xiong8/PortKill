using PortKiller.UpdateAdmin.Api;

if (args is ["export-contract", var output])
    await ContractExporter.ExportAsync(output);
else
    await AdminApiHost.Create(args).RunAsync();

public partial class Program;
