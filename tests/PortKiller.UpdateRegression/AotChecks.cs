using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class AotChecks
{
    internal static async Task HistoricalThenCurrent(string legacyDirectory, string currentDirectory, string nextDirectory, string firstReceipt)
    {
        That(firstReceipt is "text" or "structured", "Unknown historical receipt format");
        using var fixture = new Fixture();
        // 历史 0.1.1 要求 >=60 秒；真实旧配置为 300，不能把新默认 30 强塞给旧安装器。
        fixture.Options.PollSeconds = 300;
        string install = Path.Combine(fixture.Root, "install"), cache = Path.Combine(fixture.Root, "cache"),
            root = Path.Combine(install, ".portkiller-update");
        Directory.CreateDirectory(install); Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(fixture.Root, "probe-sandbox.marker"), "isolated");
        File.WriteAllText(Path.Combine(fixture.Root, "mode"), "success");
        foreach (string name in ReleasePackage.Files)
            File.Copy(Path.Combine(legacyDirectory, name == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : name), Path.Combine(install, name));
        byte[] settings = JsonSerializer.SerializeToUtf8Bytes(fixture.Options, UpdateJsonContext.Default.UpdateOptions);
        File.WriteAllBytes(Path.Combine(install, "update-settings.json"), settings);
        File.WriteAllText(Path.Combine(install, "user-settings.txt"), "preserved-settings");
        string pending = Path.Combine(cache, new string('f', 64) + ".zip");
        File.WriteAllText(pending, "unrelated future update");
        string previous = FileVersionInfo.GetVersionInfo(Path.Combine(install, "PortKiller.exe")).FileVersion!;
        int notices = 0;
        foreach (string directory in new[] { currentDirectory, nextDirectory })
        {
            string version = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "PortKiller.UpdateProbe.exe")).FileVersion!;
            That(UpdateManifest.ParseVersion(version) > UpdateManifest.ParseVersion(previous), "Compatibility probe versions must increase");
            string package = Path.Combine(fixture.Root, "update-" + notices + ".zip");
            using (var zip = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
                foreach (string name in ReleasePackage.Files)
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip,
                        Path.Combine(directory, name == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : name), name);
            byte[] bytes = File.ReadAllBytes(package);
            string cached = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".zip");
            File.WriteAllBytes(cached, bytes); File.WriteAllBytes(cached + ".manifest", fixture.Envelope(version, ++notices, bytes));
            File.Delete(Path.Combine(fixture.Root, "maintenance-complete"));
            await RunProbe(install, fixture.Root, "--install", cached);
            await Eventually(() => File.Exists(Path.Combine(fixture.Root, "maintenance-complete")), 60000);
            // 新 UI 可能先于安装器进程彻底退出，下次启动必须完成被占用副本的清理。
            await RunProbe(install, fixture.Root);
            That(!File.Exists(Path.Combine(root, "transaction.json")) && !Directory.Exists(Path.Combine(root, "backup"))
                && !Directory.EnumerateDirectories(root).Any(path => Path.GetFileName(path).StartsWith("worker-")
                    || Path.GetFileName(path).StartsWith("recovery-"))
                && !File.Exists(cached) && !File.Exists(cached + ".manifest") && File.Exists(pending),
                "AOT compatibility upgrade left files or deleted a pending package: " + previous + " -> " + version);
            That(File.ReadAllLines(Path.Combine(fixture.Root, "notices.txt")).Length == notices,
                "AOT compatibility completion notice missing or repeated");
            That(File.Exists(Path.Combine(root, "result.json")) == (notices > 1 || firstReceipt == "structured"),
                "Historical installer receipt format does not match its baseline");
            foreach (string name in ReleasePackage.Files)
                That(SHA256.HashData(File.ReadAllBytes(Path.Combine(install, name))).SequenceEqual(SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(directory, name == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : name)))),
                    "Compatibility upgrade corrupted installed binary: " + name);
            That(File.ReadAllBytes(Path.Combine(install, "update-settings.json")).SequenceEqual(settings)
                && File.ReadAllText(Path.Combine(install, "user-settings.txt")) == "preserved-settings", "Compatibility upgrade changed settings");
            Console.WriteLine($"PASS historical/current AOT chain {previous} -> {version}: receipt/cleanup/cache/hash/settings/notice");
            previous = version;
        }
    }

    internal static async Task Install(string oldDirectory, string newDirectory)
    {
        string oldExe = Path.Combine(oldDirectory, "PortKiller.UpdateProbe.exe"), newExe = Path.Combine(newDirectory, "PortKiller.UpdateProbe.exe");
        That(File.Exists(oldExe) && File.Exists(newExe), "Two AOT probe versions required");
        string oldVersion = FileVersionInfo.GetVersionInfo(oldExe).FileVersion!, newVersion = FileVersionInfo.GetVersionInfo(newExe).FileVersion!;
        That(UpdateManifest.ParseVersion(newVersion) > UpdateManifest.ParseVersion(oldVersion), "Probe versions not increasing");
        foreach (string mode in new[] { "success", "unhealthy", "health-timeout", "storage-failure", "interrupted", "locked-file" })
        {
            using var fixture = new Fixture();
            string install = Path.Combine(fixture.Root, "install"); Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(fixture.Root, "probe-sandbox.marker"), "isolated"); File.WriteAllText(Path.Combine(fixture.Root, "mode"), mode);
            foreach (string name in ReleasePackage.Files)
                File.Copy(name == "PortKiller.exe" ? oldExe : Path.Combine(oldDirectory, name), Path.Combine(install, name));
            byte[] settings = JsonSerializer.SerializeToUtf8Bytes(fixture.Options, UpdateJsonContext.Default.UpdateOptions);
            File.WriteAllBytes(Path.Combine(install, "update-settings.json"), settings);
            File.WriteAllText(Path.Combine(install, "user-settings.txt"), "preserved-settings");
            var original = ReleasePackage.Files.ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(Path.Combine(install, x))));
            string package = Path.Combine(fixture.Root, "update.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
                foreach (string name in ReleasePackage.Files)
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, name == "PortKiller.exe" ? newExe : Path.Combine(newDirectory, name), name);
            File.WriteAllBytes(package + ".manifest", fixture.Envelope(newVersion, 100, File.ReadAllBytes(package)));
            string cache = Path.Combine(fixture.Root, "cache"); Directory.CreateDirectory(cache);
            string cached = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant() + ".zip");
            File.Copy(package, cached); File.Copy(package + ".manifest", cached + ".manifest");
            using (var locked = mode == "locked-file" ? new FileStream(Path.Combine(install, "libSkiaSharp.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                await RunProbe(install, fixture.Root, "--install", cached);
            string journal = Path.Combine(install, ".portkiller-update", "transaction.json"), result = Path.Combine(install, ".portkiller-update", "result.txt");
            if (mode is "interrupted" or "locked-file")
            {
                await Eventually(() => File.Exists(journal));
                await Task.Delay(500);
                await RunProbe(install, fixture.Root);
            }
            await Eventually(() => File.Exists(result) && !File.Exists(journal), 60000);
            await Eventually(() => File.Exists(Path.Combine(fixture.Root, "launched-version")), 10000);
            await Eventually(() => File.Exists(Path.Combine(fixture.Root, "maintenance-complete")), 15000);
            string launched = File.ReadAllText(Path.Combine(fixture.Root, "launched-version"));
            That(UpdateManifest.ParseVersion(launched) == UpdateManifest.ParseVersion(mode == "success" ? newVersion : oldVersion), $"Unexpected launched version ({mode}): {launched}");
            foreach (string name in ReleasePackage.Files)
            {
                byte[] expected = mode == "success" ? SHA256.HashData(File.ReadAllBytes(name == "PortKiller.exe" ? newExe : Path.Combine(newDirectory, name))) : original[name];
                That(expected.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(install, name)))), "File mismatch: " + mode + ": " + name);
            }
            That(File.ReadAllBytes(Path.Combine(install, "update-settings.json")).SequenceEqual(settings) && File.ReadAllText(Path.Combine(install, "user-settings.txt")) == "preserved-settings", "Settings overwritten");
            // 再次正常启动应幂等清理文件锁导致的暂时残留，并且不重复提示。
            await RunProbe(install, fixture.Root);
            string updateRoot = Path.Combine(install, ".portkiller-update");
            var receipt = UpdateResult.Read(updateRoot, mode == "success" ? newVersion : oldVersion);
            That(receipt != null && receipt.Outcome == (mode == "success" ? "installed"
                : mode is "interrupted" or "locked-file" ? "interrupted" : "rolled_back"), "Missing/incorrect structured result: " + mode);
            That(!Directory.Exists(Path.Combine(updateRoot, "backup"))
                && !Directory.EnumerateDirectories(updateRoot).Any(path => Path.GetFileName(path).StartsWith("worker-")
                    || Path.GetFileName(path).StartsWith("recovery-")), "Completed installation left workers/backups: " + mode);
            That(File.ReadAllLines(Path.Combine(fixture.Root, "notices.txt")).Length == 1, "Result displayed more than once: " + mode);
            That(mode == "success" ? !File.Exists(cached) && !File.Exists(cached + ".manifest") : File.Exists(cached),
                "Installed package cleanup/retry package retention failed: " + mode);
            Console.WriteLine($"PASS AOT {mode}: {oldVersion} -> {newVersion}; four hashes/settings/startup/journal verified");
        }
        if (OperatingSystem.IsWindows()) await DeniedDirectory(oldDirectory, newExe, newVersion);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task DeniedDirectory(string oldDirectory, string newExe, string newVersion)
    {
        using var fixture = new Fixture(); string install = Path.Combine(fixture.Root, "install"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(fixture.Root, "probe-sandbox.marker"), "isolated");
        foreach (string file in ReleasePackage.Files)
            File.Copy(Path.Combine(oldDirectory, file == "PortKiller.exe" ? "PortKiller.UpdateProbe.exe" : file), Path.Combine(install, file));
        File.WriteAllBytes(Path.Combine(install, "update-settings.json"), JsonSerializer.SerializeToUtf8Bytes(fixture.Options, UpdateJsonContext.Default.UpdateOptions));
        string package = Path.Combine(fixture.Root, "update.zip"); byte[] bytes = fixture.Package(File.ReadAllBytes(newExe));
        File.WriteAllBytes(package, bytes); File.WriteAllBytes(package + ".manifest", fixture.Envelope(newVersion, 101, bytes));
        var original = ReleasePackage.Files.ToDictionary(x => x, x => SHA256.HashData(File.ReadAllBytes(Path.Combine(install, x))));
        var directory = new DirectoryInfo(install); var acl = directory.GetAccessControl(); var denied = directory.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            var start = new ProcessStartInfo(Path.Combine(install, "PortKiller.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.Environment["PORTKILLER_PROBE_ROOT"] = fixture.Root; start.ArgumentList.Add("--install"); start.ArgumentList.Add(package);
            using var process = Process.Start(start)!; string error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            That(process.ExitCode == 5 && error.Contains("UnauthorizedAccessException"), "Unwritable directory was not rejected before exit: " + error);
            That(!Directory.Exists(Path.Combine(install, ".portkiller-update")), "Permission probe created a partial installation");
        }
        finally { directory.SetAccessControl(acl); }
        foreach (string file in ReleasePackage.Files) That(original[file].SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(install, file)))), "Permission failure changed executable files");
        await RunProbe(install, fixture.Root);
        Console.WriteLine("PASS AOT denied-directory: real NTFS write denial, old files intact and old version restarts");
    }
    private static async Task RunProbe(string install, string sandbox, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(install, "PortKiller.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install, RedirectStandardError = true };
        start.Environment["PORTKILLER_PROBE_ROOT"] = sandbox;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        if (process.ExitCode != 0) throw new InvalidOperationException($"Probe failed {process.ExitCode}: {error}; " +
            (File.Exists(Path.Combine(sandbox, "probe.log")) ? File.ReadAllText(Path.Combine(sandbox, "probe.log")) : "no log"));
    }
}
