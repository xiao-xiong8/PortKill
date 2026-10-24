using PortKiller.UpdateTool;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class StoreChecks
{
    internal static Task RecoveryAndConflicts()
    {
        foreach (string phase in new[] { "staged", "package-published", "manifest-activated", "receipt-written" })
        {
            using var fixture = new Fixture(); var package = fixture.Package(); var staged = fixture.Stage("1.0.0", 10, package);
            var store = new ReleaseStore(fixture.Public, fixture.Options);
            try { store.Activate(staged.Manifest, staged.Package, "none", current => { if (current == phase) throw new IOException("Injected crash: " + phase); }); }
            catch (IOException) { }
            var receipt = store.Activate(staged.Manifest, staged.Package, "none");
            That(store.Inspect(true)?.ManifestSha256 == receipt.ManifestSha256, "Recovery not idempotent");
            var renewal = fixture.Stage("1.0.0", 11, package);
            bool rejected = false;
            try { store.Activate(renewal.Manifest, renewal.Package, "none"); } catch (InvalidOperationException) { rejected = true; }
            That(rejected, "CAS conflict ignored");
            store.Activate(renewal.Manifest, renewal.Package, receipt.ManifestSha256);
            var overwritten = fixture.Stage("1.0.0", 12, fixture.Package(dependencyByte: 9));
            rejected = false;
            try { store.Activate(overwritten.Manifest, overwritten.Package, store.Inspect()!.ManifestSha256); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "Same-version ZIP overwrite accepted");
            rejected = false; try { store.Activate(staged.Manifest, staged.Package, store.Inspect()!.ManifestSha256); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "Sequence rollback accepted");
            File.WriteAllBytes(Path.Combine(fixture.Public, receipt.PackageName), [9]);
            rejected = false; try { store.Inspect(true); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "Corrupt public ZIP accepted");
        }
        return Task.CompletedTask;
    }
    internal static async Task ConcurrentLock()
    {
        using var fixture = new Fixture(); var staged = fixture.Stage("1.0.0", 10, fixture.Package()); var store = new ReleaseStore(fixture.Public, fixture.Options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => store.Activate(staged.Manifest, staged.Package, "none", phase => { if (phase == "staged") { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); bool rejected = false;
        try { store.Activate(staged.Manifest, staged.Package, "none"); } catch (IOException) { rejected = true; }
        finally { release.Set(); }
        await first; That(rejected, "Concurrent publisher did not take exclusive lock");
    }
}
