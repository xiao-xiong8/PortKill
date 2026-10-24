using Microsoft.AspNetCore.Identity;
using PortKiller.UpdateManagement.Contracts;

namespace PortKiller.UpdateManagement.Persistence.Entities;

public sealed class AdminUser : IdentityUser
{
    public bool Disabled { get; set; }
    public bool RecoveryCodesAcknowledged { get; set; }
    public long CreatedUtcMs { get; set; }
}

public enum SessionPhase { EnrollmentRequired, RecoveryAcknowledgementRequired, MfaRequired, Full }

public sealed class AdminSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public SessionPhase Phase { get; set; }
    public string AuthenticationMethod { get; set; } = "password";
    public long CreatedUtcMs { get; set; }
    public long LastSeenUtcMs { get; set; }
    public long AbsoluteExpiresUtcMs { get; set; }
    public long? StrongAuthenticationUtcMs { get; set; }
    public long? RevokedUtcMs { get; set; }
}

public sealed class TotpConsumption
{
    public string UserId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long ExpiresUtcMs { get; set; }
}

public interface IVersionedEntity { long Revision { get; set; } }

public sealed class UploadSession : IVersionedEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string StorageId { get; set; } = Guid.NewGuid().ToString("N");
    public UploadState State { get; set; }
    public long ReceivedBytes { get; set; }
    public long ManifestExpectedBytes { get; set; }
    public long ManifestReceivedBytes { get; set; }
    public string? ManifestSha256 { get; set; }
    public long PackageExpectedBytes { get; set; }
    public long PackageReceivedBytes { get; set; }
    public string? PackageSha256 { get; set; }
    public long CreatedUtcMs { get; set; }
    public long LastActivityUtcMs { get; set; }
    public long ExpiresUtcMs { get; set; }
    public long? StorageCleanedUtcMs { get; set; }
    public long Revision { get; set; }
}

public sealed class ReleaseDraft : IVersionedEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string UploadId { get; set; } = "";
    public string ArchiveId { get; set; } = Guid.NewGuid().ToString("N");
    public string Application { get; set; } = "PortKill";
    public string Channel { get; set; } = "stable";
    public string Runtime { get; set; } = "win-x64";
    public string? Version { get; set; }
    public long? Sequence { get; set; }
    public string? ManifestSha256 { get; set; }
    public string? PackageSha256 { get; set; }
    public string ReleaseNotesJson { get; set; } = "[]";
    public ValidationState ValidationState { get; set; }
    public string? ValidationErrorCode { get; set; }
    public long? ValidatedUtcMs { get; set; }
    public long? ManifestExpiresUtcMs { get; set; }
    public long? ArchiveCleanedUtcMs { get; set; }
    public long CreatedUtcMs { get; set; }
    public long Revision { get; set; }
}

public sealed class ReleaseJob : IVersionedEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DraftId { get; set; } = "";
    public string RequestedById { get; set; } = "";
    public string Kind { get; set; } = "validate";
    public string Application { get; set; } = "PortKill";
    public string Channel { get; set; } = "stable";
    public PublishJobState State { get; set; }
    public string? ExpectedManifestSha256 { get; set; }
    public string? LeaseOwner { get; set; }
    public long? LeaseExpiresUtcMs { get; set; }
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }
    public string? ReceiptSha256 { get; set; }
    public long CreatedUtcMs { get; set; }
    public long Revision { get; set; }
}

public sealed class PublishApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DraftId { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string Version { get; set; } = "";
    public long Sequence { get; set; }
    public string ExpectedManifestSha256 { get; set; } = "none";
    public long ExpiresUtcMs { get; set; }
    public long? ConsumedUtcMs { get; set; }
}

public sealed class JobEvent
{
    public string JobId { get; set; } = "";
    public long Sequence { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public long CreatedUtcMs { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public string? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string? ResourceId { get; set; }
    public string Result { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string? RemoteAddress { get; set; }
    public long CreatedUtcMs { get; set; }
}

public sealed class IdempotencyRequest
{
    public string UserId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string RequestSha256 { get; set; } = "";
    public string? ResultResourceId { get; set; }
    public long CreatedUtcMs { get; set; }
    public long ExpiresUtcMs { get; set; }
}
