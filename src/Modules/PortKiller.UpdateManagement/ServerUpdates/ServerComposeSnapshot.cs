using System.Text.Json;
using System.Text.Json.Nodes;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class ServerComposeSnapshot
{
    private static string[] Command(ServerUpdateOptions options, string file, params string[] tail) =>
        ["compose", "--project-directory", options.DeploymentRoot, "--env-file", Path.Combine(options.DeploymentRoot, ".env"),
            "-f", file, "-p", options.Project, .. tail];

    public static async Task<JsonNode> CaptureAsync(IDockerCommand docker, ServerUpdateOptions options, string taskPath, CancellationToken token)
    {
        string source = Path.Combine(options.DeploymentRoot, "docker-compose.yml");
        AdminRuntimeLease.RequireLocalPath(source);
        string resolved = await docker.RunAsync(Command(options, source, "config", "--format", "json"), token);
        string hashes = await docker.RunAsync(Command(options, source, "config", "--hash", "*"), token);
        var expected = hashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        foreach (string service in new[] { "api", "worker", "notifier", "caddy" })
        {
            string id = await docker.RunAsync(["ps", "-aq", "--filter", "label=com.docker.compose.project=" + options.Project,
                "--filter", "label=com.docker.compose.service=" + service], token);
            if (string.IsNullOrWhiteSpace(id) || id.Contains('\n')) throw Drift();
            using var info = JsonDocument.Parse(await docker.RunAsync(["inspect", id], token));
            var labels = info.RootElement[0].GetProperty("Config").GetProperty("Labels");
            if (!expected.TryGetValue(service, out string? hash)
                || !labels.TryGetProperty("com.docker.compose.config-hash", out var actual) || actual.GetString() != hash) throw Drift();
        }
        // `compose config` already escapes literal dollars, including in JSON output.
        // Normalize once to actual values before editing bootstrap fields and writing a reloadable snapshot.
        var document = JsonNode.Parse(resolved.Replace("$$", "$", StringComparison.Ordinal)) ?? throw Drift();
        string restore = Path.Combine(taskPath, "compose-restore.json");
        Write(restore, document);
        // Verify the snapshot itself resolves to the configuration just checked, including literal '$' values.
        string frozen = await docker.RunAsync(Execution(options, restore, "config", "--hash", "*"), token);
        if (!HashLines(hashes).SequenceEqual(HashLines(frozen))) throw Drift();
        return document;
    }

    private static string[] HashLines(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Order(StringComparer.Ordinal).ToArray();
    private static AdminFailure Drift() => new("deployment_compose_drift", 409, "Compose 或环境配置与运行中容器不一致，请先核对并恢复部署配置。");

    public static void Write(string path, JsonNode document)
    {
        // Compose interpolates configuration on every load. Match its own escaping, including object keys.
        AdminRuntimeLease.RequireLocalPath(path);
        File.WriteAllText(path, document.ToJsonString().Replace("$", "$$", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static string[] Execution(ServerUpdateOptions options, string snapshot, params string[] tail) =>
        ["compose", "--project-directory", options.DeploymentRoot, "--env-file", "/dev/null", "-f", snapshot, "-p", options.Project, .. tail];

    public static async Task ApplyAsync(IDockerCommand docker, ServerUpdateOptions options, string snapshot, CancellationToken token, Action<string>? output = null)
    {
        AdminRuntimeLease.RequireLocalPath(snapshot);
        if (!File.Exists(snapshot)) throw new InvalidDataException("缺少已核验的 Compose 快照，停止恢复；请通过 SSH 核对部署。");
        await docker.RunAsync(Execution(options, snapshot, "up", "-d", "--force-recreate", "--no-deps", "--no-build", "--pull", "never", "api", "worker", "notifier"), token, output, seconds: 300);
    }
}
