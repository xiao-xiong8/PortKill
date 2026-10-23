using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Platform;

namespace PortKiller.Features.Settings;

/// <summary>只读取随应用分发的授权原文，查看时无需网络。</summary>
public static class FontLicenseCatalog
{
    public static IReadOnlyList<FontLicenseDocument> Load() => new[]
    {
        new FontLicenseDocument("Noto Sans SC", "中文界面", "Regular 400 · Medium 500 · SemiBold 600",
            "https://github.com/google/fonts/tree/main/ofl/notosanssc", Read("NotoSansSC")),
        new FontLicenseDocument("Inter", "英文与数字", "Medium 500 · SemiBold 600",
            "https://github.com/rsms/inter", Read("Inter"))
    };

    private static string Read(string name)
    {
        using var reader = new StreamReader(AssetLoader.Open(new Uri($"avares://PortKiller/Assets/Fonts/{name}-OFL.txt")));
        return reader.ReadToEnd();
    }
}
