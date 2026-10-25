namespace PortKiller.UpdateManagement.Contracts;

// IDs are opaque strings scoped to one job, so JavaScript never rounds a 64-bit sequence.
public sealed record JobSnapshotResponse(string Id, string Kind, string EventId, long ObservedUtcMs,
    PublishJobResponse? Publication, ValidationTaskResponse? Validation);
public sealed record JobEventResponse(string Id, string Stage, string Code, long CreatedUtcMs);
public sealed record JobEventPageResponse(JobSnapshotResponse Snapshot, bool Reset, string Cursor, bool HasMore,
    IReadOnlyList<JobEventResponse> Events);
