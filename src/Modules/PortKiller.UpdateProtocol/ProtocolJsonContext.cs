using System.Text.Json.Serialization;

namespace PortKiller.Updates;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SignedUpdate))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ReleaseHint))]
[JsonSerializable(typeof(ManifestVerificationOptions))]
public partial class ProtocolJsonContext : JsonSerializerContext { }

public sealed record ReleaseHint([property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sequence")] long Sequence);
