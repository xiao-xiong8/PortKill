using System;
using System.IO;
using System.Text.Json;

namespace PortKiller.Updates;

/// <summary>安装目录中的本地回执；它只用于提示和清理，不作为发布信任来源。</summary>
public sealed class UpdateResult
{
    public int Schema { get; set; } = 1;
    public string OperationId { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTimeOffset CompletedUtc { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string WorkerName { get; set; } = "";
    public string RecoveryWorkerName { get; set; } = "";
    public string Detail { get; set; } = "";

    public string Message => Outcome == "installed" ? $"已更新至 v{ToVersion}"
        : Outcome == "rolled_back" ? $"版本 {ToVersion} 安装失败，已恢复旧版本。"
        : "检测到中断的更新，已恢复可用版本。";

    internal static void Write(string root, UpdateJournal journal, string outcome, string detail = "", string recoveryWorkerName = "")
    {
        var result = new UpdateResult { OperationId = journal.Token, FromVersion = journal.PreviousVersion,
            ToVersion = journal.Version, Outcome = outcome, CompletedUtc = DateTimeOffset.UtcNow,
            PackageSha256 = journal.PackageSha256, WorkerName = journal.WorkerName,
            RecoveryWorkerName = recoveryWorkerName, Detail = detail };
        string path = Path.Combine(root, "result.json"), temporary = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, UpdateJsonContext.Default.UpdateResult);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
        // 旧客户端仍可显示本次结果，新的读取端优先 result.json。
        try { File.WriteAllText(Path.Combine(root, "result.txt"), result.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UpdateLog.Write("UpdateLegacyResult", ex); }
    }

    internal static UpdateResult? Read(string root, string currentVersion)
    {
        string path = Path.Combine(root, "result.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("更新结果文件过大。");
        var result = JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.UpdateResult);
        if (result == null || result.Schema != 1 || !Guid.TryParseExact(result.OperationId, "N", out _)
            || result.FromVersion == null || result.ToVersion == null || result.PackageSha256 == null
            || result.WorkerName == null || result.RecoveryWorkerName == null
            || result.Outcome is not ("installed" or "rolled_back" or "interrupted")
            || result.CompletedUtc == default || result.CompletedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("更新结果记录无效。");
        var current = UpdateManifest.ParseVersion(currentVersion);
        var target = UpdateManifest.ParseVersion(result.ToVersion);
        if (result.Outcome == "installed" && target != current) return null;
        if (result.Outcome != "installed" && result.FromVersion.Length > 0
            && UpdateManifest.ParseVersion(result.FromVersion) != current) return null;
        return result;
    }
}
