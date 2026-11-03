using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.ServerUpdates;

public interface IDockerCommand
{
    Task<string> RunAsync(string[] args, CancellationToken token, Action<string>? output = null, int seconds = 120);
}

public sealed class DockerCommand : IDockerCommand
{
    public async Task<string> RunAsync(string[] args, CancellationToken token, Action<string>? output = null, int seconds = 120)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var process = new Process { StartInfo = new ProcessStartInfo("docker")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (string arg in args) process.StartInfo.ArgumentList.Add(arg);
        var text = new StringBuilder(); int total = 0;
        try
        {
            process.Start();
            async Task Pump(StreamReader reader, bool stdout)
            {
                char[] buffer = new char[4096]; int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    string part = new(buffer, 0, count);
                    lock (text)
                    {
                        total = checked(total + count);
                        if (total > 4 * 1024 * 1024) throw new InvalidDataException("Docker output exceeded limit.");
                        if (stdout) text.Append(part);
                    }
                    output?.Invoke(part);
                }
            }
            // Compose warnings on stderr must never corrupt its JSON/hash output on stdout.
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), Pump(process.StandardOutput, true), Pump(process.StandardError, false));
            if (process.ExitCode != 0) throw new AdminFailure("docker_command_failed", 503, "Docker 操作失败，请查看任务阶段和本机 Docker 日志。");
            return text.ToString().Trim();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}

public sealed record DeploymentIdentity(string ApiContainer, string ComposeImage, string[] Images, string ApiHost);

public static class DockerDeployment
{
    public static async Task<DeploymentIdentity> InspectAsync(IDockerCommand docker, ServerUpdateOptions options,
        string adminRoot, CancellationToken token)
    {
        var ids = new List<string>(); var images = new List<string>(); string? image = null, host = null;
        foreach (string service in new[] { "api", "worker", "notifier", "caddy" })
        {
            string id = await docker.RunAsync(["ps", "-aq", "--filter", "label=com.docker.compose.project=" + options.Project,
                "--filter", "label=com.docker.compose.service=" + service], token);
            if (id.Length == 0 || id.Contains('\n')) throw new AdminFailure("deployment_mismatch", 409, "目标服务缺失或不唯一。");
            using var doc = JsonDocument.Parse(await docker.RunAsync(["inspect", id], token));
            var container = doc.RootElement[0]; var labels = container.GetProperty("Config").GetProperty("Labels");
            if (labels.GetProperty("com.docker.compose.project.working_dir").GetString() != options.DeploymentRoot
                || !container.GetProperty("State").GetProperty("Running").GetBoolean())
                throw new AdminFailure("deployment_mismatch", 409, "目标项目目录或运行状态不符。");
            string Mount(string destination) => container.GetProperty("Mounts").EnumerateArray()
                .Where(m => m.GetProperty("Type").GetString() == "bind" && m.GetProperty("Destination").GetString() == destination)
                .Select(m => m.GetProperty("Source").GetString()!).SingleOrDefault() ?? "";
            if (service == "api" && Mount(adminRoot) != options.DeploymentRoot + "/data/admin"
                || service == "caddy" && Mount("/srv/web") != options.DeploymentRoot + "/server-bundle/web")
                throw new AdminFailure("deployment_mismatch", 409, "数据或 Web 挂载与登记部署不符。");
            if (service != "caddy")
            {
                string reference = container.GetProperty("Config").GetProperty("Image").GetString()!;
                if (!ServerPackageVerifier.Image(reference, options.ApplicationRepository) || image != null && image != reference)
                    throw new AdminFailure("deployment_mismatch", 409, "业务容器镜像引用不一致。");
                image = reference; images.Add(container.GetProperty("Image").GetString()!);
            }
            if (service == "api")
            {
                ids.Add(id);
                host = container.GetProperty("Config").GetProperty("Env").EnumerateArray().Select(e => e.GetString()!)
                    .FirstOrDefault(e => e.StartsWith("AllowedHosts=", StringComparison.Ordinal))?[13..].Split(';')[0];
            }
        }
        if (string.IsNullOrEmpty(host) || host.IndexOfAny(['\r', '\n', '/', ' ']) >= 0 || images.Distinct().Count() != 1)
            throw new AdminFailure("deployment_mismatch", 409, "业务容器版本或管理域名不一致。");
        return new(ids[0], image!, images.ToArray(), host);
    }
}
