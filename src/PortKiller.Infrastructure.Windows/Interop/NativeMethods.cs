using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PortKiller.Native;

public static class NativeMethods
{
    public const uint AF_INET = 2;
    public const uint AF_INET6 = 23;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_SUCCESS = 0;

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    /// <summary>WaitForSingleObject 等待进程退出所必需的句柄权限，缺失时等待立即返回 WAIT_FAILED。</summary>
    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        TcpTableClass tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        UdpTableClass tableClass,
        uint reserved = 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint SetTcpEntry(ref MIB_TCPROW pTcpRow);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// 等待进程句柄信号化（进程真正退出）。TerminateProcess 是异步指令，
    /// 不等这一步就重扫会看到端口仍被占用，误判为终止失败。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);

    // ── 令牌特权（SeDebugPrivilege 启用链） ─────────────────────────────
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern unsafe bool AdjustTokenPresent(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        TOKEN_PRIVILEGES* newState,
        uint bufferLength,
        TOKEN_PRIVILEGES* previousState,
        uint* returnLength);

    // ── 进程时间（启动时间查询，替代 Process.GetProcessById 的全表枚举） ──
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(
        IntPtr hProcess,
        out FILETIME lpCreationTime,
        out FILETIME lpExitTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    public static ushort ConvertPort(uint dwPort)
    {
        return (ushort)(((dwPort & 0xFF) << 8) | ((dwPort >> 8) & 0xFF));
    }
}
