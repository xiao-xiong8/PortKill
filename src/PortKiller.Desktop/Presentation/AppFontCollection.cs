using System;
using Avalonia.Media.Fonts;

namespace PortKiller.Services;

/// <summary>显式加载随应用分发的五个静态字重，不依赖系统字体是否安装。</summary>
public sealed class AppFontCollection : EmbeddedFontCollection
{
    public const string LatinFamily = "fonts:PortKiller#Inter";
    public const string ChineseFamily = "fonts:PortKiller#Noto Sans SC";

    public AppFontCollection() : base(new Uri("fonts:PortKiller", UriKind.Absolute),
        new Uri("avares://PortKiller/Assets/Fonts", UriKind.Absolute)) { }
}
