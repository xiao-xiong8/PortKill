using System.Text.Json;
using PortKiller.Updates;
using PortKiller.UpdateNotifier;

if (args.Length != 2) { Console.Error.WriteLine("Usage: PortKiller.UpdateNotifier <public-root> <verification-config>"); return 2; }
var options = JsonSerializer.Deserialize(File.ReadAllText(args[1]), ProtocolJsonContext.Default.ManifestVerificationOptions)
    ?? throw new InvalidDataException("Verification config missing");
await NotifierHost.Create(args[0], options).RunAsync();
return 0;
