using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PortKiller.Presentation.Controls;

/// <summary>确认卡片的唯一外框；内容只在描边内侧绘制，避免矩形分区盖住四角。</summary>
public sealed class DialogSurfaceBorder : Border
{
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerRadiusProperty)
            InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (Child is not { } content)
            return result;

        // 模板使用等宽描边、统一半径且无 Padding。Border 的半径位于描边中线，
        // 因此内半径为 R - t/2，而不是 R - t。使用排版后的实际内缩量，
        // 与 Border 在当前 DPI 下舍入后的描边保持同心。
        var radiusX = Math.Max(0, CornerRadius.TopLeft - content.Bounds.X / 2);
        var radiusY = Math.Max(0, CornerRadius.TopLeft - content.Bounds.Y / 2);
        if (content.Clip is not RectangleGeometry clip)
            content.Clip = clip = new RectangleGeometry();
        clip.Rect = new Rect(content.Bounds.Size);
        clip.RadiusX = radiusX;
        clip.RadiusY = radiusY;
        return result;
    }
}
