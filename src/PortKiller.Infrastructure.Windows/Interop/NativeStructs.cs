using System;
using System.Runtime.InteropServices;

namespace PortKiller.Native;


public enum TcpTableClass
{
    BasicListener,
    BasicConnections,
    BasicAll,
    OwnerPidListener,
    OwnerPidConnections,
    OwnerPidAll,
    OwnerModuleListener,
    OwnerModuleConnections,
    OwnerModuleAll
}

public enum UdpTableClass
{
    Basic,
    OwnerPid,
    OwnerModule
}

[StructLayout(LayoutKind.Sequential)]
public struct MIB_TCPROW
{
    public uint dwState;
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwRemoteAddr;
    public uint dwRemotePort;
}

[StructLayout(LayoutKind.Sequential)]
public struct MIB_TCPROW_OWNER_PID
{
    public uint dwState;
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwRemoteAddr;
    public uint dwRemotePort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
public struct MIB_TCP6ROW_OWNER_PID
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] ucLocalAddr;
    public uint dwLocalScopeId;
    public uint dwLocalPort;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] ucRemoteAddr;
    public uint dwRemoteScopeId;
    public uint dwRemotePort;
    public uint dwState;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
public struct MIB_UDPROW_OWNER_PID
{
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
public struct MIB_UDP6ROW_OWNER_PID
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] ucLocalAddr;
    public uint dwLocalScopeId;
    public uint dwLocalPort;
    public uint dwOwningPid;
}

[Flags]
public enum SnapshotFlags : uint
{
    HeapList = 0x00000001,
    Process = 0x00000002,
    Thread = 0x00000004,
    Module = 0x00000008,
    Module32 = 0x00000010,
    All = HeapList | Process | Thread | Module,
    Inherit = 0x80000000,
    NoHeaps = 0x40000000
}

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>
/// 单特权版 TOKEN_PRIVILEGES（PrivilegeCount = 1）。
/// 变长数组在 unsafe 代码里用指针直写，这里只声明固定头 + 首元素。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
public struct FILETIME
{
    public uint DateTimeLow;
    public uint DateTimeHigh;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct PROCESSENTRY32
{
    private const int MAX_PATH = 260;
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public IntPtr th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
    public string szExeFile;
}
