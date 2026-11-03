using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.ServerUpdates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed partial class ServerUpdateTests
{
    private sealed class RunnerFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public ServerUpdateStore Store { get; }
        public RunnerRequest Request { get; }
        public FakeDocker Docker { get; }
        public string Web => Path.Combine(Root, "server-bundle/web");
        public string Database => Path.Combine(Request.DataRoot, "business-data");
        public RunnerFixture(string? literal = null)
        {
            Directory.CreateDirectory(Web); File.WriteAllText(Path.Combine(Web, "index.html"), "old-web");
            var admin = new AdminOptions { DataRoot = Path.Combine(Root, "admin") };
            Store = new(admin); Store.Ensure(); string id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Store.TaskPath(id)); Directory.CreateDirectory(Store.PackagePath(id));
            using var key = RSA.Create(2048);
            string verification = Path.Combine(Root, "verification.json");
            File.WriteAllText(verification, JsonSerializer.Serialize(new { publicKeyPem = key.ExportSubjectPublicKeyInfoPem() }));
            string zip = Path.Combine(Root, "package.zip"); File.WriteAllBytes(zip, Package(key));
            ServerPackageVerifier.ExtractAsync(zip, Path.Combine(Store.PackagePath(id), "content"), key.ExportSubjectPublicKeyInfoPem(), new(), Fingerprint, CancellationToken.None).GetAwaiter().GetResult();
            Store.Save(new(id, id, "1.2.3", "test", "running", "test", 0));
            File.WriteAllText(ServerUpdateFiles.Pending(admin), id);
            Request = new(new() { DeploymentRoot = Root, Project = "portkill", VerificationConfigPath = verification, MinimumFreeBytes = 0, HealthSeconds = 1, DrainSeconds = 1 },
                admin.DataRoot, id, id, ["sha256:old", "sha256:old", "sha256:old"], "portkill-update-server:installed", "admin.example", Fingerprint);
            ServerUpdateFiles.Write(Path.Combine(Store.TaskPath(id), "request.json"), Request);
            File.WriteAllText(Database, "live-business-data");
            File.WriteAllText(Path.Combine(Root, ".env"), "");
            string compose = JsonSerializer.Serialize(new { services = new[] { "api", "worker", "notifier", "caddy" }
                .ToDictionary(service => service, service => new { image = Request.ComposeImage, environment = new { AllowedHosts = "admin.example" },
                    volumes = new[] { new { type = "bind", source = Root + "/data/admin", target = admin.DataRoot } } }) });
            if (literal != null)
            {
                var config = System.Text.Json.Nodes.JsonNode.Parse(compose)!;
                config["services"]!["api"]!["environment"]!["SECRET"] = literal;
                compose = config.ToJsonString();
            }
            File.WriteAllText(Path.Combine(Root, "docker-compose.yml"), compose);
            Docker = new(Request);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class FakeDocker(RunnerRequest request) : IDockerCommand
    {
        public string CurrentImage = "sha256:old", TaggedImage = "sha256:old";
        public string Failure = "";
        public bool UpdaterRunning;
        public int ComposeCalls;
        public Action? OnComposeFailure;
        public Action? OnLoad;
        public long DockerFreeBytes = long.MaxValue;
        public RunnerRequest InspectedRequest = request;
        private readonly Dictionary<string, string> _runningHashes = Hashes(File.ReadAllText(Path.Combine(request.Options.DeploymentRoot, "docker-compose.yml")), false);
        public List<string[]> Commands { get; } = [];
        private static Dictionary<string, string> Hashes(string config, bool escaped = true) => JsonDocument.Parse(escaped ? config.Replace("$$", "$", StringComparison.Ordinal) : config).RootElement.GetProperty("services").EnumerateObject()
            .ToDictionary(service => service.Name, service => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(service.Value)))));
        public Task<string> RunAsync(string[] args, CancellationToken token, Action<string>? output = null, int seconds = 120)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add(args);
            if (args[0] == "ps")
            {
                string? label = args.FirstOrDefault(a => a.StartsWith("label=com.docker.compose.service="));
                return Task.FromResult(label == null ? UpdaterRunning ? "running-updater" : "" : label.Split('=').Last());
            }
            if (args[0] == "inspect")
            {
                string service = args[1];
                return Task.FromResult(JsonSerializer.Serialize(new[] { new
                {
                    Image = CurrentImage, State = new { Running = true },
                    Config = new { Image = InspectedRequest.ComposeImage, Labels = new Dictionary<string, string> { ["com.docker.compose.project.working_dir"] = InspectedRequest.Options.DeploymentRoot,
                        ["com.docker.compose.config-hash"] = _runningHashes[service] }, Env = new[] { "AllowedHosts=admin.example" } },
                    Mounts = new[] { new { Type = "bind", Destination = InspectedRequest.DataRoot, Source = InspectedRequest.Options.DeploymentRoot + "/data/admin" }, new { Type = "bind", Destination = "/srv/web", Source = InspectedRequest.Options.DeploymentRoot + "/server-bundle/web" } },
                    NetworkSettings = new { Networks = new { test = new { IPAddress = "127.0.0.1" } } }
                } }));
            }
            if (args[0] == "info") return Task.FromResult("/var/lib/docker");
            if (args[0] == "run" && args.Contains("/bin/df")) return Task.FromResult("Avail\n" + DockerFreeBytes);
            if (args[0] == "load") OnLoad?.Invoke();
            if (args[0] == "load" && Failure == "load") throw new IOException("simulated image import failure");
            if (args[0] == "image" && args.Contains("--format")) return Task.FromResult("sha256:new");
            if (args[0] == "tag" && args[2] == InspectedRequest.ComposeImage) TaggedImage = args[1];
            if (args[0] == "compose")
            {
                string file = args[Array.IndexOf(args, "-f") + 1];
                if (args.Contains("config"))
                {
                    bool escaped = Path.GetFileName(file) != "docker-compose.yml";
                    string content = File.ReadAllText(file);
                    return Task.FromResult(args.Contains("--hash") ? string.Join('\n', Hashes(content, escaped).Select(pair => pair.Key + " " + pair.Value))
                        : escaped ? content : content.Replace("$", "$$", StringComparison.Ordinal));
                }
                ComposeCalls++;
                if (Failure == "restore" || Failure == "compose" && ComposeCalls == 1)
                { OnComposeFailure?.Invoke(); throw new IOException("simulated compose failure"); }
                CurrentImage = TaggedImage;
                foreach (var pair in Hashes(File.ReadAllText(file))) _runningHashes[pair.Key] = pair.Value;
            }
            return Task.FromResult("");
        }
    }

    private sealed class HealthTransport(FakeDocker docker, bool rejectNew) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(
            new HttpResponseMessage(rejectNew && docker.CurrentImage == "sha256:new" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"database\":\"healthy\"}") });
    }

    [Fact]
    public async Task RunnerCompletesWithPersistedBackupsAndNeverReplacesBusinessData()
    {
        using var fixture = new RunnerFixture(); using var health = new HealthTransport(fixture.Docker, false);
        Assert.Equal(0, await new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.Equal("succeeded", fixture.Store.Task(fixture.Request.TaskId).State);
        Assert.Equal("sha256:new", fixture.Docker.CurrentImage);
        Assert.True(File.Exists(Path.Combine(fixture.Store.TaskPath(fixture.Request.TaskId), "web-backup.tar.gz")));
        Assert.False(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("volumes")]
    [InlineData("environment")]
    public async Task ProspectiveComposeDriftStopsBeforeAnyImportOrSwitch(string field)
    {
        using var fixture = new RunnerFixture(); using var health = new HealthTransport(fixture.Docker, false);
        string compose = Path.Combine(fixture.Root, "docker-compose.yml");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(compose))!;
        var api = document["services"]!["api"]!;
        if (field == "image") api["image"] = "unexpected:latest";
        else if (field == "environment") api["environment"]!["AllowedHosts"] = "other.example";
        else api["volumes"]![0]!["source"] = "/unexpected-data";
        File.WriteAllText(compose, document.ToJsonString());
        Assert.Equal(1, await new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.Equal("deployment_compose_drift", fixture.Store.Task(fixture.Request.TaskId).Error);
        Assert.DoesNotContain(fixture.Docker.Commands, args => args[0] == "load" || args[0] == "tag");
        Assert.Equal(0, fixture.Docker.ComposeCalls);
        Assert.Equal("old-web", File.ReadAllText(Path.Combine(fixture.Web, "index.html")));
    }

    [Fact]
    public async Task UpdateAndAutomaticRollbackUseCheckedSnapshotsEvenIfHostComposeChanges()
    {
        using var fixture = new RunnerFixture(); fixture.Docker.Failure = "compose";
        string compose = Path.Combine(fixture.Root, "docker-compose.yml"), original = File.ReadAllText(compose);
        fixture.Docker.OnLoad = () => File.WriteAllText(compose, "unexpected host edit");
        using var health = new HealthTransport(fixture.Docker, false);
        Assert.Equal(1, await new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.Equal("rolledBack", fixture.Store.Task(fixture.Request.TaskId).State);
        var executions = fixture.Docker.Commands.Where(args => args[0] == "compose" && args.Contains("up")).ToArray();
        Assert.Equal(2, executions.Length);
        Assert.All(executions, args => Assert.DoesNotContain(compose, args));
        Assert.Equal("unexpected host edit", File.ReadAllText(compose));
        Assert.Equal("sha256:old", fixture.Docker.CurrentImage);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fixture.Store.TaskPath(fixture.Request.TaskId), "compose-restore.json")));
    }

    [Fact]
    public void ComposeSnapshotsEscapeResolvedDollarsWithoutPublishingSecrets()
    {
        using var fixture = new RunnerFixture();
        var value = System.Text.Json.Nodes.JsonNode.Parse("{\"services\":{\"api\":{\"environment\":{\"SECRET\":\"a${value}$b$$c\"}}}}")!;
        string snapshot = Path.Combine(fixture.Store.TaskPath(fixture.Request.TaskId), "snapshot.json");
        ServerComposeSnapshot.Write(snapshot, value);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(snapshot))!;
        Assert.Equal("a$${value}$$b$$$$c", saved["services"]!["api"]!["environment"]!["SECRET"]!.GetValue<string>());
        Assert.Empty(fixture.Store.Logs(fixture.Request.TaskId, 0));
    }

    [Fact]
    public async Task ComposeConfigAlreadyEscapedDollarValuesAreNotEscapedTwice()
    {
        const string secret = "a${value}$b$$c";
        using var fixture = new RunnerFixture(secret);
        string taskPath = fixture.Store.TaskPath(fixture.Request.TaskId);
        var actual = await ServerComposeSnapshot.CaptureAsync(fixture.Docker, fixture.Request.Options, taskPath, default);
        Assert.Equal(secret, actual["services"]!["api"]!["environment"]!["SECRET"]!.GetValue<string>());
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(taskPath, "compose-restore.json")))!;
        Assert.Equal("a$${value}$$b$$$$c", snapshot["services"]!["api"]!["environment"]!["SECRET"]!.GetValue<string>());
    }

    [Fact]
    public async Task BootstrapPermissionRepairOnlyTouchesUpdaterSharedParentsAndProbesAsApiUser()
    {
        using var fixture = new RunnerFixture();
        await fixture.Store.PrepareBootstrapAccessAsync(fixture.Docker, "api", "sha256:old");
        var ownership = Assert.Single(fixture.Docker.Commands, args => args.Contains("/bin/chown") && !args.Contains(Path.Combine(fixture.Store.Root, "control.lock")));
        Assert.Contains("10001:10001", ownership);
        Assert.DoesNotContain(fixture.Request.DataRoot, ownership);
        Assert.DoesNotContain(fixture.Database, ownership);
        var probe = Assert.Single(fixture.Docker.Commands, args => args.Contains("/bin/sh"));
        Assert.Equal("10001:10001", probe[Array.IndexOf(probe, "--user") + 1]);
    }

    [Theory]
    [InlineData("load", "failed")]
    [InlineData("compose", "rolledBack")]
    [InlineData("health", "rolledBack")]
    public async Task RunnerStopsOrRollsBackOnImportComposeAndReadinessFailures(string failure, string expected)
    {
        using var fixture = new RunnerFixture(); fixture.Docker.Failure = failure;
        using var health = new HealthTransport(fixture.Docker, failure == "health");
        Assert.Equal(1, await new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.Equal(expected, fixture.Store.Task(fixture.Request.TaskId).State);
        Assert.Equal("sha256:old", fixture.Docker.CurrentImage);
        Assert.Equal("old-web", File.ReadAllText(Path.Combine(fixture.Web, "index.html")));
        Assert.False(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }

    [Fact]
    public async Task FailedRollbackStaysFencedUntilSshRecoveryChecksPass()
    {
        using var fixture = new RunnerFixture(); fixture.Docker.Failure = "restore";
        using var health = new HealthTransport(fixture.Docker, false); var runner = new ServerUpdateRunner(fixture.Docker, health);
        Assert.Equal(1, await runner.RunAsync(fixture.Request));
        Assert.Equal("recoveryRequired", fixture.Store.Task(fixture.Request.TaskId).State);
        Assert.True(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        fixture.Docker.Failure = ""; fixture.Docker.UpdaterRunning = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RecoverAsync(fixture.Request));
        Assert.True(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        fixture.Docker.UpdaterRunning = false;
        Assert.Equal(0, await runner.RecoverAsync(fixture.Request));
        Assert.Equal("rolledBack", fixture.Store.Task(fixture.Request.TaskId).State);
        Assert.False(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }

    [Fact]
    public async Task RunningDesktopPublicationPreventsAnyImageOrWebSwitch()
    {
        using var fixture = new RunnerFixture(); using var health = new HealthTransport(fixture.Docker, false);
        using var publication = PublishExecutionLease.TryAcquire(new() { DataRoot = fixture.Request.DataRoot });
        Assert.NotNull(publication);
        Assert.Equal(1, await new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.Equal("server_update_drain_timeout", fixture.Store.Task(fixture.Request.TaskId).Error);
        Assert.Equal(0, fixture.Docker.ComposeCalls);
        Assert.Equal("old-web", File.ReadAllText(Path.Combine(fixture.Web, "index.html")));
        Assert.False(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
    }

    [Fact]
    public async Task JournalWriteFailureAfterSwitchNeverReleasesTheUpdateFence()
    {
        using var fixture = new RunnerFixture(); fixture.Docker.Failure = "compose";
        string journal = Path.Combine(fixture.Store.TaskPath(fixture.Request.TaskId), "events.jsonl");
        fixture.Docker.OnComposeFailure = () => { File.Move(journal, journal + ".before-failure"); Directory.CreateDirectory(journal); };
        using var health = new HealthTransport(fixture.Docker, false);
        await Assert.ThrowsAnyAsync<Exception>(() => new ServerUpdateRunner(fixture.Docker, health).RunAsync(fixture.Request));
        Assert.True(File.Exists(Path.Combine(fixture.Store.Root, "pending")));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }

    [Fact]
    public async Task ManualRollbackRestoresPreviousWebAndImageAndKeepsCurrentDatabase()
    {
        using var fixture = new RunnerFixture(); using var health = new HealthTransport(fixture.Docker, false);
        var runner = new ServerUpdateRunner(fixture.Docker, health);
        Assert.Equal(0, await runner.RunAsync(fixture.Request));
        string id = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(fixture.Store.TaskPath(id));
        var rollback = fixture.Request with { TaskId = id, PreviousImages = ["sha256:new"], RollbackOf = fixture.Request.TaskId };
        fixture.Store.Save(new(id, rollback.PackageId, "1.0.0", "test", "running", "manual rollback", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RollbackOf: rollback.RollbackOf));
        File.WriteAllText(Path.Combine(fixture.Store.Root, "pending"), id);
        Assert.Equal(0, await runner.RunAsync(rollback));
        Assert.Equal("succeeded", fixture.Store.Task(id).State);
        Assert.Equal("sha256:old", fixture.Docker.CurrentImage);
        Assert.Equal("old-web", File.ReadAllText(Path.Combine(fixture.Web, "index.html")));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }

    [Fact]
    public async Task RetentionKeepsTwoSuccessesAndUnrecoveredTasksWithoutGlobalPruning()
    {
        using var fixture = new RunnerFixture(); var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        for (int index = 0; index < ids.Length; index++)
        {
            string id = ids[index]; Directory.CreateDirectory(fixture.Store.TaskPath(id)); Directory.CreateDirectory(fixture.Store.PackagePath(id));
            fixture.Store.Save(new(id, id, "1.0.0", "test", index == 3 ? "recoveryRequired" : "succeeded", "test", index));
            File.WriteAllText(Path.Combine(fixture.Store.TaskPath(id), "web-backup.tar.gz"), "test backup");
            ServerUpdateFiles.Write(Path.Combine(fixture.Store.PackagePath(id), "package.json"), new ServerPackage(id, "1.0.0", "portkill-update-server:" + id, "portkill-server-update-runner:" + id, 10, 0));
        }
        await ServerUpdateRetention.CleanAsync(fixture.Store, fixture.Docker, fixture.Request.Options, ids[2]);
        Assert.False(Directory.Exists(fixture.Store.PackagePath(ids[0])));
        Assert.False(File.Exists(Path.Combine(fixture.Store.TaskPath(ids[0]), "web-backup.tar.gz")));
        foreach (string id in ids.Skip(1))
        {
            Assert.True(Directory.Exists(fixture.Store.PackagePath(id)));
            Assert.True(File.Exists(Path.Combine(fixture.Store.TaskPath(id), "web-backup.tar.gz")));
        }
        Assert.DoesNotContain(fixture.Docker.Commands, args => args.Contains("prune") || args.Contains("--force"));
        Assert.Equal("live-business-data", File.ReadAllText(fixture.Database));
    }
}
