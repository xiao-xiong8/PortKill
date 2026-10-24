using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class UpdateToolClientTests
{
    [Fact]
    public async Task InvokesPinnedToolWithArgumentListAndReturnsSanitizedStructuredErrors()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortKill Update Tool Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string repository = FindRepositoryRoot();
            string executableName = "PortKiller.UpdateTool" + (OperatingSystem.IsWindows() ? ".exe" : "");
            string executable = Path.Combine(repository, "src", "Tools", "PortKiller.UpdateTool", "bin", "Release", "net10.0", executableName);
            Assert.True(File.Exists(executable), $"UpdateTool apphost was not built: {executable}");

            using var trustedKey = RSA.Create(2048);
            using var forgedKey = RSA.Create(2048);
            byte[] payload = Encoding.UTF8.GetBytes("{}");
            byte[] forgedSignature = forgedKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string manifestPath = Path.Combine(root, "manifest with spaces.json");
            string packagePath = Path.Combine(root, "package with spaces.zip");
            string configPath = Path.Combine(root, "verification config.json");
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(
                new SignedUpdate { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(forgedSignature) },
                ProtocolJsonContext.Default.SignedUpdate));
            await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ManifestVerificationOptions
            {
                ManifestUrl = "https://updates.example/stable/manifest.json",
                PublicKeyPem = trustedKey.ExportSubjectPublicKeyInfoPem()
            }, ProtocolJsonContext.Default.ManifestVerificationOptions));

            var client = new UpdateToolClient(new UpdateToolOptions
            {
                ExecutablePath = executable,
                VerificationConfigPath = configPath,
                TimeoutSeconds = 30,
                MaximumOutputBytes = 1024
            });
            var result = await client.VerifyAsync(manifestPath, packagePath, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("validation_failed", result.ErrorCode);
            Assert.Null(result.Version);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingToolAndInvalidWorkerConfigurationFailClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortKill Update Tool Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var rsa = RSA.Create(2048);
            string configPath = Path.Combine(root, "config.json");
            string manifestPath = Path.Combine(root, "manifest.json");
            string packagePath = Path.Combine(root, "package.zip");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new ManifestVerificationOptions
            {
                ManifestUrl = "https://updates.example/manifest.json",
                PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem()
            }, ProtocolJsonContext.Default.ManifestVerificationOptions));
            await File.WriteAllBytesAsync(manifestPath, [1]);
            await File.WriteAllBytesAsync(packagePath, [1]);

            var client = new UpdateToolClient(new UpdateToolOptions
            {
                ExecutablePath = Path.Combine(root, "missing tool"), VerificationConfigPath = configPath
            });
            Assert.Equal("tool_unavailable", (await client.VerifyAsync(manifestPath, packagePath, CancellationToken.None)).ErrorCode);

            var invalid = new UpdateToolClient(new UpdateToolOptions { ExecutablePath = "relative-tool", VerificationConfigPath = configPath });
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.VerifyAsync(manifestPath, packagePath, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ToolOutputIsBoundedAndTimeoutKillsTheEntireProcessTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortKill Update Tool Process Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string testHostRoot = Path.Combine(FindRepositoryRoot(), "tests", "PortKiller.UpdateTool.TestHost", "bin");
            string executable = new[] { "Release", "Debug" }
                .Select(configuration => Path.Combine(testHostRoot, configuration, "net10.0",
                    "PortKiller.UpdateTool.TestHost" + (OperatingSystem.IsWindows() ? ".exe" : "")))
                .FirstOrDefault(File.Exists) ?? Path.Combine(testHostRoot, "Release", "net10.0", "PortKiller.UpdateTool.TestHost.exe");
            Assert.True(File.Exists(executable), $"Process test host was not built: {executable}");
            string manifestPath = Path.Combine(root, "manifest.json");
            string packagePath = Path.Combine(root, "package.zip");
            await File.WriteAllBytesAsync(manifestPath, [1]);
            await File.WriteAllBytesAsync(packagePath, [1]);

            string outputConfig = Path.Combine(root, "output.json");
            await File.WriteAllTextAsync(outputConfig, "{\"testMode\":\"output\"}");
            var outputClient = new UpdateToolClient(new UpdateToolOptions
            {
                ExecutablePath = executable, VerificationConfigPath = outputConfig,
                TimeoutSeconds = 10, MaximumOutputBytes = 1024
            });
            var output = await outputClient.VerifyAsync(manifestPath, packagePath, CancellationToken.None);
            Assert.Equal("tool_output_limit", output.ErrorCode);

            string pidPath = Path.Combine(root, "child.pid");
            string spawnConfig = Path.Combine(root, "spawn.json");
            await File.WriteAllTextAsync(spawnConfig, JsonSerializer.Serialize(new { testMode = "spawn", pidPath }));
            var timeoutClient = new UpdateToolClient(new UpdateToolOptions
            {
                ExecutablePath = executable, VerificationConfigPath = spawnConfig,
                TimeoutSeconds = 1, MaximumOutputBytes = 1024
            });
            var timeout = await timeoutClient.VerifyAsync(manifestPath, packagePath, CancellationToken.None);
            Assert.Equal("tool_timeout", timeout.ErrorCode);

            Assert.True(File.Exists(pidPath));
            int childId = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            bool exited = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try { using var child = Process.GetProcessById(childId); if (child.HasExited) { exited = true; break; } }
                catch (ArgumentException) { exited = true; break; }
                await Task.Delay(100);
            }
            Assert.True(exited, "Timed-out UpdateTool child process was left running.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root could not be located from test output.");
    }
}
