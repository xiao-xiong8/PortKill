using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateAdmin.Api;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

// Only the browser test runner launches this executable. Production API has no seed or test endpoint.
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Browser evidence directory required.");
string runtime = args[0]; Directory.CreateDirectory(runtime);
string root = Path.Combine(runtime, "run-" + Guid.NewGuid().ToString("N"));
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
});
using (var scope = app.Services.CreateScope())
{
    await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
    await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync("browser-admin", "Browser-Test-Only!492");
}
await app.RunAsync();
