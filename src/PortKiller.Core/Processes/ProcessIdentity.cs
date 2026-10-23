namespace PortKiller.Models;

/// <summary>创建时间为 Windows FILETIME；0 表示无法验证，不能据此复用应用信息。</summary>
public readonly record struct ProcessIdentity(int Pid, long CreationTime)
{
    public bool IsVerified => Pid > 4 && CreationTime > 0;
}

public readonly record struct ProcessReference(int Pid, string ProcessName);
public sealed record ProcessObservation(ProcessIdentity Identity, string ProcessName, string? ProcessPath);
