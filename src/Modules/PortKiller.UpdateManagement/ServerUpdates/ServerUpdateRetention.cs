using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class ServerUpdateRetention
{
    public static async Task CleanAsync(ServerUpdateStore store, IDockerCommand docker, ServerUpdateOptions options, string currentId)
    {
        var tasks = store.List<ServerUpdateTask>("tasks", "status.json").OrderByDescending(t => t.UpdatedUtcMs).ToArray();
        var protectedTasks = tasks.Where(t => t.Id == currentId || t.State == "succeeded").Take(2).Select(t => t.Id).ToHashSet();
        protectedTasks.UnionWith(tasks.Where(t => !t.Terminal || t.State == "recoveryRequired").Select(t => t.Id));
        var packages = tasks.Where(t => protectedTasks.Contains(t.Id)).Select(t => t.PackageId).ToHashSet();
        foreach (var task in tasks.Where(t => !protectedTasks.Contains(t.Id)))
        {
            foreach (string name in new[] { "database-backup", "backup-verify", "rollback-web", "restore-web" })
            {
                string path = Path.Combine(store.TaskPath(task.Id), name); AdminRuntimeLease.RequireLocalPath(path);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            foreach (string path in Directory.GetDirectories(store.TaskPath(task.Id), "ssh-restore-*"))
            { AdminRuntimeLease.RequireLocalPath(path); Directory.Delete(path, true); }
            string web = Path.Combine(store.TaskPath(task.Id), "web-backup.tar.gz");
            if (File.Exists(web)) File.Delete(web);
            try { await docker.RunAsync(["image", "rm", "portkill-update-previous:" + task.Id], CancellationToken.None); }
            catch { /* Referenced images are retained; never force removal. */ }
        }
        foreach (var package in store.List<ServerPackage>("packages", "package.json").Where(p => !packages.Contains(p.Id)
            && (tasks.Any(t => t.PackageId == p.Id) || p.CreatedUtcMs < DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds())))
        {
            foreach (string image in new[] { package.ApplicationImage, package.RunnerImage })
            {
                if (!ServerPackageVerifier.Image(image, options.ApplicationRepository) && !ServerPackageVerifier.Image(image, options.RunnerRepository)) continue;
                try { await docker.RunAsync(["image", "rm", image], CancellationToken.None); } catch { }
            }
            string path = store.PackagePath(package.Id); AdminRuntimeLease.RequireLocalPath(path); Directory.Delete(path, true);
        }
        foreach (var old in tasks.Skip(20).Where(t => !protectedTasks.Contains(t.Id)))
        { string path = store.TaskPath(old.Id); AdminRuntimeLease.RequireLocalPath(path); Directory.Delete(path, true); }
    }
}
