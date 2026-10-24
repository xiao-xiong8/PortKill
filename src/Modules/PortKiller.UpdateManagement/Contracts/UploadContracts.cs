using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Contracts;

public sealed record CreateUploadRequest(long ManifestBytes, long PackageBytes);
public sealed record UploadSessionResponse(string Id, UploadState State, long ManifestExpectedBytes, long ManifestReceivedBytes,
    long PackageExpectedBytes, long PackageReceivedBytes, long ExpiresUtcMs);
public sealed record UploadCompletionResponse(string UploadId, UploadState State, string DraftId, string JobId);
public sealed record ValidationTaskResponse(string Id, string DraftId, PublishJobState State, int Attempts,
    long CreatedUtcMs, long? LeaseExpiresUtcMs, string? ErrorCode,
    ValidationState ValidationState = ValidationState.Pending, string? Version = null, long? Sequence = null,
    string? ManifestSha256 = null, string? PackageSha256 = null, string[]? ReleaseNotes = null,
    long? ValidatedUtcMs = null);

public enum UploadPart { Manifest, Package }
