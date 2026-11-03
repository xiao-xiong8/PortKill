using System.Globalization;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class ServerUpdateSpace
{
    public static long BackupBytes(AdminOptions admin)
    {
        long bytes = 0;
        foreach (string file in new[] { admin.DatabasePath, admin.DatabasePath + "-wal" }
            .Concat(Directory.Exists(admin.DataProtectionPath) ? Directory.GetFiles(admin.DataProtectionPath, "*.xml") : []))
        {
            AdminRuntimeLease.RequireLocalPath(file);
            if (File.Exists(file)) bytes = checked(bytes + new FileInfo(file).Length);
        }
        return bytes;
    }
    public static long Required(ServerPackageManifest manifest, AdminOptions admin, long minimum, long webBytes = 0) =>
        checked(manifest.Files.Sum(file => file.Bytes) * 2 + manifest.DockerBytes * 2 + BackupBytes(admin) * 2 + webBytes * 3 + minimum);

    public static async Task<string> DockerRootAsync(IDockerCommand docker, CancellationToken token)
    {
        string path = await docker.RunAsync(["info", "--format", "{{.DockerRootDir}}"], token);
        if (!path.StartsWith('/') || path == "/" || path.IndexOfAny([':', ',', '\r', '\n']) >= 0)
            throw new InvalidDataException("Invalid Docker data-root.");
        return path;
    }
    public static async Task EnsureDockerAsync(IDockerCommand docker, DeploymentIdentity identity, string dockerRoot, long required, CancellationToken token)
    {
        // Use the already installed application image, before importing any runner. No downloaded helper or shell.
        string result = await docker.RunAsync(["run", "--rm", "--network", "none", "--read-only", "--user", "0:0", "--pull", "never",
            "--mount", "type=bind,src=" + dockerRoot + ",dst=/docker-storage,readonly", "--entrypoint", "/bin/df",
            identity.Images[0], "--block-size=1", "--output=avail", "/docker-storage"], token);
        if (!long.TryParse(result.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long free))
            throw new AdminFailure("server_update_space_unknown", 409, "无法确认 Docker 数据盘剩余空间，已停止更新。");
        if (free < required) throw new AdminFailure("server_update_space", 409, "Docker 数据盘空间不足，尚未备份或导入镜像。");
    }
}
