namespace PortKiller.Models;

public enum ProcessMetadataState { Pending, Available, Unavailable }

/// <summary>共享不可变文件元数据；无界面状态，多个进程及连接共用同一结果。</summary>
public sealed record ProcessMetadata(string ApplicationName, string FileDescription, string ProductName,
    ProcessMetadataState State)
{
    public static ProcessMetadata Pending { get; } = new("", "", "", ProcessMetadataState.Pending);
    public static ProcessMetadata Unavailable { get; } = new("", "", "", ProcessMetadataState.Unavailable);
}
