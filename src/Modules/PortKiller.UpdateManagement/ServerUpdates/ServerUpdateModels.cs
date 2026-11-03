using System.Text.Json;
using System.Text.RegularExpressions;
using PortKiller.UpdateManagement.Configuration;

namespace PortKiller.UpdateManagement.ServerUpdates;

public sealed class ServerUpdateOptions
{
    public bool Enabled { get; set; }
    public string DeploymentRoot { get; set; } = "";
    public string Project { get; set; } = "";
    public string ApplicationRepository { get; set; } = "portkill-update-server";
    public string RunnerRepository { get; set; } = "portkill-server-update-runner";
    public string VerificationConfigPath { get; set; } = "/etc/portkill/verification.json";
    public long MaximumPackageBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaximumExpandedBytes { get; set; } = 6L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 1024L * 1024 * 1024;
    public int DrainSeconds { get; set; } = 600;
    public int HealthSeconds { get; set; } = 120;
    public void Validate()
    {
        if (!Enabled) return;
        if (!DeploymentRoot.StartsWith('/') || DeploymentRoot == "/" || DeploymentRoot.Contains("..")
            || !Regex.IsMatch(Project, "^[a-z0-9][a-z0-9_-]{0,62}$")
            || !Regex.IsMatch(ApplicationRepository, "^[a-z0-9][a-z0-9._/-]{0,127}$")
            || !Regex.IsMatch(RunnerRepository, "^[a-z0-9][a-z0-9._/-]{0,127}$")
            || !Path.IsPathFullyQualified(VerificationConfigPath)
            || MaximumPackageBytes is < 1 or > 8L * 1024 * 1024 * 1024
            || MaximumExpandedBytes < MaximumPackageBytes || MaximumExpandedBytes > 24L * 1024 * 1024 * 1024
            || MinimumFreeBytes < 64L * 1024 * 1024 || DrainSeconds is < 1 or > 900 || HealthSeconds is < 1 or > 300)
            throw new InvalidOperationException("ServerUpdate configuration is invalid.");
    }
}

public sealed record ServerPackageFile(string Path, long Bytes, string Sha256);
public sealed record ServerPackageManifest(int Schema, string Kind, string Version, string Runtime, bool DatabaseChanges,
    string DatabaseFingerprint, string ApplicationImage, string RunnerImage, ServerPackageFile[] Files, long DockerBytes);
public sealed record ServerPackage(string Id, string Version, string ApplicationImage, string RunnerImage, long Bytes, long CreatedUtcMs);
public sealed record ServerUpdateEvent(long Seq, long AtUtcMs, string Message);
public sealed record ServerUpdateTask(string Id, string PackageId, string Version, string ActorId, string State,
    string Stage, long UpdatedUtcMs, string? Error = null, string? RollbackOf = null)
{
    public bool Terminal => State is "succeeded" or "failed" or "rolledBack" or "recoveryRequired";
}
public sealed record ServerUpdateSummary(bool Enabled, long MaximumPackageBytes, string CurrentVersion,
    ServerPackage[] Packages, ServerUpdateTask[] Tasks);
public sealed record RunnerRequest(ServerUpdateOptions Options, string DataRoot, string TaskId, string PackageId,
    string[] PreviousImages, string ComposeImage, string ApiHost, string DatabaseFingerprint, string? RollbackOf = null,
    bool Bootstrap = false, string? DockerGroup = null, string PreviousVersion = "unknown");

public static class ServerUpdateFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static string Root(AdminOptions options) => Path.Combine(options.DataRoot, "server-updates");
    public static string Pending(AdminOptions options) => Path.Combine(Root(options), "pending");
    public static bool IsPending(AdminOptions options) => File.Exists(Pending(options));
    public static void SetPending(AdminOptions options, string id)
    {
        Id(id); string path = Pending(options), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Persistence.AdminRuntimeLease.RequireLocalPath(path);
        try { File.WriteAllText(temporary, id); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Id(string value) => Regex.IsMatch(value, "^[a-f0-9]{32}$") ? value
        : throw new Security.AdminFailure("server_update_id_invalid", 400, "更新编号无效。");
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException("更新记录不完整。");
    public static void Write<T>(string path, T value)
    {
        Persistence.AdminRuntimeLease.RequireLocalPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
