using System.Text.Json.Serialization;

namespace PortKiller.Updates;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateOptions))]
[JsonSerializable(typeof(SignedUpdate))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(UpdateJournal))]
[JsonSerializable(typeof(TrustedRelease))]
internal partial class UpdateJsonContext : JsonSerializerContext { }
