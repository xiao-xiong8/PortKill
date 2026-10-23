using System;
using System.Collections.Generic;

namespace PortKiller.Native;

/// <summary>
/// 系统进程保护名单的唯一真相源 —— 扫描器、终止引擎与 UI 统一从这里取数，
/// 新增或移除受保护进程只改本文件（历史上名单散落多处、需手工同步，曾导致
/// 拦截弹窗与执行边界判定不一致）。分两级：
/// · 绝对保护级（蓝屏级）：Windows 在内核层禁止任何用户终止，强杀 = BSOD，
///   拦截弹窗不提供「仍要终止」按钮，执行边界二次拦截；
/// · 警告级（特权服务）：终止可能导致依赖功能异常，勾选风险确认后允许真实执行。
/// </summary>
public static class SystemProtection
{
    /// <summary>绝对保护级进程精确名单（OrdinalIgnoreCase 精确匹配，含扩展名）。</summary>
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "smss.exe", "csrss.exe",
        "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "MemCompression"
    };

    /// <summary>
    /// 警告级特权服务的进程名特征（OrdinalIgnoreCase 子串匹配）。
    /// 覆盖全部绝对保护级词根（lsass/services/wininit/winlogon/csrss/smss/registry），
    /// 另含 svchost / spoolsv / dwm 等可确认后强杀的特权服务。
    /// </summary>
    private static readonly string[] PrivilegedServiceHints =
    {
        "svchost", "lsass", "services", "spoolsv", "wininit", "winlogon", "csrss", "smss", "dwm", "registry"
    };

    /// <summary>
    /// 内核绝对保护级：PID ≤ 4 或蓝屏级关键进程（csrss/lsass/services 等）。
    /// 任何用户权限都无法安全终止 —— UI 不应对这一级提供「仍要强制终止」选项，
    /// 强行 TerminateProcess 会直接触发 BSOD 或会话崩溃。
    /// </summary>
    public static bool IsAbsolutelyProtected(int pid, string? processName)
        => pid <= 4 || (processName is not null && CriticalProcessNames.Contains(processName));

    /// <summary>是否为警告级 Windows 核心特权服务进程（svchost / dwm 等，子串启发式）。</summary>
    public static bool IsPrivilegedService(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var hint in PrivilegedServiceHints)
        {
            if (processName.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
