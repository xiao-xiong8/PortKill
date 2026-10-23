using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using System.Security.Cryptography;
using PortKiller.Services;

namespace PortKiller.Regression;

internal static class FontChecks
{
    internal static void EmbeddedFacesAndFallback()
    {
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold })
            CheckFace(AppFontCollection.ChineseFamily, "Noto Sans SC", weight);
        foreach (var weight in new[] { FontWeight.Medium, FontWeight.SemiBold })
            CheckFace(AppFontCollection.LatinFamily, "Inter", weight);

        var mixed = new TextBlock
        {
            Text = "自动更新 PortKiller 62%", FontFamily = new FontFamily(AppFontCollection.LatinFamily),
            FontWeight = FontWeight.Medium, FontSize = 19
        };
        mixed.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var shaped = mixed.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().ToArray();
        var families = shaped.Select(run => run.GlyphRun.GlyphTypeface.FamilyName).ToHashSet();
        Program.Assert(families.SetEquals(new[] { "Noto Sans SC", "Inter" }),
            "中英混排没有使用内嵌原型字体：" + string.Join(", ", families));

        double Width(string text)
        {
            var block = new TextBlock { Text = text, FontFamily = new FontFamily(AppFontCollection.LatinFamily),
                FontWeight = FontWeight.SemiBold, FontSize = 22, FontFeatures = FontFeatureCollection.Parse("+tnum") };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
        Program.Assert(Math.Abs(Width("111111") - Width("888888")) < .01,
            "Inter 等宽数字未生效，进度数值会左右跳动。");
        TypographyService.Apply(12, 12, true, null, null);
        Program.Assert(Application.Current!.Resources[TypographyService.KeyFontUi] is FontFamily ui && ui.ToString() == AppFontCollection.LatinFamily
            && Application.Current.Resources[TypographyService.KeyFontMono] is FontFamily data && data.ToString() == AppFontCollection.LatinFamily,
            "默认/恢复默认又回到了系统字体。");
        Console.WriteLine("PASS 内嵌字体五个真实字重、中英回退及 Inter 等宽数字");
    }

    private static void CheckFace(string uri, string family, FontWeight weight)
    {
        Program.Assert(FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(uri), FontStyle.Normal, weight), out var face)
            && face.FamilyName == family && face.Weight == weight && face.FontSimulations == FontSimulations.None,
            $"{family}/{weight} 未加载实际字重，或使用了合成字形。");
        string style = weight == FontWeight.Normal ? "Regular" : weight == FontWeight.Medium ? "Medium" : "SemiBold";
        string name = family == "Inter" ? "Inter" : "NotoSansSC";
        using var embedded = AssetLoader.Open(new Uri($"avares://PortKiller/Assets/Fonts/{name}-{style}.ttf"));
        Program.Assert(face!.PlatformTypeface.TryGetStream(out var actual), "无法验证字体来源。");
        using (actual)
            Program.Assert(SHA256.HashData(embedded).SequenceEqual(SHA256.HashData(actual!)),
                $"{family}/{weight} 使用了系统字体，未使用内嵌字重。");
    }
}
