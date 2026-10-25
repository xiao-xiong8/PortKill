using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateAdmin.Api;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.Updates;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;

// Only the browser test runner launches this executable. Production API has no seed or test endpoint.
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Browser evidence directory required.");
string runtime = args[0]; Directory.CreateDirectory(runtime);
string root = Path.Combine(runtime, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var signing = RSA.Create(2048);
var tool = new UpdateToolOptions { ReleaseRoot = Path.Combine(root, "public"), VerificationConfigPath = Path.Combine(root, "verification.json") };
string repository = Directory.GetCurrentDirectory();
string extension = OperatingSystem.IsWindows() ? ".exe" : "";
tool.ExecutablePath = Path.Combine(repository, "src/Tools/PortKiller.UpdateTool/bin/Release/net10.0/PortKiller.UpdateTool" + extension);
Directory.CreateDirectory(tool.ReleaseRoot);
await File.WriteAllTextAsync(tool.VerificationConfigPath, JsonSerializer.Serialize(new ManifestVerificationOptions {
    ManifestUrl = "http://127.0.0.1:15082/stable/manifest.json", AllowHttp = true, PublicKeyPem = signing.ExportSubjectPublicKeyInfoPem()
}, ProtocolJsonContext.Default.ManifestVerificationOptions));
using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
await File.WriteAllTextAsync(Path.Combine(runtime, "certificate.pem"), generated.ExportCertificatePem());
await File.WriteAllTextAsync(Path.Combine(runtime, "key.pem"), key.ExportPkcs8PrivateKeyPem());
await using var app = AdminApiHost.Create(["--urls", "https://127.0.0.1:15081", "--Administration:DataRoot", root], builder =>
{
    builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(tool);
});
using (var scope = app.Services.CreateScope())
{
    await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
    await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync("browser-admin", "Browser-Test-Only!492");
}
string fixtures = Path.Combine(runtime, "fixtures"); Directory.CreateDirectory(fixtures);
string? prepared = Environment.GetEnvironmentVariable("PORTKILL_BROWSER_AOT_DIRECTORY");
string executable = prepared == null ? tool.ExecutablePath : Path.Combine(prepared, "PortKiller.exe");
string version = UpdateManifest.ParseVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion!).ToString();
string package = Path.Combine(fixtures, "package.zip");
using (var zip = new ZipArchive(File.Create(package), ZipArchiveMode.Create))
    foreach (string name in ReleasePackage.Files)
    {
        using Stream output = zip.CreateEntry(name).Open();
        if (prepared != null) { using var input = File.OpenRead(Path.Combine(prepared, name)); input.CopyTo(output); }
        else if (name == "PortKiller.exe") { using var input = File.OpenRead(executable); input.CopyTo(output); }
        else output.Write([1, 2, 3, 4]);
    }
byte[] packageBytes = await File.ReadAllBytesAsync(package);
var manifest = new UpdateManifest { Schema = 2, Version = version, Sequence = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30), PackageUrl = $"http://127.0.0.1:15082/stable/PortKiller-{version}-win-x64.zip", Size = packageBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)), ReleaseNotes = ["Browser integration release", "<img src=x onerror=window.__releaseXss=1>"] };
byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
await File.WriteAllTextAsync(Path.Combine(fixtures, "manifest.json"), JsonSerializer.Serialize(new SignedUpdate { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(signing.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }, ProtocolJsonContext.Default.SignedUpdate));
packageBytes[^1] ^= 1; await File.WriteAllBytesAsync(Path.Combine(fixtures, "tampered.zip"), packageBytes);
await File.WriteAllTextAsync(Path.Combine(runtime, "fixture-info.json"), JsonSerializer.Serialize(new { version, genuineAot = prepared != null, packageSha256 = manifest.Sha256 }));
var publicBuilder = WebApplication.CreateBuilder(); publicBuilder.WebHost.UseUrls("http://127.0.0.1:15082"); publicBuilder.Logging.ClearProviders();
await using var distribution = publicBuilder.Build();
distribution.MapGet("/stable/{name}", (string name) => name == "manifest.json" || name == $"PortKiller-{version}-win-x64.zip"
    ? Results.File(Path.Combine(tool.ReleaseRoot, name), enableRangeProcessing: true) : Results.NotFound());
await distribution.StartAsync();
var start = new ProcessStartInfo(Path.Combine(repository, "src/Services/PortKiller.UpdatePublisher.Worker/bin/Release/net10.0/PortKiller.UpdatePublisher.Worker" + extension)) { UseShellExecute = false, CreateNoWindow = true };
start.Environment.Clear();
foreach (string variable in new[] { "SYSTEMROOT", "WINDIR", "TEMP", "TMP" }) if (Environment.GetEnvironmentVariable(variable) is { } value) start.Environment[variable] = value;
foreach (string argument in new[] { "--Administration:DataRoot", root, "--UpdateTool:Enabled", "true", "--UpdateTool:ExecutablePath", tool.ExecutablePath, "--UpdateTool:VerificationConfigPath", tool.VerificationConfigPath, "--UpdateTool:ReleaseRoot", tool.ReleaseRoot, "--Logging:LogLevel:Default", "Warning" }) start.ArgumentList.Add(argument);
using var worker = Process.Start(start)!;
try { await app.RunAsync(); }
finally { if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); } }
