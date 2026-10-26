using System;

namespace PortKiller.Updates;

public enum UpdateStage { Idle, Disabled, Checking, Available, Downloading, Paused, Verifying, Ready, Latest, Failed, NoRelease }
public enum UpdateFailure { None, Network, Server, Integrity, Storage, Configuration, Unknown }

/// <summary>与 UI 框架无关的更新快照。下载进度至多每 200 ms 发布一次。</summary>
public sealed record UpdateSnapshot(UpdateStage Stage, string Message, UpdateManifest? Manifest = null,
    long DownloadedBytes = 0, double BytesPerSecond = 0, int Attempt = 1,
    UpdateFailure Failure = UpdateFailure.None, string ErrorDetail = "")
{
    public bool IsBusy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying;
    public double Percent => Manifest?.Size > 0 ? Math.Clamp(100d * DownloadedBytes / Manifest.Size, 0, 100) : 0;
    public TimeSpan? Remaining => BytesPerSecond > 0 && Manifest is { } manifest
        ? TimeSpan.FromSeconds(Math.Max(0, manifest.Size - DownloadedBytes) / BytesPerSecond) : null;
}
