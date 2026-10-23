using System.Text.Json.Serialization;

namespace PortKiller.Regression;

internal sealed record ApplicationNameMeasurement(int Rows, int Samples, double P50, double P95, double P99,
    int Warmup, double UiCommitP95, double UiCommitP99, double MergeP95, double MetadataP95, List<double> Commits);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<ApplicationNameMeasurement>))]
internal partial class ApplicationNameMeasurementContext : JsonSerializerContext;
