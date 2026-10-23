using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PortKiller.Models;
using PortKiller.Native;

namespace PortKiller.Services;

public class NetworkScannerService : PortKiller.Modules.Ports.Abstractions.IPortScanner
{
    public ScanResult ScanAllPorts(ulong previousFingerprint = 0)
    {
        var sw = Stopwatch.StartNew();

        Dictionary<int, ProcessSnapshotItem> processMap = null!;
        List<PortConnection> tcp4Items = null!;
        List<PortConnection> tcp6Items = null!;
        List<PortConnection> udp4Items = null!;
        List<PortConnection> udp6Items = null!;

        // 5 路操作系统内核调用极致并行采集
        Parallel.Invoke(
            () => processMap = GetProcessSnapshotMap(),
            () => tcp4Items = FetchTcp4(),
            () => tcp6Items = FetchTcp6(),
            () => udp4Items = FetchUdp4(),
            () => udp6Items = FetchUdp6()
        );

        return AssembleSnapshot(new[] { tcp4Items, tcp6Items, udp4Items, udp6Items },
            processMap, previousFingerprint, sw);
    }

    /// <summary>合并四类表并计算全量指纹；保留采集阶段的完整耗时。</summary>
    private static ScanResult AssembleSnapshot(List<PortConnection>[] sources,
        Dictionary<int, ProcessSnapshotItem> processMap, ulong previousFingerprint, Stopwatch clock)
    {
        int capacity = 0;
        foreach (var source in sources) capacity += source.Count;
        var allItems = new List<PortConnection>(capacity);
        int listeningCount = 0;
        ulong fingerprint = 14695981039346656037UL;
        foreach (var source in sources)
        {
            foreach (var item in source)
            {
                AttachProcess(item, processMap);
                if (item.IsListening) listeningCount++;
                fingerprint += CalculateRowHash(item);
                allItems.Add(item);
            }
        }
        clock.Stop();
        return new ScanResult
        {
            Items = allItems, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            ListeningCount = listeningCount, TcpCount = sources[0].Count + sources[1].Count,
            UdpCount = sources[2].Count + sources[3].Count, Fingerprint = fingerprint,
            HasChanged = previousFingerprint == 0 || previousFingerprint != fingerprint
        };
    }

    /// <summary>绑定进程信息；PID 4 只证明内核持有，不能直接证明 HTTP.sys。</summary>
    private static void AttachProcess(PortConnection item, Dictionary<int, ProcessSnapshotItem> processes)
    {
        if (processes.TryGetValue(item.Pid, out var process))
        {
            item.ProcessName = process.ProcessName;
            item.ParentPid = process.ParentPid;
        }
        else item.ProcessName = item.Pid == 0 ? "System Idle Process" : item.Pid == 4 ? "System" : "Unknown";
        item.IsSystemProtected = ProcessManagerService.IsAbsolutelyProtected(item.Pid, item.ProcessName);
    }

    /// <summary>网络字段与进程名称共同决定刷新指纹，避免 PID 复用后显示旧名称。</summary>
    private static ulong CalculateRowHash(PortConnection item)
    {
        const ulong prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        hash = (hash ^ (item.Protocol == "TCP" ? 6u : 17u)) * prime;
        hash = (hash ^ (item.IpVersion == "IPv4" ? 4u : 6u)) * prime;
        hash = (hash ^ (uint)item.LocalPort) * prime;
        hash = (hash ^ (uint)item.RemotePort) * prime;
        hash = (hash ^ (uint)item.Pid) * prime;
        hash = (hash ^ (uint)item.RawState) * prime;
        FoldChars(ref hash, item.LocalAddress, prime);
        FoldChars(ref hash, item.RemoteAddress, prime);
        FoldChars(ref hash, item.ProcessName, prime);
        return hash;
    }

    public virtual Dictionary<int, ProcessSnapshotItem> GetProcessSnapshotMap()
    {
        var map = new Dictionary<int, ProcessSnapshotItem>(512);
        IntPtr hSnapshot = NativeMethods.CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
        if (hSnapshot == IntPtr.Zero || hSnapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取进程快照失败。");
        }

        try { ReadProcesses(hSnapshot, map); }
        finally { NativeMethods.CloseHandle(hSnapshot); }

        if (!map.ContainsKey(0))
        {
            map[0] = new ProcessSnapshotItem { Pid = 0, ParentPid = 0, ProcessName = "System Idle Process" };
        }
        if (!map.ContainsKey(4))
        {
            map[4] = new ProcessSnapshotItem { Pid = 4, ParentPid = 0, ProcessName = "System" };
        }

        return map;
    }

    /// <summary>读取进程快照并检查枚举结束码，失败不伪装成空进程表。</summary>
    private static void ReadProcesses(IntPtr snapshot, Dictionary<int, ProcessSnapshotItem> processes)
    {
        const int noMoreFiles = 18;
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        bool found = NativeMethods.Process32First(snapshot, ref entry);
        while (found)
        {
            int pid = (int)entry.th32ProcessID;
            processes[pid] = new ProcessSnapshotItem
            {
                Pid = pid, ParentPid = (int)entry.th32ParentProcessID, ProcessName = entry.szExeFile
            };
            found = NativeMethods.Process32Next(snapshot, ref entry);
        }
        int error = Marshal.GetLastWin32Error();
        if (error != noMoreFiles) throw new Win32Exception(error, $"枚举进程失败 [Win32 {error}]。");
    }

    /// <summary>读取 Tcp4 表并转换为界面行数据。</summary>
    private List<PortConnection> FetchTcp4()
        => ReadTable<MIB_TCPROW_OWNER_PID>(
            (IntPtr buffer, ref int size) => NativeMethods.GetExtendedTcpTable(buffer, ref size,
                false, NativeMethods.AF_INET, TcpTableClass.OwnerPidAll), "Tcp4", row =>
        {
            var state = (MibTcpState)row.dwState;
            return new PortConnection
            {
                Protocol = "TCP",
                IpVersion = "IPv4",
                LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                LocalPort = NativeMethods.ConvertPort(row.dwLocalPort),
                RemoteAddress = new IPAddress(row.dwRemoteAddr).ToString(),
                RemotePort = NativeMethods.ConvertPort(row.dwRemotePort),
                State = FormatTcpState(state),
                RawState = state,
                RawLocalAddr = row.dwLocalAddr,
                RawLocalPort = row.dwLocalPort,
                RawRemoteAddr = row.dwRemoteAddr,
                RawRemotePort = row.dwRemotePort,
                Pid = (int)row.dwOwningPid
            };
        });

    /// <summary>读取 Tcp6 表并转换为界面行数据。</summary>
    private List<PortConnection> FetchTcp6()
        => ReadTable<MIB_TCP6ROW_OWNER_PID>(
            (IntPtr buffer, ref int size) => NativeMethods.GetExtendedTcpTable(buffer, ref size,
                false, NativeMethods.AF_INET6, TcpTableClass.OwnerPidAll), "Tcp6", row =>
        {
            var state = (MibTcpState)row.dwState;
            return new PortConnection
            {
                Protocol = "TCP",
                IpVersion = "IPv6",
                LocalAddress = FormatIpv6Address(row.ucLocalAddr, row.dwLocalScopeId),
                LocalPort = NativeMethods.ConvertPort(row.dwLocalPort),
                RemoteAddress = FormatIpv6Address(row.ucRemoteAddr, row.dwRemoteScopeId),
                RemotePort = NativeMethods.ConvertPort(row.dwRemotePort),
                State = FormatTcpState(state),
                RawState = state,
                RawLocalPort = row.dwLocalPort,
                RawRemotePort = row.dwRemotePort,
                Pid = (int)row.dwOwningPid
            };
        });

    /// <summary>读取 Udp4 表并转换为界面行数据。</summary>
    private List<PortConnection> FetchUdp4()
        => ReadTable<MIB_UDPROW_OWNER_PID>(
            (IntPtr buffer, ref int size) => NativeMethods.GetExtendedUdpTable(buffer, ref size,
                false, NativeMethods.AF_INET, UdpTableClass.OwnerPid), "Udp4", row =>
        {
            return new PortConnection
            {
                Protocol = "UDP",
                IpVersion = "IPv4",
                LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                LocalPort = NativeMethods.ConvertPort(row.dwLocalPort),
                RemoteAddress = "*",
                RemotePort = 0,
                State = "UDP (无连接)",
                Pid = (int)row.dwOwningPid
            };
        });

    /// <summary>读取 Udp6 表并转换为界面行数据。</summary>
    private List<PortConnection> FetchUdp6()
        => ReadTable<MIB_UDP6ROW_OWNER_PID>(
            (IntPtr buffer, ref int size) => NativeMethods.GetExtendedUdpTable(buffer, ref size,
                false, NativeMethods.AF_INET6, UdpTableClass.OwnerPid), "Udp6", row =>
        {
            return new PortConnection
            {
                Protocol = "UDP",
                IpVersion = "IPv6",
                LocalAddress = FormatIpv6Address(row.ucLocalAddr, row.dwLocalScopeId),
                LocalPort = NativeMethods.ConvertPort(row.dwLocalPort),
                RemoteAddress = "*",
                RemotePort = 0,
                State = "UDP (无连接)",
                Pid = (int)row.dwOwningPid
            };
        });

    /// <summary>保留网卡作用域，避免不同接口的 IPv6 链路本地地址被视为同一端点。</summary>
    internal static string FormatIpv6Address(byte[] address, uint scopeId)
        => new IPAddress(address, scopeId).ToString();

    /// <summary>四类网络表共用的原生读取签名。</summary>
    private delegate uint TableReader(IntPtr buffer, ref int size);
    /// <summary>网络表增长时最多重读三次，避免一次缓冲区不足被误报成零连接。</summary>
    private const int TableReadAttempts = 3;

    /// <summary>原生错误必须向上传递，不能返回空表掩盖读取失败。</summary>
    private static List<PortConnection> ReadTable<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(TableReader read, string source,
        Func<T, PortConnection> convert) where T : struct
    {
        int size = 0;
        uint result = read(IntPtr.Zero, ref size);
        for (int attempt = 0; attempt < TableReadAttempts; attempt++)
        {
            if (result != NativeMethods.ERROR_INSUFFICIENT_BUFFER && result != NativeMethods.ERROR_SUCCESS)
                throw new Win32Exception((int)result, $"读取 {source} 网络表失败 [Win32 {result}]。");
            if (size < sizeof(int)) throw new InvalidOperationException($"{source} 网络表长度无效。");
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int capacity = size;
                result = read(buffer, ref size);
                if (result == NativeMethods.ERROR_SUCCESS) return ConvertRows(buffer, capacity, convert);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new Win32Exception((int)result, $"读取 {source} 网络表失败 [Win32 {result}]，请重新扫描。");
    }

    /// <summary>按操作系统给出的表头转换行，同时检查边界以避免读取缓冲区外的数据。</summary>
    private static List<PortConnection> ConvertRows<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(IntPtr buffer, int capacity,
        Func<T, PortConnection> convert) where T : struct
    {
        int count = Marshal.ReadInt32(buffer);
        int rowSize = Marshal.SizeOf<T>();
        if (count < 0 || count > (capacity - sizeof(int)) / rowSize)
            throw new InvalidOperationException("系统网络表行数与缓冲区长度不一致。");
        var rows = new List<PortConnection>(count);
        for (int index = 0; index < count; index++)
            rows.Add(convert(Marshal.PtrToStructure<T>(IntPtr.Add(buffer, sizeof(int) + index * rowSize))));
        return rows;
    }

    /// <summary>把字符串按字符折入 FNV-1a 指纹（零分配，避免对地址串做哈希对象）。</summary>
    private static void FoldChars(ref ulong fingerprint, string s, ulong fnvPrime)
    {
        for (int c = 0; c < s.Length; c++)
        {
            fingerprint ^= s[c];
            fingerprint *= fnvPrime;
        }
    }

    private static string FormatTcpState(MibTcpState state)
    {
        return state switch
        {
            MibTcpState.Listen => "LISTENING",
            MibTcpState.Established => "ESTABLISHED",
            MibTcpState.TimeWait => "TIME_WAIT",
            MibTcpState.CloseWait => "CLOSE_WAIT",
            MibTcpState.FinWait1 => "FIN_WAIT_1",
            MibTcpState.FinWait2 => "FIN_WAIT_2",
            MibTcpState.SynSent => "SYN_SENT",
            MibTcpState.SynReceived => "SYN_RCVD",
            MibTcpState.Closing => "CLOSING",
            MibTcpState.LastAck => "LAST_ACK",
            MibTcpState.Closed => "CLOSED",
            _ => state.ToString().ToUpperInvariant()
        };
    }
}
