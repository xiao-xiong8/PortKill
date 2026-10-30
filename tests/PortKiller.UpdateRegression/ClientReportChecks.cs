using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class ClientReportChecks
{
    internal static async Task Run()
    {
        await ClientReportRetryChecks.Run();
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "client-identity.json");
        var store = new ClientIdentityStore(path);
        string id, secret;
        using (store.AcquireLease())
        {
            var identity = store.LoadOrCreate(); id = identity.InstallationId; secret = identity.ClientToken;
            That(!File.ReadAllText(path).Contains(secret), "Client token stored in plaintext");
            That(store.LoadOrCreate().InstallationId == id && store.LoadOrCreate().ClientToken == secret, "Identity did not survive restart");
        }
        int registrations = 0, reports = 0;
        var results = new List<string>();
        using (var service = new ClientReportService(new Uri("https://updates.example.com/api/v1/client-installations/"), store: store,
            handler: new Handler(async (request, token) =>
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (request.RequestUri!.AbsolutePath.EndsWith("register"))
                {
                    registrations++;
                    That(payload.RootElement.GetProperty("clientToken").GetString() == secret, "Registration lost locally generated token");
                    return new(HttpStatusCode.Created);
                }
                reports++;
                That(request.Headers.Authorization?.Parameter == secret, "Report credential missing");
                That(payload.RootElement.EnumerateObject().Count() == 3, "Report collected extra fields");
                That(payload.RootElement.GetProperty("version").GetString() == "1.2.3", "Wrong report version");
                results.Add(payload.RootElement.GetProperty("result").GetString()!);
                return new(HttpStatusCode.NoContent);
            })))
            foreach (string result in new[] { "started", "installed", "rolled_back", "failed" }) await service.ReportAsync("1.2.3", result);
        That(registrations == 1 && reports == 4 && results.SequenceEqual(new[] { "started", "installed", "rolled_back", "failed" }), "Wrong report lifecycle");
        int calls = 0;
        using (var offline = new ClientReportService(new Uri("https://updates.example.com/"), store: store,
            handler: new Handler((_, _) => { calls++; throw new HttpRequestException("Offline"); })))
            await offline.ReportAsync("1.2.3", "started");
        That(calls == 3, "Network retries are not bounded to two");
        calls = 0;
        var ids = new List<string>();
        using (var revoked = new ClientReportService(new Uri("https://updates.example.com/"), store: store,
            handler: new Handler(async (request, token) =>
            {
                calls++;
                if (request.RequestUri!.AbsolutePath.EndsWith("register"))
                {
                    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    ids.Add(json.RootElement.GetProperty("installationId").GetString()!);
                    return new(HttpStatusCode.Created);
                }
                return new(HttpStatusCode.Unauthorized);
            })))
        { await revoked.ReportAsync("1.2.3", "started"); await revoked.ReportAsync("1.2.3", "installed"); }
        That(ids.Count == 1 && ids[0] != id && calls == 4, "Revocation entered an unbounded registration loop");
        calls = 0;
        var broken = new ClientIdentityStore(path, bytes => bytes, _ => throw new CryptographicException("DPAPI unavailable"));
        using (var service = new ClientReportService(new Uri("https://updates.example.com/"), store: broken,
            handler: new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); })))
            await service.ReportAsync("1.2.3", "started");
        That(calls == 0, "DPAPI failure did not skip reporting safely");
        That(new UpdateOptions().GetClientReportUri() == null, "Old configuration did not disable reporting");

        string officialLegacyRoot = Path.Combine(fixture.Root, "official-legacy-settings");
        Directory.CreateDirectory(officialLegacyRoot);
        var officialLegacy = new UpdateOptions
        {
            Enabled = true,
            UseSystemProxy = false,
            AllowHttp = false,
            ManifestUrl = "https://xiaoxiongupdate.duckdns.org/stable/manifest.json",
            PublicKeyPem = fixture.Options.PublicKeyPem,
            RealtimeEnabled = true,
            PollSeconds = 300,
            CheckTimeoutSeconds = 8,
            DownloadTimeoutSeconds = 180
        };
        var officialJson = JsonNode.Parse(JsonSerializer.Serialize(officialLegacy, UpdateJsonContext.Default.UpdateOptions))!.AsObject();
        officialJson.Remove(nameof(UpdateOptions.ClientReportUrl));
        string officialLegacyPath = Path.Combine(officialLegacyRoot, "update-settings.json");
        string officialLegacyText = officialJson.ToJsonString();
        File.WriteAllText(officialLegacyPath, officialLegacyText);
        var migrated = UpdateOptions.Load(officialLegacyRoot);
        That(migrated.GetClientReportUri()?.AbsoluteUri == "https://xiaoxiongupdate.duckdns.org/api/v1/client-installations/",
            "Legacy official settings did not receive the embedded client report endpoint");
        That(migrated.PollSeconds == 30, "Legacy official settings did not preserve the production polling migration");
        That(File.ReadAllText(officialLegacyPath) == officialLegacyText, "Automatic report migration rewrote the user's settings file");

        string customLegacyRoot = Path.Combine(fixture.Root, "custom-legacy-settings");
        Directory.CreateDirectory(customLegacyRoot);
        var customLegacy = new UpdateOptions
        {
            Enabled = true,
            AllowHttp = false,
            ManifestUrl = "https://custom-updates.example/stable/manifest.json",
            PublicKeyPem = fixture.Options.PublicKeyPem
        };
        var customJson = JsonNode.Parse(JsonSerializer.Serialize(customLegacy, UpdateJsonContext.Default.UpdateOptions))!.AsObject();
        customJson.Remove(nameof(UpdateOptions.ClientReportUrl));
        File.WriteAllText(Path.Combine(customLegacyRoot, "update-settings.json"), customJson.ToJsonString());
        That(UpdateOptions.Load(customLegacyRoot).GetClientReportUri() == null,
            "Custom legacy settings unexpectedly inherited the official report endpoint");

        string optOutRoot = Path.Combine(fixture.Root, "official-opt-out-settings");
        Directory.CreateDirectory(optOutRoot);
        var optOutJson = JsonNode.Parse(JsonSerializer.Serialize(officialLegacy, UpdateJsonContext.Default.UpdateOptions))!.AsObject();
        optOutJson[nameof(UpdateOptions.ClientReportUrl)] = "";
        File.WriteAllText(Path.Combine(optOutRoot, "update-settings.json"), optOutJson.ToJsonString());
        That(UpdateOptions.Load(optOutRoot).GetClientReportUri() == null,
            "An explicit empty ClientReportUrl did not remain an opt-out");

        string install = Path.Combine(fixture.Root, "install"); Directory.CreateDirectory(install);
        var scoped = new ClientIdentityStore(Path.Combine(fixture.Root, "scoped-identity.json"), install);
        using (scoped.AcquireLease())
        {
            string firstId = scoped.LoadOrCreate().InstallationId;
            That(scoped.LoadOrCreate().InstallationId == firstId, "Installation did not reuse identity");
            File.WriteAllText(Path.Combine(install, "PortKiller.exe"), "replacement binary");
            That(scoped.LoadOrCreate().InstallationId == firstId, "Replacing executable changed identity");
            File.Delete(Path.Combine(install, ".portkiller-client-instance"));
            That(scoped.LoadOrCreate().InstallationId != firstId, "Reinstallation reused removed installation identity");
        }
        foreach (var (outcome, expected, current) in new[] { ("installed", "installed", "2.0"), ("rolled_back", "rolled_back", "1.0"), ("interrupted", "failed", "1.0") })
        {
            string root = Path.Combine(install, ".portkiller-update"); Directory.CreateDirectory(root);
            var receipt = new UpdateResult { OperationId = Guid.NewGuid().ToString("N"), FromVersion = "1.0", ToVersion = "2.0", Outcome = outcome, CompletedUtc = DateTimeOffset.UtcNow };
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(receipt, UpdateJsonContext.Default.UpdateResult));
            var notice = await UpdateMaintenance.RunAsync(install, Path.Combine(fixture.Root, "cache"), current, default);
            That(notice?.Outcome == expected, "Incorrect completion report outcome: " + outcome);
        }
        var options = fixture.Options; options.ClientReportUrl = "https://user:secret@example.com/report";
        options.Validate(); // Reporting URL must not invalidate existing update configuration.
        try { options.GetClientReportUri(); throw new Exception("Unsafe report address accepted"); } catch (InvalidDataException) { }
    }
}
