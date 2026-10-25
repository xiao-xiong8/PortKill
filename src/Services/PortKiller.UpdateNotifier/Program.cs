using System.Text.Json;
using PortKiller.Updates;
using PortKiller.UpdateNotifier;

if (args.Length is < 2 or > 3) { Console.Error.WriteLine("Usage: PortKiller.UpdateNotifier <public-root> <verification-config> [listen-url]"); return 2; }
var options = JsonSerializer.Deserialize(File.ReadAllText(args[1]), ProtocolJsonContext.Default.ManifestVerificationOptions)
    ?? throw new InvalidDataException("Verification config missing");
await NotifierHost.Create(args[0], options, args.Length == 3 ? args[2] : "http://127.0.0.1:8091").RunAsync();
return 0;
