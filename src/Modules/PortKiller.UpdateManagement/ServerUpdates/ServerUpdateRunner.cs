using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.ServerUpdates;

public sealed class ServerUpdateRunner(IDockerCommand docker, HttpMessageHandler? healthTransport = null)
{
    public async Task<int> RunAsync(RunnerRequest request)
    {
        request.Options.Validate(); ServerUpdateFiles.Id(request.TaskId); ServerUpdateFiles.Id(request.PackageId);
        var admin = new AdminOptions { DataRoot = request.DataRoot };
        var store = new ServerUpdateStore(admin); string taskPath = store.TaskPath(request.TaskId);
        var task = store.Task(request.TaskId); bool changed = false; FileStream? publishing = null;
        string[] beforeWeb = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        string web = Path.Combine(request.Options.DeploymentRoot, "server-bundle/web"), content = Path.Combine(store.PackagePath(request.PackageId), "content");
        void Stage(string text)
        {
            task = task with { Stage = text, UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            store.Save(task); store.Event(task.Id, text);
            Console.WriteLine(text);
        }
        void Output(string text) => store.Event(task.Id, Sanitize(text));
        string execution = Path.Combine(taskPath, "compose-execution.json"), restoreConfig = Path.Combine(taskPath, "compose-restore.json");
        try
        {
            if (File.ReadAllText(ServerUpdateFiles.Pending(admin)) != request.TaskId) throw new InvalidDataException("Task ownership mismatch.");
            Stage("核对部署、签名与剩余空间");
            _ = await DockerDeployment.InspectAsync(docker, request.Options, request.DataRoot, timeout.Token);
            await ServerComposeSnapshot.CaptureAsync(docker, request.Options, taskPath, timeout.Token);
            File.Copy(request.Bootstrap ? Path.Combine(taskPath, "compose-candidate.json") : restoreConfig, execution, true);
            AdminRuntimeLease.RequireLocalPath(web);
            using var key = JsonDocument.Parse(File.ReadAllBytes(request.Options.VerificationConfigPath));
            string publicKey = key.RootElement.EnumerateObject().Single(p => p.Name.Equals("publicKeyPem", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            var manifest = await ServerPackageContent.VerifyAsync(content, publicKey, request.Options, request.DatabaseFingerprint);
            long webBytes = Directory.GetFiles(web, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            long required = ServerUpdateSpace.Required(manifest, admin, request.Options.MinimumFreeBytes, webBytes);
            ServerUpdateService.EnsureSpace(request.Options.DeploymentRoot, required);
            ServerUpdateService.EnsureSpace(request.DataRoot, required);
            ServerUpdateService.EnsureSpace("/docker-storage", required);
            Stage("等待当前桌面发布完成");
            var drainEnd = DateTimeOffset.UtcNow.AddSeconds(request.Options.DrainSeconds);
            do
            {
                publishing = PublishExecutionLease.TryAcquire(admin);
                if (publishing != null) break;
                await Task.Delay(1000, timeout.Token);
            } while (DateTimeOffset.UtcNow < drainEnd);
            if (publishing == null) throw new AdminFailure("server_update_drain_timeout", 409, "桌面发布尚未完成，服务器更新已停止。");
            Stage("备份当前 Web 和应用镜像");
            string activeFiles = Path.Combine(store.Root, "web-current.json");
            beforeWeb = File.Exists(activeFiles) ? ServerUpdateFiles.Read<string[]>(activeFiles)
                : Directory.GetFiles(web, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(web, f).Replace('\\', '/')).ToArray();
            ServerUpdateFiles.Write(Path.Combine(taskPath, "web-before.json"), beforeWeb);
            string backup = Path.Combine(taskPath, "web-backup.tar.gz");
            await using (var output = File.Create(backup))
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                await TarFile.CreateFromDirectoryAsync(web, gzip, false, timeout.Token);
            await VerifyWebBackupAsync(backup, Path.Combine(taskPath, "backup-verify"), timeout.Token);
            foreach (string image in request.PreviousImages.Distinct())
                await docker.RunAsync(["image", "inspect", image], timeout.Token);
            // Pin the previous application even after Compose deletes its old containers.
            await docker.RunAsync(["tag", request.PreviousImages[0], "portkill-update-previous:" + request.TaskId], timeout.Token);
            string newImage;
            if (request.RollbackOf == null)
            {
                Stage("导入应用镜像");
                await docker.RunAsync(["load", "--input", Path.Combine(content, "images/application.tar.gz")], timeout.Token, Output, seconds: 600);
                newImage = await docker.RunAsync(["image", "inspect", "--format", "{{.Id}}", manifest.ApplicationImage], timeout.Token);
            }
            else
            {
                var previousRequest = ServerUpdateFiles.Read<RunnerRequest>(Path.Combine(store.TaskPath(request.RollbackOf), "request.json"));
                newImage = previousRequest.PreviousImages[0];
                await VerifyWebBackupAsync(Path.Combine(store.TaskPath(request.RollbackOf), "web-backup.tar.gz"), Path.Combine(taskPath, "rollback-web"), timeout.Token);
            }
            Stage(request.RollbackOf == null ? "替换 Web 并重建业务容器" : "恢复上一镜像和 Web");
            File.WriteAllText(Path.Combine(taskPath, "switch-started"), request.TaskId);
            changed = true;
            if (request.Bootstrap)
            {
                File.Copy(Path.Combine(taskPath, "compose-candidate.json"), Path.Combine(request.Options.DeploymentRoot, "docker-compose.yml"), true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(request.Options.DeploymentRoot, "docker-compose.yml"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await docker.RunAsync(["tag", newImage, request.ComposeImage], timeout.Token);
            CopyWeb(request.RollbackOf == null ? Path.Combine(content, "web") : Path.Combine(taskPath, "rollback-web"), web);
            await ServerComposeSnapshot.ApplyAsync(docker, request.Options, execution, timeout.Token, Output);
            Stage("检查 API、Worker、Notifier 和 Web");
            await HealthAsync(request, newImage, timeout.Token);
            VerifyWeb(request.RollbackOf == null ? Path.Combine(content, "web") : Path.Combine(taskPath, "rollback-web"), web);
            File.WriteAllText(Path.Combine(taskPath, "activated-image"), newImage);
            string[] currentWeb = request.RollbackOf == null ? manifest.Files.Where(f => f.Path.StartsWith("web/", StringComparison.Ordinal)).Select(f => f.Path[4..]).ToArray()
                : ServerUpdateFiles.Read<string[]>(Path.Combine(store.TaskPath(request.RollbackOf), "web-before.json"));
            var retainWeb = currentWeb.Concat(beforeWeb).ToHashSet(StringComparer.Ordinal);
            string assets = Path.Combine(web, "assets");
            if (Directory.Exists(assets))
                foreach (string file in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
                {
                    AdminRuntimeLease.RequireLocalPath(file);
                    if (!retainWeb.Contains(Path.GetRelativePath(web, file).Replace('\\', '/'))) File.Delete(file);
                }
            ServerUpdateFiles.Write(activeFiles, currentWeb);
            task = task with { State = "succeeded", Stage = request.RollbackOf == null ? "更新完成" : "已恢复上一版", UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            store.Save(task); store.Event(task.Id, task.Stage);
            try { await ServerUpdateRetention.CleanAsync(store, docker, request.Options, request.TaskId); }
            catch { store.Event(task.Id, "更新成功；旧文件清理未全部完成，下次更新重试。"); }
            return 0;
        }
        catch (Exception error)
        {
            string code = error is AdminFailure failure ? failure.Code : error is OperationCanceledException ? "server_update_timeout" : "server_update_failed";
            // A full/unwritable disk may also prevent logging. Default to fenced once switching has begun.
            task = task with { State = changed ? "recoveryRequired" : "failed", Error = code, Stage = changed ? "更新失败，需要检查恢复" : "更新失败，旧版未切换" };
            store.Event(task.Id, "更新失败：" + code + " · " + error.GetType().Name + " · " + Sanitize(error.Message));
            if (changed)
            {
                try
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                    store.Event(task.Id, "正在恢复更新前的应用镜像和 Web，保留业务数据库。");
                    if (request.Bootstrap) File.Copy(Path.Combine(taskPath, "compose-before.yml"), Path.Combine(request.Options.DeploymentRoot, "docker-compose.yml"), true);
                    await docker.RunAsync(["tag", request.PreviousImages[0], request.ComposeImage], restore.Token);
                    string source = Path.Combine(taskPath, "restore-web");
                    await VerifyWebBackupAsync(Path.Combine(taskPath, "web-backup.tar.gz"), source, restore.Token);
                    CopyWeb(source, web); await ServerComposeSnapshot.ApplyAsync(docker, request.Options, restoreConfig, restore.Token, Output);
                    await HealthAsync(request, request.PreviousImages[0], restore.Token);
                    VerifyWeb(source, web);
                    ServerUpdateFiles.Write(Path.Combine(store.Root, "web-current.json"), beforeWeb);
                    task = task with { State = "rolledBack", Stage = "更新失败，旧版已恢复" };
                }
                catch (Exception restoreError)
                {
                    task = task with { State = "recoveryRequired", Stage = "自动回退失败，需要 SSH 恢复" };
                    store.Event(task.Id, "自动回退失败：" + restoreError.GetType().Name + " · " + Sanitize(restoreError.Message));
                }
            }
            store.Save(task with { UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); store.Event(task.Id, task.Stage);
            return 1;
        }
        finally
        {
            publishing?.Dispose();
            foreach (string name in new[] { "backup-verify", "restore-web", "rollback-web" })
            {
                string temporary = Path.Combine(taskPath, name);
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }
            // A failed recovery remains fenced until an operator verifies the actual deployment.
            if (task.Terminal && task.State != "recoveryRequired" && File.Exists(ServerUpdateFiles.Pending(admin))
                && File.ReadAllText(ServerUpdateFiles.Pending(admin)) == task.Id) File.Delete(ServerUpdateFiles.Pending(admin));
        }
    }

    private static string Sanitize(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"(?i)(token|password|secret|authorization|key)\s*[:=]\s*\S+", "$1=[redacted]");

    private async Task HealthAsync(RunnerRequest request, string imageId, CancellationToken token)
    {
        using var client = new HttpClient(healthTransport ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }, healthTransport == null) { Timeout = TimeSpan.FromSeconds(5) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(request.Options.HealthSeconds);
        do
        {
            try
            {
                foreach (string service in new[] { "api", "worker", "notifier" })
                {
                    string id = await docker.RunAsync(["ps", "-aq", "--filter", "label=com.docker.compose.project=" + request.Options.Project,
                        "--filter", "label=com.docker.compose.service=" + service], token);
                    if (string.IsNullOrWhiteSpace(id) || id.Contains('\n')) throw new InvalidDataException();
                    using var info = JsonDocument.Parse(await docker.RunAsync(["inspect", id], token)); var item = info.RootElement[0];
                    if (item.GetProperty("Image").GetString() != imageId || !item.GetProperty("State").GetProperty("Running").GetBoolean()) throw new InvalidDataException();
                    if (service == "worker") continue;
                    string address = item.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject()
                        .Select(n => n.Value.GetProperty("IPAddress").GetString()).First(ip => IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)!;
                    using var message = new HttpRequestMessage(HttpMethod.Get, "http://" + address + (service == "api" ? ":5081/health/ready" : ":8091/health"));
                    message.Headers.Host = request.ApiHost;
                    using var response = await client.SendAsync(message, token); response.EnsureSuccessStatusCode();
                    if (service == "api")
                    {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                        if (body.RootElement.GetProperty("database").GetString() != "healthy") throw new InvalidDataException("API database is not ready.");
                    }
                }
                return;
            }
            catch when (!token.IsCancellationRequested) { await Task.Delay(2000, token); }
        } while (DateTimeOffset.UtcNow < deadline);
        throw new AdminFailure("server_health_timeout", 503, "目标服务健康检查超时。");
    }

    // SSH-only recovery. Never runs while any updater or publisher is still active and never restores the database.
    public async Task<int> RecoverAsync(RunnerRequest request)
    {
        request.Options.Validate(); ServerUpdateFiles.Id(request.TaskId);
        var admin = new AdminOptions { DataRoot = request.DataRoot }; var store = new ServerUpdateStore(admin);
        using var control = store.Lock();
        if (!File.Exists(ServerUpdateFiles.Pending(admin)) || File.ReadAllText(ServerUpdateFiles.Pending(admin)) != request.TaskId)
            throw new InvalidDataException("恢复任务与当前更新屏障不一致。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (string filter in new[] { "portkill.server-update.task=" + request.TaskId, "portkill.server-update.bootstrap=true" })
            if (!string.IsNullOrWhiteSpace(await docker.RunAsync(["ps", "-q", "--filter", "label=" + filter], timeout.Token)))
                throw new InvalidOperationException("更新容器仍在运行；请先核对其日志和状态，不可同时恢复。");
        using var publishing = PublishExecutionLease.TryAcquire(admin)
            ?? throw new InvalidOperationException("桌面发布仍在执行，不能恢复服务器。");
        var task = store.Task(request.TaskId); string taskPath = store.TaskPath(request.TaskId);
        void Output(string _) { } // Recovery prints stages only; raw Compose configuration never leaves the process.
        try
        {
            store.Event(task.Id, "SSH 恢复开始：恢复更新前镜像和 Web，保留当前数据库。");
            if (File.Exists(Path.Combine(taskPath, "switch-started")))
            {
                string extracted = Path.Combine(taskPath, "ssh-restore-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await VerifyWebBackupAsync(Path.Combine(taskPath, "web-backup.tar.gz"), extracted, timeout.Token);
                    if (request.Bootstrap) File.Copy(Path.Combine(taskPath, "compose-before.yml"), Path.Combine(request.Options.DeploymentRoot, "docker-compose.yml"), true);
                    await docker.RunAsync(["tag", request.PreviousImages[0], request.ComposeImage], timeout.Token);
                    CopyWeb(extracted, Path.Combine(request.Options.DeploymentRoot, "server-bundle/web"));
                    await ServerComposeSnapshot.ApplyAsync(docker, request.Options, Path.Combine(taskPath, "compose-restore.json"), timeout.Token, Output);
                    await HealthAsync(request, request.PreviousImages[0], timeout.Token);
                    VerifyWeb(extracted, Path.Combine(request.Options.DeploymentRoot, "server-bundle/web"));
                    ServerUpdateFiles.Write(Path.Combine(store.Root, "web-current.json"), ServerUpdateFiles.Read<string[]>(Path.Combine(taskPath, "web-before.json")));
                }
                finally { if (Directory.Exists(extracted)) Directory.Delete(extracted, true); }
            }
            else
            {
                // An interrupted launch/preflight must not rewrite a deployment which has never switched.
                _ = await DockerDeployment.InspectAsync(docker, request.Options, request.DataRoot, timeout.Token);
                await HealthAsync(request, request.PreviousImages[0], timeout.Token);
            }
            task = task with { State = "rolledBack", Stage = "SSH 已恢复更新前版本", UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            store.Save(task); store.Event(task.Id, task.Stage);
            File.Delete(ServerUpdateFiles.Pending(admin));
            Console.WriteLine("恢复检查通过，更新屏障已解除；数据库未覆盖。");
            return 0;
        }
        catch
        {
            store.Save(task with { State = "recoveryRequired", Stage = "SSH 恢复未通过，保留更新屏障", UpdatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            store.Event(task.Id, "SSH 恢复未通过；请保留备份并检查 Docker/磁盘/服务日志。");
            throw;
        }
    }

    private static async Task VerifyWebBackupAsync(string archive, string extracted, CancellationToken token)
    {
        Directory.CreateDirectory(extracted);
        await using var input = File.OpenRead(archive); await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, extracted, false, token);
        if (!File.Exists(Path.Combine(extracted, "index.html"))) throw new InvalidDataException("Web backup is incomplete.");
    }
    public static void CopyWeb(string source, string destination)
    {
        AdminRuntimeLease.RequireLocalPath(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetFileName(p) == "index.html" ? 1 : 0))
        {
            AdminRuntimeLease.RequireLocalPath(file); string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative); AdminRuntimeLease.RequireLocalPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".portkill-tmp";
            File.Copy(file, temporary, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, target, true);
        }
    }
    private static void VerifyWeb(string source, string destination)
    {
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            using var input = File.OpenRead(file); using var actual = File.OpenRead(Path.Combine(destination, Path.GetRelativePath(source, file)));
            if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(actual))) throw new InvalidDataException("Web verification failed.");
        }
    }
}
