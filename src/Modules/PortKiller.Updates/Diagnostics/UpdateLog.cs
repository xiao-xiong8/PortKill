using System;

namespace PortKiller.Updates;

/// <summary>日志由宿主注入，模块不依赖 Avalonia 或主应用。</summary>
public static class UpdateLog
{
    public static Action<string, Exception?>? Sink { get; set; }
    internal static void Write(string source, Exception error) => Sink?.Invoke(source, error);
}
