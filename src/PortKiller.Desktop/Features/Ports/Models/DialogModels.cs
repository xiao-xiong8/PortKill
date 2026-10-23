using PortKiller.Models;

namespace PortKiller.Models;

/// <summary>系统保护拦截弹窗「本次被拦截的目标」chips。</summary>
public sealed class InterceptChip
{
    public InterceptChip(string text, bool isPrimary)
    {
        Text = text;
        IsPrimary = isPrimary;
    }

    public string Text { get; }

    /// <summary>true = 玫瑰红高亮（本次指令的直接目标）；false = 中性 chip。</summary>
    public bool IsPrimary { get; }
}

/// <summary>桌面悬浮窗 HUD 数据行（端口 + 进程名，端口按服务语义色着色）。</summary>
public sealed class HudRow
{
    public HudRow(string port, string process, string category)
    {
        Port = port;
        Process = process;
        Category = category;
        IsWeb = category == "Web / API";
        IsDb = category == "数据库";
        IsDev = category == "开发工具";
        IsNet = category == "代理工具";
        IsKernel = category == "内核服务";
        IsApp = !(IsWeb || IsDb || IsDev || IsNet || IsKernel);
    }

    public string Port { get; }
    public string Process { get; }
    public string Category { get; }
    public bool IsWeb { get; }
    public bool IsDb { get; }
    public bool IsDev { get; }
    public bool IsNet { get; }
    public bool IsKernel { get; }
    public bool IsApp { get; }
}
