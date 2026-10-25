using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PortKiller.Release.Tests;

internal sealed class PreparationFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PortKillReleaseTests", Guid.NewGuid().ToString("N"));
    public static string Repository => FindRepository();
    public static string Prepared => Environment.GetEnvironmentVariable("PORTKILL_RELEASE_AOT_DIRECTORY")
        ?? throw new InvalidOperationException("Run the release checks with a real desktop AOT directory in PORTKILL_RELEASE_AOT_DIRECTORY.");
    public string Version => FileVersionInfo.GetVersionInfo(Path.Combine(Prepared, "PortKiller.exe")).FileVersion!;
    public string PrivateKey => Path.Combine(Root, "private.pem");
    public string PublicKey => Path.Combine(Root, "public.pem");
    public string Fingerprint { get; }
    public string NetworkGuard => Path.Combine(Root, "unexpected-network.txt");
    public string RecordsRoot => Path.Combine(Repository, ".local", "artifacts", "releases");
    public PreparationFixture()
    {
        Directory.CreateDirectory(Root);
        using var key = RSA.Create(2048);
        File.WriteAllText(PrivateKey, key.ExportRSAPrivateKeyPem()); File.WriteAllText(PublicKey, key.ExportSubjectPublicKeyInfoPem());
        Fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
    public string[] PrepareArguments(string source = "https://updates.example/stable/") =>
    ["-PrepareOnly", "-Version", Version, "-PreparedPublishDirectory", Prepared, "-SigningKeyPath", PrivateKey,
        "-PublicKeyPath", PublicKey, "-PublicBaseUrl", source, "-ReleaseNotesPath",
        Path.Combine(Repository, "deploy", "examples", "release-notes.txt"), "-AllowDirty"];

    public async Task<(int Code, string Text)> RunAsync(string[] arguments, bool networkGuard = true)
    {
        string argumentsFile = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(argumentsFile, JsonSerializer.Serialize(arguments));
        string wrapper = Path.ChangeExtension(argumentsFile, ".ps1");
        await File.WriteAllTextAsync(wrapper, """
            $ErrorActionPreference = 'Stop'
            $p5Arguments = @(Get-Content -LiteralPath $env:PORTKILL_RELEASE_TEST_ARGUMENTS -Raw | ConvertFrom-Json)
            $p5Bound = @{}
            for ($p5Index = 0; $p5Index -lt $p5Arguments.Count; $p5Index++) {
                $p5Name = $p5Arguments[$p5Index].TrimStart('-')
                if ($p5Name -in @('PrepareOnly','AllowDirty','AllowHttp')) { $p5Bound[$p5Name] = $true }
                else { $p5Index++; $p5Bound[$p5Name] = $p5Arguments[$p5Index] }
            }
            $env:PORTKILL_RELEASE_TEST_REAL_DOTNET = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            if ($env:PORTKILL_RELEASE_TEST_GUARD -eq 'true') {
                function global:ssh { [IO.File]::WriteAllText($env:PORTKILL_RELEASE_TEST_NETWORK, 'ssh'); throw 'Unexpected ssh.' }
                function global:scp { [IO.File]::WriteAllText($env:PORTKILL_RELEASE_TEST_NETWORK, 'scp'); throw 'Unexpected scp.' }
                function global:dotnet {
                    if ($args.Count -gt 1 -and $args[1] -in @('inspect','activate','recover','public-check')) {
                        [IO.File]::WriteAllText($env:PORTKILL_RELEASE_TEST_NETWORK, [string]$args[1]); throw 'Unexpected delivery command.'
                    }
                    & $env:PORTKILL_RELEASE_TEST_REAL_DOTNET @args
                }
            }
            & $env:PORTKILL_RELEASE_TEST_SCRIPT @p5Bound
            """);
        var start = new ProcessStartInfo("pwsh")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Repository };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(wrapper);
        start.Environment["PORTKILL_RELEASE_TEST_ARGUMENTS"] = argumentsFile;
        start.Environment["PORTKILL_RELEASE_TEST_SCRIPT"] = Path.Combine(Repository, "scripts", "release", "Publish-Update.ps1");
        start.Environment["PORTKILL_RELEASE_TEST_NETWORK"] = NetworkGuard;
        start.Environment["PORTKILL_RELEASE_TEST_GUARD"] = networkGuard ? "true" : "false";
        using var process = Process.Start(start)!;
        Task<string> stdout = CaptureAsync(process.StandardOutput), stderr = CaptureAsync(process.StandardError);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await stdout + await stderr);
    }
    public string LatestDirectory()
        => OwnedDirectories().OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("No matching local release record was produced.");
    private IEnumerable<string> OwnedDirectories()
    {
        if (!Directory.Exists(RecordsRoot)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(RecordsRoot))
        {
            string path = Path.Combine(directory, "release-record.json");
            if (!File.Exists(path)) continue;
            using var record = JsonDocument.Parse(File.ReadAllText(path));
            if (record.RootElement.TryGetProperty("KeyFingerprint", out var key) && key.GetString() == Fingerprint) yield return directory;
        }
    }
    public ValueTask DisposeAsync()
    {
        foreach (string directory in OwnedDirectories().ToArray()) DeleteInside(RecordsRoot, directory);
        DeleteInside(Path.Combine(Path.GetTempPath(), "PortKillReleaseTests"), Root);
        return ValueTask.CompletedTask;
    }
    private static void DeleteInside(string parent, string target)
    {
        string full = Path.GetFullPath(target);
        if (!full.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Release test cleanup outside its sandbox.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static async Task<string> CaptureAsync(StreamReader input)
    {
        var text = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
            if (text.Length < 131_072) text.Append(buffer, 0, Math.Min(count, 131_072 - text.Length));
        return text.ToString();
    }
    private static string FindRepository()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException();
    }
}
