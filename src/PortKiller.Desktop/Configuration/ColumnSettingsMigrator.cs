using System;
using System.Globalization;
using System.Linq;

namespace PortKiller.Services;

/// <summary>逻辑 ID 0～6 保持不变；应用名称为 7。按字段修复，重复调用无副作用。</summary>
internal static class ColumnSettingsMigrator
{
    internal const int Count = 8;
    internal const double MaximumWidth = 8192;
    internal static readonly double[] DefaultWidths = { 96, 132, 132, 0, 78, 180, 132, 144 };
    internal static readonly int[] DefaultOrder = { 0, 1, 2, 7, 3, 4, 5, 6 };
    internal const string Widths = "96,132,132,0,78,180,132,144";
    internal const string Order = "0,1,2,7,3,4,5,6";
    internal const string Visible = "True,True,True,True,True,True,True,True";

    internal static void Migrate(AppSettings settings)
    {
        var sourceWidths = (settings.ColWidths ?? "").Split(',');
        var widths = (double[])DefaultWidths.Clone();
        for (int i = 0; i < Math.Min(sourceWidths.Length, Count); i++)
            if (double.TryParse(sourceWidths[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) && value >= 0 && value <= MaximumWidth && (i == 3 || value > 0)) widths[i] = value;
        settings.ColWidths = string.Join(',', widths.Select(x => x.ToString(CultureInfo.InvariantCulture)));

        var order = (settings.ColOrder ?? "").Split(',')
            .Select(x => int.TryParse(x, out int value) ? value : -1)
            .Where(x => x >= 0 && x < Count).Distinct().ToList();
        foreach (int id in DefaultOrder)
            if (!order.Contains(id))
            {
                if (id == 7 && order.Contains(3)) order.Insert(order.IndexOf(3), id);
                else order.Add(id);
            }
        settings.ColOrder = string.Join(',', order);
        var sourceVisible = (settings.ColVisible ?? "").Split(',');
        var visible = Enumerable.Repeat(true, Count).ToArray();
        for (int i = 0; i < Math.Min(sourceVisible.Length, Count); i++)
            if (bool.TryParse(sourceVisible[i], out bool value)) visible[i] = value;
        settings.ColVisible = string.Join(',', visible);
    }
}
