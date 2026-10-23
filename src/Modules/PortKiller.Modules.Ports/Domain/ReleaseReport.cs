using System.Collections.Generic;

namespace PortKiller.Models;

/// <summary>连接释放的真实结果；调用成功后还必须确认该连接已从网络表消失。</summary>
public sealed class ReleaseReport
{
    /// <summary>经过调用并复检确认消失的连接数。</summary>
    public int Released { get; set; }
    /// <summary>操作前已自然消失，不计作本工具释放成功。</summary>
    public int AlreadyGone { get; set; }
    /// <summary>不适用 IPv4 TCP TIME_WAIT 操作的记录数。</summary>
    public int Unsupported { get; set; }
    /// <summary>逐目标失败原因，包含原始错误码或复检失败证据。</summary>
    public List<string> Failures { get; } = new();
    /// <summary>顶部提示使用的简短统计。</summary>
    public string Summary => $"已释放 {Released} 条 · 已消失 {AlreadyGone} 条 · 不支持 {Unsupported} 条 · 失败 {Failures.Count} 条";
    /// <summary>完整报告只通过复制入口输出，避免提示区域被长错误占满。</summary>
    public string Detail => Summary + (Failures.Count == 0 ? "\n已复检连接状态；端口能否重新绑定还取决于其他监听和系统规则。" : "\n" + string.Join("\n", Failures));
}
