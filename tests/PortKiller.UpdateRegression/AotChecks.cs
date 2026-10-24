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
            using (var locked = mode == "locked-file" ? new FileStream(Path.Combine(install, "libSkiaSharp.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                await RunProbe(install, fixture.Root, "--install", package);
            string journal = Path.Combine(install, ".portkiller-update", "transaction.json"), result = Path.Combine(install, ".portkiller-update", "result.txt");
            if (mode is "interrupted" or "locked-file")
            {
                await Eventually(() => File.Exists(journal));
                await Task.Delay(500);
                await RunProbe(install, fixture.Root);
            }
            await Eventually(() => File.Exists(result) && !File.Exists(journal), 60000);
            await Eventually(() => File.Exists(Path.Combine(fixture.Root, "launched-version")), 10000);
            string launched = File.ReadAllText(Path.Combine(fixture.Root, "launched-version"));
            That(UpdateManifest.ParseVersion(launched) == UpdateManifest.ParseVersion(mode == "success" ? newVersion : oldVersion), $"Unexpected launched version ({mode}): {launched}");
            foreach (string name in ReleasePackage.Files)
            {
                byte[] expected = mode == "success" ? SHA256.HashData(File.ReadAllBytes(name == "PortKiller.exe" ? newExe : Path.Combine(newDirectory, name))) : original[name];
                That(expected.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(install, name)))), "File mismatch: " + mode + ": " + name);
            }
            That(File.ReadAllBytes(Path.Combine(install, "update-settings.json")).SequenceEqual(settings) && File.ReadAllText(Path.Combine(install, "user-settings.txt")) == "preserved-settings", "Settings overwritten");
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
