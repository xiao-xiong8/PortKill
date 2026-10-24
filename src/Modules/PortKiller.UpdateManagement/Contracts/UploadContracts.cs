using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Contracts;

public sealed record CreateUploadRequest(long ManifestBytes, long PackageBytes);
public sealed record UploadSessionResponse(string Id, UploadState State, long ManifestExpectedBytes, long ManifestReceivedBytes,
    long PackageExpectedBytes, long PackageReceivedBytes, long ExpiresUtcMs);
public sealed record UploadCompletionResponse(string UploadId, UploadState State);

public enum UploadPart { Manifest, Package }
