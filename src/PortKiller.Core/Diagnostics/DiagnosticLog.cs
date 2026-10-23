using System;

namespace PortKiller.Diagnostics;

/// <summary>宿主在启动时提供日志出口，底层项目不依赖桌面 Application。</summary>
public static class DiagnosticLog
{
    public static Action<string, Exception?>? Sink { get; set; }

    public static void Write(string source, Exception? error)
    {
        try { Sink?.Invoke(source, error); }
        catch { /* 日志失败不能改变系统操作或启动结果。 */ }
    }
}
