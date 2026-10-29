using System;

namespace PortKiller.Services;

/// <summary>分开保存用户意愿与系统状态，注册失败不应把默认开启写成永久关闭。</summary>
internal sealed class AutoStartPreference(Func<bool> readEnabled, Func<bool, bool> writeEnabled)
{
    internal bool Requested { get; private set; } = true;
    internal bool Enabled => readEnabled();

    internal void Load(bool requested) => Requested = requested;

    internal bool Change(bool requested, Func<bool> persist)
    {
        bool previousEnabled = Enabled;
        if (!writeEnabled(requested)) return false;

        bool previousRequested = Requested;
        Requested = requested;
        if (persist()) return true;

        // 设置文件写入失败时恢复旧启动项，避免界面关闭、下次启动又被旧偏好开启。
        Requested = previousRequested;
        if (!writeEnabled(previousEnabled))
            PortKiller.Diagnostics.DiagnosticLog.Write("AutoStartRollback",
                new InvalidOperationException("设置保存失败，且无法恢复先前的开机启动状态。"));
        return false;
    }
}
