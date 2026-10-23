using System;

namespace PortKiller.Features.Updates;

/// <summary>将已签名的纯文本说明映射为展示标签，兼容没有分类的旧清单。</summary>
public sealed record UpdateReleaseNoteItem(string Tag, string Text)
{
    private static readonly string[] Tags = ["新增", "优化", "修复"];
    public bool IsNew => Tag == "新增";
    public bool IsImprovement => Tag == "优化";

    internal static UpdateReleaseNoteItem Parse(string text)
    {
        string trimmed = text.Trim();
        foreach (string tag in Tags)
        {
            foreach (string prefix in new[] { tag + "：", tag + ":", tag + " ", "[" + tag + "]", "【" + tag + "】" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed[prefix.Length..].Trim() is { Length: > 0 } body)
                    return new(tag, body);
            }
        }
        return new("更新", text);
    }
}
