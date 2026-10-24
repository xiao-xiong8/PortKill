using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class PublisherChecks
{
    internal static async Task Run(string repository, string prepared)
    {
        using var fixture = new Fixture(); await using var host = await fixture.HostAsync();
        string privateKey = Path.Combine(fixture.Root, "private.pem"), publicKey = Path.Combine(fixture.Root, "public.pem");
        File.WriteAllText(privateKey, fixture.Key.ExportRSAPrivateKeyPem()); File.WriteAllText(publicKey, fixture.Key.ExportSubjectPublicKeyInfoPem());
        string version = FileVersionInfo.GetVersionInfo(Path.Combine(prepared, "PortKiller.exe")).FileVersion!;
        string script = Path.Combine(repository, "scripts", "release", "Publish-Update.ps1");
        string[] initial = ["-Version", version, "-PreparedPublishDirectory", prepared, "-SigningKeyPath", privateKey, "-PublicKeyPath", publicKey,
            "-PublicBaseUrl", new Uri(new Uri(fixture.Options.ManifestUrl), "./").AbsoluteUri, "-AllowHttp", "-AllowDirty", "-LocalRoot", fixture.Public,
            "-ReleaseNotesPath", Path.Combine(repository, "deploy", "examples", "release-notes.txt")];
        using (var wrong = RSA.Create(2048))
        {
            string wrongPath = Path.Combine(fixture.Root, "wrong.pem"); File.WriteAllText(wrongPath, wrong.ExportSubjectPublicKeyInfoPem());
            var mismatched = initial.ToArray(); mismatched[Array.IndexOf(mismatched, publicKey)] = wrongPath;
            var failure = await Invoke(script, mismatched); That(failure.Exit != 0 && failure.Text.Contains("SigningKeyMismatch"), "Key mismatch not rejected before build: " + failure.Text);
        }
        // A public probe failure occurs after the commit point; resume must reuse signed artifacts.
        fixture.BlockManifest = true;
        var failed = await Invoke(script, initial); That(failed.Exit != 0, "Public probe failure was ignored");
        string root = Path.Combine(repository, ".local", "artifacts", "releases");
        string recordPath = Directory.EnumerateFiles(root, "release-record.json", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(fixture.Options.ManifestUrl.Replace("manifest.json", "")))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? throw new InvalidOperationException("Publisher did not record release: " + failed.Text);
        using var record = JsonDocument.Parse(File.ReadAllText(recordPath));
        string releaseId = record.RootElement.GetProperty("ReleaseId").GetString()!;
        That(record.RootElement.GetProperty("Phase").GetString() == "ManifestActivated", "Wrong recovery phase: " + failed.Text);
        string directory = Path.GetDirectoryName(recordPath)!;
        byte[] manifestBefore = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
        fixture.BlockManifest = false;
        var resumed = await Invoke(script, ["-Resume", releaseId, "-LocalRoot", fixture.Public]); That(resumed.Exit == 0, "Resume failed: " + resumed.Text);
        var repeated = await Invoke(script, ["-Resume", releaseId, "-LocalRoot", fixture.Public]); That(repeated.Exit == 0, "Repeated resume failed: " + repeated.Text);
        That(manifestBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "manifest.json"))), "Resume rebuilt/re-signed artifacts");
        using (var setup = ZipFile.OpenRead(Directory.EnumerateFiles(directory, "*-setup.zip").Single()))
        {
            That(setup.Entries.Count == 5 && setup.GetEntry("update-settings.json") != null, "First-install package incomplete");
        }
        var renewed = await Invoke(script, [.. initial, "-ResignRelease", releaseId]); That(renewed.Exit == 0, "Renewal failed: " + renewed.Text);
        Console.WriteLine("PASS publisher: key mismatch, first install, public failure, immutable resume, repeated resume, renewal, full download and Range");
    }
    private static async Task<(int Exit, string Text)> Invoke(string script, string[] args)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-NoProfile", "-File", script }.Concat(args)) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        return (process.ExitCode, await output + await error);
    }
}
