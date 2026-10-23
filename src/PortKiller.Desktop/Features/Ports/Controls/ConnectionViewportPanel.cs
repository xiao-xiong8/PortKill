using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PortKiller.Views;

/// <summary>
/// 固定行高的连接列表虚拟化。槽位留在可视树中循环换绑，滚动不反复卸载样式/绑定树。
/// 只保留视口及半屏缓冲，不保存历史连接；窗口缩小时裁减多余槽位。
/// </summary>
public sealed class ConnectionViewportPanel : VirtualizingPanel
{
    internal const double RowHeight = 48;
    private const double BufferScreens = 0.5;
    private readonly Dictionary<Control, int> _indices = new();
    private readonly Dictionary<int, Control> _containers = new();
    private readonly List<Control> _free = new();
    private Rect _viewport;

    public ConnectionViewportPanel()
        => EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            InvalidateMeasure();
        };

    protected override Size MeasureOverride(Size availableSize)
    {
        int itemCount = Items.Count;
        double viewHeight = double.IsFinite(_viewport.Height) && _viewport.Height > 0
            ? _viewport.Height : 600;
        double top = double.IsFinite(_viewport.Y) ? Math.Max(0, _viewport.Y) : 0;
        double extent = itemCount * RowHeight;
        top = Math.Min(top, Math.Max(0, extent - viewHeight));
        int first = Math.Clamp((int)Math.Floor((top - viewHeight * BufferScreens) / RowHeight), 0, itemCount);
        int end = Math.Clamp((int)Math.Ceiling((top + viewHeight * (1 + BufferScreens)) / RowHeight), first, itemCount);
        int wanted = end - first;
        var generator = ItemContainerGenerator!;

        _free.Clear();
        foreach (var child in Children)
        {
            if (!_indices.TryGetValue(child, out int index) || index < first || index >= end)
            {
                if (_indices.Remove(child, out int previousIndex)) _containers.Remove(previousIndex);
                _free.Add(child);
            }
        }
        int freeIndex = 0;
        for (int index = first; index < end; index++)
        {
            var container = ContainerFromIndex(index);
            if (container is null)
            {
                bool created = false;
                if (freeIndex < _free.Count) container = _free[freeIndex++];
                else
                {
                    generator.NeedsContainer(Items[index], index, out var key);
                    container = generator.CreateContainer(Items[index], index, key);
                    generator.PrepareItemContainer(container, Items[index], index);
                    AddInternalChild(container);
                    created = true;
                }
                _indices[container] = index;
                _containers[index] = container;
                // 回收槽位保持挂载，只更新数据；新槽位已由 generator 准备。
                if (!created) generator.PrepareItemContainer(container, Items[index], index);
                generator.ItemContainerPrepared(container, Items[index], index);
            }
            else if (!ReferenceEquals(container.DataContext, Items[index]))
                generator.PrepareItemContainer(container, Items[index], index);
            container.IsVisible = true;
            container.Measure(new Size(availableSize.Width, RowHeight));
        }
        // 仅在视口缩小或数据缩短时卸载，持续滚动的槽位数量稳定。
        int capacity = Math.Max(wanted, Math.Min(itemCount, (int)Math.Ceiling(viewHeight * 2 / RowHeight) + 2));
        for (int i = freeIndex; i < _free.Count; i++)
        {
            generator.ClearItemContainer(_free[i]);
            _free[i].IsVisible = false;
        }
        for (int i = _free.Count - 1; i >= freeIndex && Children.Count > capacity; i--)
            RemoveInternalChild(_free[i]);
        // 首次布局预建两屏以内的槽位，避免开始滚动时才在输入线程上创建模板。
        while (Children.Count < capacity)
        {
            generator.NeedsContainer(null, -1, out var key);
            var spare = generator.CreateContainer(null, -1, key);
            generator.PrepareItemContainer(spare, null, -1);
            spare.IsVisible = false;
            AddInternalChild(spare);
        }
        _free.Clear();
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        return new Size(width, extent);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            if (_indices.TryGetValue(child, out int index))
                child.Arrange(new Rect(0, index * RowHeight, finalSize.Width, RowHeight));
        return finalSize;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // 索引槽位固定；布局前逐槽校验引用，新增/删除也不会保留陈旧绑定。
        foreach (var pair in _indices)
            if (pair.Value < items.Count)
            {
                if (!ReferenceEquals(pair.Key.DataContext, items[pair.Value]))
                    ItemContainerGenerator!.PrepareItemContainer(pair.Key, items[pair.Value], pair.Value);
                pair.Key.IsVisible = true;
            }
            else
            {
                ItemContainerGenerator!.ClearItemContainer(pair.Key);
                pair.Key.IsVisible = false;
            }
        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        _indices.Clear();
        _containers.Clear();
        _free.Clear();
    }

    protected override Control? ContainerFromIndex(int index)
        => index >= 0 && index < Items.Count ? _containers.GetValueOrDefault(index) : null;

    protected override int IndexFromContainer(Control container) => _indices.GetValueOrDefault(container, -1);
    protected override IEnumerable<Control>? GetRealizedContainers() => _indices.Keys;
    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        var owner = this.Parent;
        while (owner is not null && owner is not ScrollViewer) owner = owner.Parent;
        if (owner is ScrollViewer scroll)
        {
            double top = index * RowHeight;
            if (top < scroll.Offset.Y) scroll.Offset = new Vector(0, top);
            else if (top + RowHeight > scroll.Offset.Y + scroll.Viewport.Height)
                scroll.Offset = new Vector(0, top + RowHeight - scroll.Viewport.Height);
            scroll.UpdateLayout();
        }
        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var container = from as Control;
        while (container is not null && !_indices.ContainsKey(container)) container = container.Parent as Control;
        int index = container is null ? -1 : IndexFromContainer(container);
        int next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Up or NavigationDirection.Previous => index < 0 ? Items.Count - 1 : index - 1,
            NavigationDirection.Down or NavigationDirection.Next => index + 1,
            _ => index
        };
        if (wrap && Items.Count > 0) next = (next + Items.Count) % Items.Count;
        return ScrollIntoView(next);
    }
}
