using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PortKiller.Models;
using PortKiller.Native;

namespace PortKiller.Services;

/// <summary>
/// 详情面板数据装配服务：进程可执行文件、启动时间、父进程、子进程树。
/// 全部走 Win32 / BCL 原生路径，零反射，Native AOT 兼容。
///
/// 性能约定：所有需要进程拓扑的方法都接收调用方传入的 Toolhelp32 快照，
/// 本服务内部绝不自行全表枚举 —— 详情面板一次装配只允许发生 1 次快照。
/// </summary>
public class ProcessDetailService
{
    /// <summary>
    /// 进程启动时间。直接 OpenProcess + GetProcessTimes，
    /// 替代 Process.GetProcessById（其内部会全表枚举进程，毫秒级卡顿源）。
    /// 对内核进程或无权限进程返回 null。
    /// </summary>
    public DateTime? GetStartTime(int pid)
    {
        if (pid <= 4) return null;

        IntPtr hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            if (NativeMethods.GetProcessTimes(hProcess, out FILETIME creation, out _, out _, out _))
            {
                long fileTime = ((long)creation.DateTimeHigh << 32) | creation.DateTimeLow;
                if (fileTime > 0) return DateTime.FromFileTimeUtc(fileTime);
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    /// <summary>父进程 PID 与名称（来自调用方传入的 Toolhelp32 快照）。</summary>
    public (int Pid, string Name) GetParent(int pid, Dictionary<int, ProcessSnapshotItem> snapshot)
    {
        if (snapshot.TryGetValue(pid, out var self))
        {
            int parentPid = self.ParentPid;
            if (snapshot.TryGetValue(parentPid, out var parent))
            {
                return (parentPid, parent.ProcessName);
            }
            return (parentPid, parentPid == 0 ? "System Idle Process" : "System");
        }
        return (0, "—");
    }

    /// <summary>
    /// 构建以指定进程为根的子进程树（前序展开，最多 3 层，避免巨型树刷屏）。
    /// </summary>
    public List<ProcessTreeNode> BuildProcessTree(
        int rootPid, string rootName, Dictionary<int, ProcessSnapshotItem> snapshot)
    {
        var result = new List<ProcessTreeNode>
        {
            new() { Name = rootName, Pid = rootPid, Depth = 0, IsRoot = true }
        };

        try
        {
            var parentToChildren = new Dictionary<int, List<(int Pid, string Name)>>();

            foreach (var kvp in snapshot)
            {
                int childPid = kvp.Key;
                int parentPid = kvp.Value.ParentPid;
                if (!parentToChildren.TryGetValue(parentPid, out var list))
                {
                    list = new List<(int, string)>();
                    parentToChildren[parentPid] = list;
                }
                list.Add((childPid, kvp.Value.ProcessName));
            }

            void Walk(int pid, int depth)
            {
                if (depth >= 3) return;
                if (!parentToChildren.TryGetValue(pid, out var children)) return;

                foreach (var (childPid, name) in children)
                {
                    if (childPid == pid) continue;
                    result.Add(new ProcessTreeNode { Name = name, Pid = childPid, Depth = depth + 1 });
                    Walk(childPid, depth + 1);
                    if (result.Count > 24) return;
                }
            }

            Walk(rootPid, 0);
        }
        catch
        {
            // 快照失败时只返回根节点
        }

        return result;
    }

    /// <summary>该进程当前持有的监听端口（去重，最多 8 个）。</summary>
    public List<PortChipItem> GetListeningPorts(int pid, IReadOnlyList<PortConnection> allItems)
    {
        return allItems
            .Where(x => x.Pid == pid && (x.IsListening || x.IsEstablished))
            .GroupBy(x => x.LocalPort)
            .OrderBy(g => g.Key)
            .Take(8)
            .Select(g =>
            {
                bool listening = g.Any(x => x.IsListening);
                return new PortChipItem
                {
                    Port = g.Key.ToString(),
                    StateText = listening ? "监听中" : "已连接",
                    IsListening = listening
                };
            })
            .ToList();
    }

    /// <summary>把启动时间格式化为设计稿的中文相对时间表达。</summary>
    public static string FormatStartTime(DateTime? start)
    {
        if (start is null) return "不可用（内核进程或权限不足）";

        var local = start.Value.ToLocalTime();
        var today = DateTime.Today;
        string dayText = local.Date == today
            ? "今天"
            : local.Date == today.AddDays(-1)
                ? "昨天"
                : local.ToString("yyyy-MM-dd");

        var span = DateTime.Now - local;
        string runText = span.TotalDays >= 1
            ? $"{(int)span.TotalDays} 天 {span.Hours} 小时"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes:D2} 分"
                : $"{(int)span.TotalMinutes} 分钟";

        return $"{dayText} {local:HH:mm} · 已运行 {runText}";
    }
}
