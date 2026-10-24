namespace PortKiller.Updates;

public sealed record ReleaseIdentity(string Version, long Sequence, string PayloadSha256, string PackageSha256)
{
    public static ReleaseIdentity From(UpdateManifest manifest) => new(
        UpdateManifest.ParseVersion(manifest.Version).ToString(4), manifest.Sequence,
        manifest.PayloadSha256.ToUpperInvariant(), manifest.Sha256.ToUpperInvariant());
}

public sealed record PreparedUpdate(UpdateManifest Manifest, string PackagePath, ReleaseIdentity Identity);
