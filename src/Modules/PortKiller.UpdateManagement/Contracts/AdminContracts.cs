namespace PortKiller.UpdateManagement.Contracts;

public enum UploadState { Uploading, Uploaded, Cancelled, Expired }
public enum ValidationState { Pending, Validating, ReadyForApproval, Rejected }
public enum PublishJobState { Queued, Publishing, ManifestActivated, PublicVerifying, Completed, ActiveUnverified, Failed, Conflict, Superseded }

public sealed record LiveHealthResponse(string Status, string Component);
public sealed record SystemStatusResponse(string Application, string Version, int ContractVersion);
public sealed record CapabilitiesResponse(UploadState[] UploadStates, ValidationState[] ValidationStates, PublishJobState[] PublishStates);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);
public sealed record ReleaseToolError(int Schema, string Code, string Message);

public sealed record PublishChallengeResponse(string Id, string DraftId, string Application, string Channel,
    string Version, long Sequence, string ManifestSha256, string PackageSha256, string[] ReleaseNotes,
    string ExpectedCurrentManifestSha256, long ExpiresUtcMs);

public sealed record PublishConfirmationRequest(string ChallengeId, string? IdempotencyKey = null);

public sealed record PublishJobResponse(string Id, string DraftId, string Application, string Channel,
    PublishJobState State, string Version, long Sequence, string ManifestSha256, string PackageSha256,
    string ExpectedCurrentManifestSha256, long CreatedUtcMs, string? ErrorCode = null);

public static class AdminContract
{
    public const int Version = 1;
    public const int ToolErrorSchema = 1;
    public const string ToolJsonErrorsArgument = "--json-errors";
    public static CapabilitiesResponse Capabilities => new(
        Enum.GetValues<UploadState>(), Enum.GetValues<ValidationState>(), Enum.GetValues<PublishJobState>());
}

public static class AdminErrorCodes
{
    public const string AuthenticationRequired = "authentication_required";
    public const string PermissionDenied = "permission_denied";
    public const string InvalidRequest = "invalid_request";
    public const string CsrfInvalid = "csrf_invalid";
    public const string NotFound = "resource_not_found";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
}
