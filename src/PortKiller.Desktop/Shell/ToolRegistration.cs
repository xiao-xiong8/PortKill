using System;
using Avalonia.Controls;

namespace PortKiller.Shell;

/// <summary>静态注册的工具入口。新工具使用 ViewFactory，既有页面保留索引兼容映射。</summary>
public sealed record ToolRegistration(string Id, string Title, string IconPath,
    int? LegacyIndex = null, Func<Control>? ViewFactory = null);

public interface IToolPageLifecycle
{
    void Activate();
    void Deactivate();
}
