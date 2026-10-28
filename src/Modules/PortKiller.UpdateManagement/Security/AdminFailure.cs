namespace PortKiller.UpdateManagement.Security;

public sealed class AdminFailure(string code, int status, string safeMessage, int? retryAfterSeconds = null) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
