using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.ServerUpdates;

public sealed class ServerUpdateStore(AdminOptions admin)
{
    public string Root { get; } = ServerUpdateFiles.Root(admin);
    public string PackagePath(string id) => Path.Combine(Root, "packages", ServerUpdateFiles.Id(id));
    public string TaskPath(string id) => Path.Combine(Root, "tasks", ServerUpdateFiles.Id(id));
    public void Ensure()
    {
        foreach (string path in new[] { Root, Path.Combine(Root, "packages"), Path.Combine(Root, "tasks") })
            EnsureSharedDirectory(path);
    }
    public static void EnsureSharedDirectory(string path)
    {
        AdminRuntimeLease.EnsureSharedDirectory(path);
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
        // mkdir is affected by umask and does not repair existing directories.
        if (File.GetUnixFileMode(path) != mode) File.SetUnixFileMode(path, mode);
    }
    public async Task PrepareBootstrapAccessAsync(IDockerCommand docker, string apiContainer, string image)
    {
        Ensure();
        string[] shared = [Root, Path.Combine(Root, "packages"), Path.Combine(Root, "tasks")];
        // Only the updater's shared parents change ownership; databases, keys and backups stay private.
        await docker.RunAsync(["run", "--rm", "--network", "none", "--user", "0:0", "--volumes-from", apiContainer,
            "--entrypoint", "/bin/chown", image, "10001:10001", .. shared], CancellationToken.None);
        Ensure(); // chown may clear setgid; establish the final mode after ownership is set.
        string control = Path.Combine(Root, "control.lock");
        if (File.Exists(control))
        {
            AdminRuntimeLease.RequireLocalPath(control);
            await docker.RunAsync(["run", "--rm", "--network", "none", "--user", "0:0", "--volumes-from", apiContainer,
                "--entrypoint", "/bin/chown", image, "10001:10001", control], CancellationToken.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(control, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        await docker.RunAsync(["run", "--rm", "--network", "none", "--user", "10001:10001", "--volumes-from", apiContainer,
            "--entrypoint", "/bin/sh", image, "-eu", "-c",
            "for directory do probe=\"$directory/.access-$$\"; (umask 077; set -C; : > \"$probe\"); rm -- \"$probe\"; done", "probe", .. shared], CancellationToken.None);
    }
    public FileStream Lock()
    {
        Ensure(); string path = Path.Combine(Root, "control.lock"); AdminRuntimeLease.RequireLocalPath(path);
        try
        {
            bool exists = File.Exists(path);
            var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            return handle;
        }
        catch (IOException) { throw new Security.AdminFailure("server_update_busy", 409, "已有服务器更新操作正在进行。"); }
    }
    public ServerUpdateTask Task(string id)
    {
        string path = Path.Combine(TaskPath(id), "status.json"); AdminRuntimeLease.RequireLocalPath(path);
        if (!File.Exists(path)) throw new Security.AdminFailure("server_update_not_found", 404, "更新任务不存在。");
        return ServerUpdateFiles.Read<ServerUpdateTask>(path);
    }
    public void Save(ServerUpdateTask task) => ServerUpdateFiles.Write(Path.Combine(TaskPath(task.Id), "status.json"), task);
    // Caller holds the control lock and has verified that no update is pending.
    public void DiscardIncompletePackages()
    {
        var referenced = List<ServerUpdateTask>("tasks", "status.json").Select(t => t.PackageId).ToHashSet();
        foreach (string path in Directory.GetDirectories(Path.Combine(Root, "packages")))
        {
            string id = Path.GetFileName(path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$") || referenced.Contains(id) || File.Exists(Path.Combine(path, "package.json"))) continue;
            AdminRuntimeLease.RequireLocalPath(path); Directory.Delete(path, true);
        }
    }
    private readonly object _journal = new();
    public void Event(string id, string message)
    {
        lock (_journal)
        {
            string path = Path.Combine(TaskPath(id), "events.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 20 * 1024 * 1024) return;
            long seq = File.Exists(path) ? File.ReadLines(path).LongCount() + 1 : 1;
            // Command stdout is intentionally not accepted here: Docker inspect/config may contain secrets.
            File.AppendAllText(path, JsonSerializer.Serialize(new ServerUpdateEvent(seq,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message[..Math.Min(message.Length, 2000)]), ServerUpdateFiles.Json) + "\n");
            if (!OperatingSystem.IsWindows() && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.GroupWrite))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
    }
    public ServerUpdateEvent[] Logs(string id, long after)
    {
        _ = Task(id);
        string path = Path.Combine(TaskPath(id), "events.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream); var result = new List<ServerUpdateEvent>(); string? line;
        while ((line = reader.ReadLine()) != null && result.Count < 200)
        {
            try { var item = JsonSerializer.Deserialize<ServerUpdateEvent>(line, ServerUpdateFiles.Json); if (item != null && item.Seq > after) result.Add(item); }
            catch (JsonException) { break; } // An append may not yet be complete; next poll retries the same cursor.
        }
        return result.ToArray();
    }
    public T[] List<T>(string collection, string file)
    {
        string path = Path.Combine(Root, collection); if (!Directory.Exists(path)) return [];
        return Directory.GetDirectories(path).Where(d => File.Exists(Path.Combine(d, file)))
            .OrderByDescending(Directory.GetCreationTimeUtc).Take(100).Select(d => ServerUpdateFiles.Read<T>(Path.Combine(d, file))).ToArray();
    }
}
