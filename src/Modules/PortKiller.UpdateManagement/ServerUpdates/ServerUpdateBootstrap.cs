using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.ServerUpdates;

public static class ServerUpdateBootstrap
{
    // Invoked only by the SSH bootstrap script after host-side signature verification.
    public static async Task<int> RunAsync(string root, string project, string expectedSha, string socketGroup)
    {
        var options = new ServerUpdateOptions { Enabled = true, DeploymentRoot = root, Project = project };
        options.Validate(); if (!uint.TryParse(socketGroup, out _)) throw new InvalidDataException("Invalid socket group.");
        string compose = Path.Combine(root, "docker-compose.yml"); AdminRuntimeLease.RequireLocalPath(compose);
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(compose))) != expectedSha) throw new InvalidDataException("Compose changed since review.");
        var admin = new AdminOptions { DataRoot = "/var/lib/portkill/admin" }; var store = new ServerUpdateStore(admin);
        using (store.Lock())
        {
            if (ServerUpdateFiles.IsPending(admin)) throw new InvalidOperationException("Another update is pending.");
            var docker = new DockerCommand();
            var identity = await DockerDeployment.InspectAsync(docker, options, admin.DataRoot, CancellationToken.None);
            await store.PrepareBootstrapAccessAsync(docker, identity.ApiContainer, identity.Images[0]);
            using var context = new UpdateAdminDbContext(new DbContextOptionsBuilder<UpdateAdminDbContext>().UseSqlite("Data Source=" + admin.DatabasePath).Options);
            string fingerprint = ServerPackageVerifier.Fingerprint(context.Database.GetMigrations());
            using var publicConfig = JsonDocument.Parse(File.ReadAllBytes(options.VerificationConfigPath));
            string publicKey = publicConfig.RootElement.EnumerateObject().Single(p => p.Name.Equals("publicKeyPem", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            var preview = await ServerPackageVerifier.InspectAsync("/update-input/package.zip", publicKey, options, fingerprint, CancellationToken.None);
            long webBytes = Directory.GetFiles(Path.Combine(root, "server-bundle/web"), "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            long required = ServerUpdateSpace.Required(preview, admin, options.MinimumFreeBytes, webBytes);
            ServerUpdateService.EnsureSpace(admin.DataRoot, required);
            ServerUpdateService.EnsureSpace(root, required);
            ServerUpdateService.EnsureSpace("/docker-storage", required);
            string id = Guid.NewGuid().ToString("N"), path = store.PackagePath(id); ServerUpdateStore.EnsureSharedDirectory(path);
            var manifest = await ServerPackageVerifier.ExtractAsync("/update-input/package.zip", Path.Combine(path, "content"), publicKey, options, fingerprint, CancellationToken.None);
            ServerUpdateFiles.Write(Path.Combine(path, "package.json"), new ServerPackage(id, manifest.Version, manifest.ApplicationImage, manifest.RunnerImage,
                new FileInfo("/update-input/package.zip").Length, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var task = new ServerUpdateTask(id, id, manifest.Version, "ssh-bootstrap", "running", "首次接入", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(store.TaskPath(id));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(store.TaskPath(id), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
            store.Save(task);
            await new AdminBackupService(admin, TimeProvider.System).BackupAsync(Path.Combine(store.TaskPath(id), "database-backup"));
            var document = await ServerComposeSnapshot.CaptureAsync(docker, options, store.TaskPath(id), CancellationToken.None);
            var api = document["services"]!["api"]!;
            var environment = api["environment"]!.AsObject();
            environment["ServerUpdate__Enabled"] = "true"; environment["ServerUpdate__DeploymentRoot"] = root;
            environment["ServerUpdate__Project"] = project; environment["PORTKILL_DOCKER_GID"] = socketGroup;
            var volumes = api["volumes"]!.AsArray();
            if (!volumes.Any(v => v?["target"]?.GetValue<string>() == "/var/run/docker.sock"))
                volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = "/var/run/docker.sock", ["target"] = "/var/run/docker.sock" });
            var groups = api["group_add"] as JsonArray ?? new JsonArray();
            if (!groups.Any(g => g?.ToString() == socketGroup)) groups.Add(socketGroup);
            api["group_add"] = groups.DeepClone();
            // Save both original and candidate; the runner switches only after its preflight and drain.
            File.Copy(compose, Path.Combine(store.TaskPath(id), "compose-before.yml"));
            ServerComposeSnapshot.Write(Path.Combine(store.TaskPath(id), "compose-candidate.json"), document);
            var request = new RunnerRequest(options, admin.DataRoot, id, id, identity.Images, identity.ComposeImage, identity.ApiHost, fingerprint,
                Bootstrap: true, DockerGroup: socketGroup);
            ServerUpdateFiles.Write(Path.Combine(store.TaskPath(id), "request.json"), request);
            ServerUpdateFiles.SetPending(admin, id);
            Console.WriteLine("Bootstrap task: " + id);
            return await new ServerUpdateRunner(docker).RunAsync(request);
        }
    }
}
