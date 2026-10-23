using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PortKiller.Models;
using PortKiller.Native;

namespace PortKiller.Services;

public class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int AffectedCount { get; set; }

    /// <summary>逐目标失败明细（Win32 错误码 + 处置建议），供 UI 展示与排查。</summary>
    public List<string> Failures { get; } = new();
}

public class ProcessManagerService : PortKiller.Modules.Ports.Abstractions.ITcpConnectionResetter
{
    /// <summary>
    /// 守护包装器：把 nginx 等普通程序托管成 Windows 服务并在其退出后自动重生的宿主。
    /// 家族杀时必须先终止包装器，否则杀掉的目标会被立即重新拉起。
    /// </summary>
    private static readonly string[] DaemonWrapperHints = { "nssm", "winsw", "srvany", "firedaemon" };

    private static bool IsDaemonWrapper(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var hint in DaemonWrapperHints)
        {
            if (processName.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 保护名单唯一真相源：Native/SystemProtection.cs（扫描器 / UI / 本类统一引用）
    private static bool IsProtectedTarget(int pid, string processName)
        => SystemProtection.IsAbsolutelyProtected(pid, processName);

    /// <summary>
    /// 内核绝对保护级：PID ≤ 4 或蓝屏级关键进程（csrss/lsass/services 等）。
    /// 任何用户权限都无法安全终止 —— UI 不应对这一级提供「仍要强制终止」选项，
    /// 强行 TerminateProcess 会直接触发 BSOD 或会话崩溃。
    /// </summary>
    public static bool IsAbsolutelyProtected(int pid, string processName)
        => SystemProtection.IsAbsolutelyProtected(pid, processName);

    // ══════════════════════════════════════════════════════════════════
    //  统一击杀引擎 · 两阶段：先连发终止指令（零等待），后统一等待退出
    //
    //  为什么必须两阶段 —— taskkill /F /IM 能杀掉 nginx 而旧实现不能的根因：
    //  旧实现「杀一个 → 等退出(≤1.5s) → 再杀下一个」，nginx master 在 worker
    //  死亡后毫秒级就会 spawn 替补（新 PID 不在快照目标集、继承监听套接字），
    //  1.5 秒的等待窗口足够它重生好几轮。两阶段把指令连发压缩到微秒级，
    //  master 来不及反应就已自身难保 —— 与 taskkill 的节奏一致。
    // ══════════════════════════════════════════════════════════════════

    public OperationResult KillProcess(int pid, string processName, CancellationToken cancellationToken = default)
    {
        if (IsProtectedTarget(pid, processName))
        {
            return new OperationResult
            {
                Success = false,
                Message = $"PID {pid} ({processName}) 属于系统关键保护进程，直接终止将导致系统崩溃或蓝屏，已拦截！"
            };
        }

        return KillTargetsCore(new List<(int, string)> { (pid, processName) }, "KILL", cancellationToken: cancellationToken);
    }

    /// <summary>对已收集的目标集执行强杀（确认弹窗后走这里）。目标须已按「长辈先行」排序。</summary>
    public OperationResult KillFamilyTargets(IReadOnlyList<(int Pid, string Name)> targets, CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0)
        {
            return new OperationResult { Success = false, Message = "目标集为空：进程属系统保护范围或已退出。" };
        }

        return KillTargetsCore(targets.ToList(), "KILL_FAMILY", cancellationToken: cancellationToken);
    }

    /// <summary>一步式家族杀：自行取快照 → 收集目标 → 全部终止。</summary>
    public OperationResult KillFamily(int rootPid, string rootName, PortKiller.Modules.Ports.Abstractions.IPortScanner scanner)
    {
        if (IsProtectedTarget(rootPid, rootName))
        {
            return new OperationResult
            {
                Success = false,
                Message = $"PID {rootPid} ({rootName}) 属于系统保护进程，禁止家族强杀！"
            };
        }

        var snapshot = scanner.GetProcessSnapshotMap();
        var targets = CollectFamilyTargets(rootPid, rootName, snapshot);
        return KillFamilyTargets(targets);
    }

    public OperationResult KillProcessTree(int rootPid, string rootProcessName, PortKiller.Modules.Ports.Abstractions.IPortScanner scanner, CancellationToken cancellationToken = default)
    {
        if (IsProtectedTarget(rootPid, rootProcessName))
        {
            return new OperationResult
            {
                Success = false,
                Message = $"PID {rootPid} ({rootProcessName}) 属于系统保护进程，禁止进程树强杀！"
            };
        }

        var snapshotMap = scanner.GetProcessSnapshotMap();
        var parentToChildren = new Dictionary<int, List<int>>();

        foreach (var kvp in snapshotMap)
        {
            int parentPid = kvp.Value.ParentPid;
            if (!parentToChildren.TryGetValue(parentPid, out var list))
            {
                list = new List<int>();
                parentToChildren[parentPid] = list;
            }
            list.Add(kvp.Key);
        }

        // 父先子后：守护进程必须比子进程先死 —— 若先杀子代，nginx master
        // 这类守护方会在等待间隙立即重生新子代（旧实现「先子后父」是反的）
        var ordered = new List<(int Pid, string Name)> { (rootPid, rootProcessName) };
        var descendants = new List<int>();
        CollectDescendants(rootPid, parentToChildren, descendants); // 广度优先 = 父先子后

        foreach (int pid in descendants)
        {
            string pName = snapshotMap.TryGetValue(pid, out var p) ? p.ProcessName : "Unknown";
            if (IsProtectedTarget(pid, pName)) continue;
            ordered.Add((pid, pName));
        }

        return KillTargetsCore(ordered, "KILL_TREE", $"root={rootProcessName}:{rootPid} ", cancellationToken);
    }

    /// <summary>
    /// 两阶段击杀核心。ordered 必须已按「长辈先行」拓扑排序。
    /// 失败明细带 Win32 错误码、权限环境与处置建议 —— 终止失败必须可排查。
    /// </summary>
    private OperationResult KillTargetsCore(List<(int Pid, string Name)> ordered, string action, string logPrefix = "", CancellationToken cancellationToken = default)
    {
        var condemned = new List<(int Pid, string Name, IntPtr Handle)>();
        var failures = new List<string>();

        SendTerminationRequests(ordered, condemned, failures, cancellationToken);
        int confirmed = ConfirmTerminations(condemned, failures);

        int killed = confirmed; // 只有观察到退出才计为成功

        string envNote = failures.Count > 0
            ? $"（环境：{(SecurityHelper.IsRunningAsAdministrator() ? "管理员" : "普通用户")} · SeDebugPrivilege {(PrivilegeHelper.IsSeDebugEnabled ? "已启用" : "未启用")}）"
            : string.Empty;

        var result = new OperationResult
        {
            Success = killed > 0,
            AffectedCount = killed,
            Message = failures.Count == 0
                ? $"已终止 {killed} 个进程，{confirmed} 个确认退出。"
                : $"终止 {killed}/{ordered.Count} 个进程（{confirmed} 个确认退出）；失败明细：{string.Join("；", failures)}{envNote}"
        };
        foreach (var f in failures) result.Failures.Add(f);

        OperationLog.Record(action,
            $"{logPrefix}targets={ordered.Count} terminated={killed} confirmedExit={confirmed}"
            + (failures.Count > 0 ? $" failures=[{string.Join(" | ", failures)}]" : string.Empty));

        return result;
    }

    /// <summary>先下发全部终止请求；执行边界再次拦截保护进程与本程序。</summary>
    private static void SendTerminationRequests(List<(int Pid, string Name)> targets,
        List<(int Pid, string Name, IntPtr Handle)> accepted, List<string> failures, CancellationToken cancellationToken)
    {
        var image = new StringBuilder(32768);
        foreach (var (pid, name) in targets.DistinctBy(x => x.Pid))
        {
            if (cancellationToken.IsCancellationRequested) { failures.Add("操作已取消，剩余目标未执行。"); break; }
            if (IsProtectedTarget(pid, name) || pid == Environment.ProcessId)
            {
                failures.Add($"PID {pid} ({name})：目标受保护，已跳过。");
                continue;
            }
            IntPtr handle = OpenForTerminate(pid);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                failures.Add($"PID {pid}：打开进程失败 [Win32 {error}] {DescribeFailure(error)}");
                continue;
            }
            // 用即将终止的同一个句柄核对实际映像，不能只信旧列表中的 PID/名称。
            image.Clear();
            int length = image.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, image, ref length))
            {
                int error = Marshal.GetLastWin32Error();
                NativeMethods.CloseHandle(handle);
                failures.Add($"PID {pid}：无法核验当前进程映像，已跳过 [Win32 {error}]。");
                continue;
            }
            string currentName = Path.GetFileName(image.ToString());
            if (!string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase) || IsProtectedTarget(pid, currentName))
            {
                NativeMethods.CloseHandle(handle);
                failures.Add($"PID {pid}：当前进程为 {currentName}，与目标 {name} 不符或属于保护范围；请刷新后重新确认。");
                continue;
            }
            if (NativeMethods.TerminateProcess(handle, 1)) accepted.Add((pid, name, handle));
            else
            {
                int error = Marshal.GetLastWin32Error();
                NativeMethods.CloseHandle(handle);
                failures.Add($"PID {pid}：终止失败 [Win32 {error}] {DescribeFailure(error)}");
            }
        }
    }

    /// <summary>全部目标共用退出等待预算，不让 N 个超时累计成 N 倍等待。</summary>
    private static int ConfirmTerminations(List<(int Pid, string Name, IntPtr Handle)> accepted,
        List<string> failures)
    {
        const int waitBudgetMs = 1500;
        var clock = Stopwatch.StartNew();
        int confirmed = 0;
        foreach (var (pid, name, handle) in accepted)
        {
            try
            {
                uint wait = NativeMethods.WaitForSingleObject(handle, (uint)Math.Max(0, waitBudgetMs - clock.ElapsedMilliseconds));
                if (wait == 0) confirmed++;
                else failures.Add($"PID {pid} ({name})：退出未确认，等待结果 0x{wait:X8}。");
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        return confirmed;
    }

    /// <summary>
    /// 打开进程句柄用于终止；ACCESS_DENIED (5) / 特权未持有 (1314) 时
    /// 自动启用 SeDebugPrivilege 并重试一次 —— 服务托管 / SYSTEM / 高完整性
    /// 进程（nginx 服务的典型形态）依赖这一步才能被管理员终止。
    /// </summary>
    private static IntPtr OpenForTerminate(int pid)
    {
        // SYNCHRONIZE 必须带上：否则阶段 2 的 WaitForSingleObject 立即 WAIT_FAILED，
        // 「确认退出」形同虚设（该 bug 曾被 --verify 的 wait=0xFFFFFFFF 抓出）
        const uint access = NativeMethods.PROCESS_TERMINATE
                          | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION
                          | NativeMethods.SYNCHRONIZE;

        IntPtr h = NativeMethods.OpenProcess(access, false, pid);
        if (h != IntPtr.Zero) return h;

        int err = Marshal.GetLastWin32Error();
        if ((err == 5 || err == 1314) && PrivilegeHelper.EnableSeDebugPrivilege())
        {
            h = NativeMethods.OpenProcess(access, false, pid);
        }
        return h;
    }

    /// <summary>把 Win32 错误码翻译成可执行的排查指引（终止失败必须能定位原因）。</summary>
    private static string DescribeFailure(int err)
    {
        bool admin = SecurityHelper.IsRunningAsAdministrator();
        return err switch
        {
            5 when !admin =>
                "访问被拒绝：目标以更高权限运行（服务 / SYSTEM / 高完整性），请以管理员身份重启 PortKiller 后重试",
            5 when !PrivilegeHelper.IsSeDebugEnabled =>
                "访问被拒绝：SeDebugPrivilege 自动启用失败，目标可能受安全软件或私有 ACL 保护",
            5 =>
                "访问被拒绝：已持有 SeDebugPrivilege 仍失败，目标可能是 PPL 受保护进程（反作弊 / 杀软核心），Windows 禁止任何用户终止",
            87 =>
                "PID 已不存在：进程已退出或列表数据过期。若端口仍被占用，说明套接字句柄被其他进程继承，或守护 / 服务已拉起新实例 —— 刷新后对新 PID 重试",
            1314 =>
                "缺少 SeDebugPrivilege 且自动启用失败（通常因为非管理员运行）",
            _ => new Win32Exception(err).Message
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  智能家族目标收集：守护包装器 ∪ 同名祖先进程 ∪ 子孙树，
    //  输出按「长辈先行」拓扑排序（父进程必须先于子进程终止，
    //  否则 nginx master 会在 worker 死亡的毫秒级间隙内重生替补）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>只沿真实父子关系收集家族，不把全机器同名程序当作同一应用。</summary>
    public List<(int Pid, string Name)> CollectFamilyTargets(
        int rootPid, string rootName, Dictionary<int, ProcessSnapshotItem> snapshot)
    {
        if (IsProtectedTarget(rootPid, rootName) || !snapshot.TryGetValue(rootPid, out var root)
            || !string.Equals(root.ProcessName, rootName, StringComparison.OrdinalIgnoreCase)) return new();
        var visited = new HashSet<int> { rootPid };
        // 从 worker 向上找到同名 master；不扩大到无关的同名独立实例。
        while (snapshot.TryGetValue(root.ParentPid, out var parent) && visited.Add(parent.Pid)
            && !IsProtectedTarget(parent.Pid, parent.ProcessName)
            && string.Equals(parent.ProcessName, rootName, StringComparison.OrdinalIgnoreCase)) root = parent;
        if (snapshot.TryGetValue(root.ParentPid, out var wrapper) && IsDaemonWrapper(wrapper.ProcessName)) root = wrapper;
        var children = snapshot.Values.GroupBy(x => x.ParentPid)
            .ToDictionary(x => x.Key, x => x.Select(p => p.Pid).ToList());
        var descendants = new List<int>();
        CollectDescendants(root.Pid, children, descendants);
        descendants.Insert(0, root.Pid);
        return descendants.Distinct().Where(snapshot.ContainsKey).Select(pid => snapshot[pid])
            .Where(x => x.Pid != Environment.ProcessId && !IsProtectedTarget(x.Pid, x.ProcessName))
            .Select(x => (x.Pid, x.ProcessName)).ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    //  TCP 连接重置 / 路径查询
    // ══════════════════════════════════════════════════════════════════

    /// <summary>仅重置 IPv4 TIME_WAIT；先核对权限，错误保留准确的系统返回码。</summary>
    public OperationResult ResetTcpConnection(PortConnection item)
    {
        if (!PortReleaseService.CanRelease(item))
            return new OperationResult { Message = "此操作仅支持 IPv4 TCP TIME_WAIT；监听进程请使用终止或诊断。" };
        if (!SecurityHelper.IsRunningAsAdministrator())
            return new OperationResult { Message = DescribeResetFailure(317) };
        var row = new MIB_TCPROW
        {
            dwState = (uint)MibTcpState.DeleteTcb,
            dwLocalAddr = item.RawLocalAddr,
            dwLocalPort = item.RawLocalPort,
            dwRemoteAddr = item.RawRemoteAddr,
            dwRemotePort = item.RawRemotePort
        };
        uint result = NativeMethods.SetTcpEntry(ref row);
        OperationLog.Record("RELEASE_RST",
            $"{item.LocalEndpoint}->{item.RemoteEndpoint} pid={item.Pid} win32={result}");
        return new OperationResult
        {
            Success = result == NativeMethods.ERROR_SUCCESS,
            AffectedCount = result == NativeMethods.ERROR_SUCCESS ? 1 : 0,
            Message = result == NativeMethods.ERROR_SUCCESS
                ? "系统已接受连接删除请求，等待重新扫描确认。" : DescribeResetFailure(result)
        };
    }

    /// <summary>317 在 SetTcpEntry 中表示未提权，不能套用通用错误文本误导用户。
    /// https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-settcpentry
    /// </summary>
    public static string DescribeResetFailure(uint code) => code switch
    {
        317 => "权限不足：当前调用未获管理员授权，请以管理员身份重启后重试 [Win32 317]。",
        5 => "系统拒绝访问：请检查管理员权限及安全软件策略 [Win32 5]。",
        87 => "连接参数无效或记录已过期，请刷新后重试 [Win32 87]。",
        50 => "当前系统不支持此连接删除操作 [Win32 50]。",
        1168 => "连接记录已不存在，请刷新确认 [Win32 1168]。",
        _ => $"系统拒绝删除连接 [Win32 {code}]：{new Win32Exception((int)code).Message}"
    };

    public string? GetProcessFullPath(int pid)
    {
        if (pid <= 4) return null;

        IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) return null;

        // P/Invoke 不抛异常（失败以返回值 + LastWin32Error 表达），无需 catch；
        // finally 只负责句柄归还。真出 CLR 级异常应冒泡到上层 ReportFailure 而非静默吞掉。
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (NativeMethods.QueryFullProcessImageName(hProcess, 0, sb, ref size))
            {
                return sb.ToString();
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }

        return null;
    }

    public bool OpenProcessLocation(int pid)
    {
        string? path = GetProcessFullPath(pid);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                // UseShellExecute 交给 Shell 启动：窗口激活/置前行为与手动双击一致，
                // 避免 CreateProcess 直启时定位窗口偶尔落到后台、用户误以为「点了没反应」
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>广度优先遍历真实子树（输出即「父先子后」顺序）并去重；进程快照存在环或 PID 复用时也能结束。</summary>
    private void CollectDescendants(int currentPid, Dictionary<int, List<int>> parentToChildren, List<int> result)
    {
        var seen = new HashSet<int> { currentPid };
        var queue = new Queue<int>();
        queue.Enqueue(currentPid);
        while (queue.TryDequeue(out int parent))
        {
            if (!parentToChildren.TryGetValue(parent, out var children)) continue;
            foreach (int child in children)
            {
                if (!seen.Add(child)) continue;
                result.Add(child);
                queue.Enqueue(child);
            }
        }
    }
}
