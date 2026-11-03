using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.ServerUpdates;

public sealed class ServerUpdateService(ServerUpdateOptions options, AdminOptions admin, ServerUpdateStore store,
    IDockerCommand docker, UpdateAdminDbContext database, AdminBackupService backup, AdminAuditService audit, AdminSessionService sessions)
{
    public void RequireEnabled()
    {
        if (!options.Enabled) throw new AdminFailure("server_update_disabled", 409, "服务器更新尚未启用，请先执行一次性接入脚本。");
        options.Validate(); store.Ensure();
    }
    private string Fingerprint() => ServerPackageVerifier.Fingerprint(database.Database.GetMigrations());
    private async Task<string> PublicKeyAsync(CancellationToken token)
    {
        AdminRuntimeLease.RequireLocalPath(options.VerificationConfigPath);
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(options.VerificationConfigPath, token));
        return doc.RootElement.EnumerateObject().Single(p => p.Name.Equals("publicKeyPem", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    }
    public async Task<ServerUpdateSummary> SummaryAsync(CancellationToken token)
    {
        if (!options.Enabled) return new(false, options.MaximumPackageBytes, AdminApplication.Version, [], []);
        RequireEnabled();
        var tasks = store.List<ServerUpdateTask>("tasks", "status.json");
        // Reconcile final audit entries after API restarts; runner never opens the application DB.
        using (store.Lock())
        {
            foreach (var task in tasks.Where(t => (!t.Terminal || t.State == "recoveryRequired" && t.Error == "server_runner_interrupted")
                && t.UpdatedUtcMs < DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()))
            {
                try
                {
                    string label = task.ActorId == "ssh-bootstrap" ? "portkill.server-update.bootstrap=true" : "portkill.server-update.task=" + task.Id;
                    if (string.IsNullOrWhiteSpace(await docker.RunAsync(["ps", "-q", "--filter", "label=" + label], token)))
                    {
                        var latest = store.Task(task.Id);
                        if (latest.Terminal && latest.State != "recoveryRequired") continue;
                        bool beforePending = !ServerUpdateFiles.IsPending(admin) && !File.Exists(Path.Combine(store.TaskPath(task.Id), "switch-started"));
                        store.Save(latest with { State = beforePending ? "failed" : "recoveryRequired",
                            Stage = beforePending ? "准备过程已中断，部署未切换，可重新发起" : "更新执行器意外退出，需要 SSH 检查恢复",
                            Error = "server_runner_interrupted", UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                        store.Event(task.Id, beforePending ? "任务在建立更新屏障前中断，未切换部署；已结束准备任务。" : "未发现运行中的更新执行器；保留更新屏障和恢复文件。");
                    }
                }
                catch (Exception error) when (error is AdminFailure or IOException or OperationCanceledException) { /* A Docker/query failure cannot prove that a runner has stopped. */ }
            }
            tasks = store.List<ServerUpdateTask>("tasks", "status.json");
            foreach (var task in tasks.Where(t => t.Terminal))
            {
                string receipt = Path.Combine(store.TaskPath(task.Id), "audited");
                if (!File.Exists(receipt) || File.ReadAllText(receipt) != task.State)
                {
                    audit.Stage("server.update.completed", task.Id, task.State, task.ActorId);
                    await database.SaveChangesAsync(token); File.WriteAllText(receipt, task.State);
                }
            }
        }
        return new(true, options.MaximumPackageBytes, tasks.Where(t => t.State == "succeeded").OrderByDescending(t => t.UpdatedUtcMs).FirstOrDefault()?.Version ?? AdminApplication.Version,
            store.List<ServerPackage>("packages", "package.json"), tasks);
    }
    public async Task<ServerPackage> UploadAsync(Stream input, long? length, CancellationToken token)
    {
        RequireEnabled(); using var lease = store.Lock();
        if (ServerUpdateFiles.IsPending(admin)) throw new AdminFailure("server_update_busy", 409, "服务器更新正在执行。");
        store.DiscardIncompletePackages();
        if (length is null or <= 0 || length > options.MaximumPackageBytes)
            throw new AdminFailure("server_package_size", 413, "服务器 ZIP 大小无效或超过服务器限制。");
        if (Directory.GetDirectories(Path.Combine(store.Root, "packages")).Length >= 4)
            throw new AdminFailure("server_package_quota", 409, "服务器包暂存已满，请删除未使用的包后重试。");
        EnsureSpace(store.Root, checked(length.Value + options.MaximumExpandedBytes + options.MinimumFreeBytes));
        string id = Guid.NewGuid().ToString("N"), path = store.PackagePath(id);
        Directory.CreateDirectory(path);
        try
        {
            string zip = Path.Combine(path, "package.zip");
            await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024]; long bytes = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    bytes = checked(bytes + read); if (bytes > length.Value) throw new InvalidDataException("服务器包长度不符。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (bytes != length.Value) throw new InvalidDataException("服务器包上传不完整。");
            }
            var manifest = await ServerPackageVerifier.ExtractAsync(zip, Path.Combine(path, "content"), await PublicKeyAsync(token), options, Fingerprint(), token);
            var result = new ServerPackage(id, manifest.Version, manifest.ApplicationImage, manifest.RunnerImage, length.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ServerUpdateFiles.Write(Path.Combine(path, "package.json"), result);
            File.Delete(zip); audit.Stage("server.package.uploaded", id); await database.SaveChangesAsync(token);
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException)
        { Directory.Delete(path, true); throw new AdminFailure("server_package_invalid", 400, "服务器包签名、结构、摘要或数据库版本校验失败。"); }
        catch { Directory.Delete(path, true); throw; }
    }
    public void DeletePackage(string id)
    {
        RequireEnabled(); using var lease = store.Lock();
        if (ServerUpdateFiles.IsPending(admin) || store.List<ServerUpdateTask>("tasks", "status.json")
            .Any(t => t.PackageId == id && (!t.Terminal || t.State is "succeeded" or "recoveryRequired")))
            throw new AdminFailure("server_package_in_use", 409, "包仍被更新或恢复记录使用。");
        string path = store.PackagePath(id); AdminRuntimeLease.RequireLocalPath(path);
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    public async Task<ServerUpdateTask> StartAsync(string packageId, ClaimsPrincipal principal, string? rollbackOf = null)
    {
        RequireEnabled(); using var lease = store.Lock();
        if (ServerUpdateFiles.IsPending(admin)) throw new AdminFailure("server_update_busy", 409, "已有服务器更新任务，请等待完成；异常中断请按恢复说明检查。");
        if (Directory.GetDirectories(Path.Combine(store.Root, "tasks")).Length >= 100)
            throw new AdminFailure("server_update_history_full", 409, "更新诊断记录已达上限，请先通过 SSH 处理失败记录。");
        string packagePath = store.PackagePath(packageId);
        if (!File.Exists(Path.Combine(packagePath, "package.json"))) throw new AdminFailure("server_package_missing", 404, "服务器更新包不存在。");
        // Re-verify content from its original signed bytes before importing either executable image.
        var manifest = await ServerPackageContent.VerifyAsync(Path.Combine(packagePath, "content"), await PublicKeyAsync(CancellationToken.None), options, Fingerprint());
        var identity = await DockerDeployment.InspectAsync(docker, options, admin.DataRoot, CancellationToken.None);
        if (rollbackOf != null)
        {
            var target = store.Task(rollbackOf);
            if (target.State != "succeeded" || !File.Exists(Path.Combine(store.TaskPath(rollbackOf), "activated-image"))
                || identity.Images[0] != File.ReadAllText(Path.Combine(store.TaskPath(rollbackOf), "activated-image")))
                throw new AdminFailure("server_rollback_unavailable", 409, "当前容器已变化，不能使用该记录回退。");
        }
        // These checks precede task creation, backup and runner import. Check the host's actual Docker filesystem.
        string dockerRoot = await ServerUpdateSpace.DockerRootAsync(docker, CancellationToken.None);
        long required = ServerUpdateSpace.Required(manifest, admin, options.MinimumFreeBytes);
        EnsureSpace(store.Root, required);
        await ServerUpdateSpace.EnsureDockerAsync(docker, identity, dockerRoot, required, CancellationToken.None);
        // Serialize acceptance with login replacement, then let the accepted task run independently.
        await using var acceptance = await database.Database.BeginTransactionAsync();
        await sessions.RequireFullAsync(principal, CancellationToken.None);
        string id = Guid.NewGuid().ToString("N"), path = store.TaskPath(id); Directory.CreateDirectory(path);
        string previousVersion = store.List<ServerUpdateTask>("tasks", "status.json").Where(t => t.State == "succeeded").OrderByDescending(t => t.UpdatedUtcMs).FirstOrDefault()?.Version ?? AdminApplication.Version;
        string version = rollbackOf == null ? manifest.Version : ServerUpdateFiles.Read<RunnerRequest>(Path.Combine(store.TaskPath(rollbackOf), "request.json")).PreviousVersion;
        var task = new ServerUpdateTask(id, packageId, version, principal.FindFirstValue(ClaimTypes.NameIdentifier)!, "starting", "准备备份", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RollbackOf: rollbackOf);
        store.Save(task);
        var request = new RunnerRequest(options, admin.DataRoot, id, packageId, identity.Images, identity.ComposeImage, identity.ApiHost, Fingerprint(), rollbackOf, PreviousVersion: previousVersion);
        bool launchAttempted = false;
        try
        {
            ServerUpdateFiles.Write(Path.Combine(path, "request.json"), request);
            ServerUpdateFiles.SetPending(admin, id);
            audit.Stage(rollbackOf == null ? "server.update.started" : "server.rollback.started", id);
            await database.SaveChangesAsync();
            await acceptance.CommitAsync();
            await backup.BackupAsync(Path.Combine(path, "database-backup")); store.Event(id, "SQLite 在线备份及认证密钥备份已验证；自动回退不覆盖数据库。");
            await docker.RunAsync(["load", "--input", Path.Combine(packagePath, "content/images/runner.tar.gz")], CancellationToken.None, seconds: 600);
            task = task with { State = "running", Stage = "启动独立更新任务" }; store.Save(task);
            string socketGroup = await SocketGroupAsync();
            launchAttempted = true;
            await docker.RunAsync(["run", "--detach", "--rm", "--name", "portkill-update-" + id,
                "--label", "portkill.server-update.task=" + id, "--network", "host", "--user", "0:0",
                "--group-add", "0", "--volumes-from", identity.ApiContainer,
                "--mount", "type=bind,src=" + options.DeploymentRoot + ",dst=" + options.DeploymentRoot,
                "--mount", "type=bind,src=" + dockerRoot + ",dst=/docker-storage,readonly",
                "--group-add", socketGroup, manifest.RunnerImage, Path.Combine(path, "request.json")], CancellationToken.None);
            return store.Task(id);
        }
        catch
        {
            // A failed CLI response may still have created the container. Do not unlock an uncertain deployment.
            task = task with { State = launchAttempted ? "recoveryRequired" : "failed", Stage = "启动更新失败", Error = "server_runner_start_failed" };
            store.Save(task); store.Event(id, launchAttempted ? "无法确认 runner 是否启动，请通过 SSH 核对任务容器；保留更新屏障。" : "更新尚未切换，旧版继续运行。");
            if (!launchAttempted) File.Delete(ServerUpdateFiles.Pending(admin));
            throw new AdminFailure("server_runner_start_failed", 503, "更新启动失败，请查看任务记录。");
        }
    }
    private static async Task<string> SocketGroupAsync()
    {
        // bootstrap writes the measured numeric socket group; Docker CLI cannot report Unix inode ownership.
        string group = Environment.GetEnvironmentVariable("PORTKILL_DOCKER_GID") ?? "";
        if (!uint.TryParse(group, out _)) throw new InvalidOperationException("Docker socket GID is not configured.");
        return await Task.FromResult(group);
    }
    public static void EnsureSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetFullPath(path));
        if (drive.AvailableFreeSpace < bytes) throw new AdminFailure("server_update_space", 409, "更新或备份所需磁盘空间不足。");
    }
}

public static class ServerPackageContent
{
    public static async Task<ServerPackageManifest> VerifyAsync(string content, string publicKey, ServerUpdateOptions options, string fingerprint)
    {
        // Verify the same signed format without trusting mutable package metadata. Hash all files again.
        byte[] payload = File.ReadAllBytes(Path.Combine(content, "server-manifest.json"));
        byte[] sig = File.ReadAllBytes(Path.Combine(content, "server-manifest.sig"));
        using var rsa = System.Security.Cryptography.RSA.Create(); rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(payload, sig, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1)) throw new InvalidDataException("Invalid signature.");
        var manifest = JsonSerializer.Deserialize<ServerPackageManifest>(payload, ServerUpdateFiles.Json)!;
        if (manifest.DatabaseFingerprint != fingerprint || manifest.DatabaseChanges || manifest.Schema != 1 || manifest.Kind != "portkill-server-update"
            || manifest.Runtime != "linux-x64" || !ServerPackageVerifier.Image(manifest.ApplicationImage, options.ApplicationRepository)
            || !ServerPackageVerifier.Image(manifest.RunnerImage, options.RunnerRepository)) throw new InvalidDataException("Invalid manifest.");
        foreach (var file in manifest.Files)
        {
            if (!ServerPackageVerifier.SafePath(file.Path)) throw new InvalidDataException("Invalid path.");
            string path = Path.Combine(content, file.Path); AdminRuntimeLease.RequireLocalPath(path);
            await using var stream = File.OpenRead(path);
            if (stream.Length != file.Bytes || Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream)) != file.Sha256)
                throw new InvalidDataException("Package changed after verification.");
        }
        return manifest;
    }
}
