using System;
using Avalonia.Controls;

namespace PortKiller.Views;

/// <summary>
/// 连接列表专用容器：直接回收完整行模板，避免默认 ContentPresenter 清空模板后重建绑定树。
/// 缓存大小由 ConnectionViewportPanel 的视口决定，不缓存全部连接行。
/// </summary>
public sealed class ConnectionItemsControl : ItemsControl
{
    public ConnectionItemsControl() => Focusable = true;
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => ItemTemplate?.Build(item) ?? throw new InvalidOperationException("连接列表必须配置行模板。");

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        // 不让键盘焦点留在一个已经换绑到其他端口的“终止/释放”按钮上。
        if (container.IsKeyboardFocusWithin && !ReferenceEquals(container.DataContext, item)) Focus();
        container.DataContext = item;
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container.IsKeyboardFocusWithin) Focus();
        container.DataContext = null;
    }
}
