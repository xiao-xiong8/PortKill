using System;
using PortKiller.Native;

namespace PortKiller.Models;

/// <summary>无界面依赖的网络表快照记录。原始 TCP 参数仅供原生适配器使用。</summary>
public class PortConnection
{
    public string Protocol { get; set; } = "TCP";
    public string IpVersion { get; set; } = "IPv4";
    public string LocalAddress { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public string State { get; set; } = string.Empty;
    public MibTcpState RawState { get; set; }

    public uint RawLocalAddr { get; set; }
    public uint RawLocalPort { get; set; }
    public uint RawRemoteAddr { get; set; }
    public uint RawRemotePort { get; set; }

    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int ParentPid { get; set; }
    public string? ProcessPath { get; set; }

    public bool IsSystemProtected { get; set; }
    public bool IsHttpSys { get; set; }

    public bool IsTimeWait => RawState == MibTcpState.TimeWait;
    public bool IsListening => State.Equals("LISTENING", StringComparison.OrdinalIgnoreCase);
    // State 来自 FormatTcpState 的精确枚举文本，精确匹配避免子串误命中
    public bool IsEstablished => State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为 Windows 核心特权服务进程（名单唯一真相源：Native/SystemProtection.cs）。</summary>
    public bool IsSystemService => SystemProtection.IsPrivilegedService(ProcessName);

    /// <summary>
    /// 内核级占用：PID 4 / HTTP.SYS / 系统保护进程 / Windows 核心特权服务。
    /// 此类端口无法通过终止进程释放，操作列统一降级为中性「诊断」。
    /// </summary>
    public bool IsKernelHeld => !IsTimeWait && (IsHttpSys || IsSystemProtected || Pid == 4 || IsSystemService);

    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);
    public string RemoteEndpoint => RemotePort > 0 ? FormatEndpoint(RemoteAddress, RemotePort) : "*:*";

    private string FormatEndpoint(string address, int port)
        => IpVersion == "IPv6" ? $"[{address}]:{port}" : $"{address}:{port}";

    // 不含状态的连接身份用于跨快照复用；名称参与匹配可识别不同名的 PID 复用，同名实例仍无法区分。
    internal readonly record struct ConnectionIdentity(string Protocol, string IpVersion,
        string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, int Pid, string ProcessName);

    internal ConnectionIdentity Identity => new(Protocol, IpVersion, LocalAddress, LocalPort,
        RemoteAddress, RemotePort, Pid, ProcessName);

    private string? _serviceCategoryCache;

    public string ServiceCategory => _serviceCategoryCache ??= ComputeServiceCategory();

    /// <summary>ProcessName 或保护标志在首次读取分类后被改写时，调用此方法失效缓存。</summary>
    public void InvalidateCategoryCache() => _serviceCategoryCache = null;

    private string ComputeServiceCategory()
    {
        if (IsKernelHeld) return "内核服务";

        string name = ProcessName.ToLowerInvariant();

        if (LocalPort is 3306 or 33060 or 5432 or 6379 or 27017 or 27018 or 1433 or 1521 or 9200 or 9300 or 11211 ||
            name.Contains("mysql") || name.Contains("redis") || name.Contains("postgres") || name.Contains("mongod") || name.Contains("sqlservr"))
        {
            return "数据库";
        }

        if (LocalPort is 80 or 443 or 5173 or 5174 or 8888 ||
            (LocalPort >= 3000 && LocalPort <= 3010) ||
            (LocalPort >= 5000 && LocalPort <= 5010) ||
            (LocalPort >= 8000 && LocalPort <= 8090) ||
            (LocalPort >= 9000 && LocalPort <= 9010) ||
            name.Contains("node") || name.Contains("python") || name.Contains("uvicorn") || name.Contains("vite") ||
            name.Contains("nginx") || name.Contains("caddy") || name.Contains("httpd") || name.Contains("apache") ||
            name.Contains("dotnet") || name.Contains("tomcat") || name.Contains("java"))
        {
            return "Web / API";
        }

        if (LocalPort is 1080 or 1081 or 7890 or 7891 or 10808 or 10809 or 53 or 22 ||
            name.Contains("clash") || name.Contains("v2ray") || name.Contains("xray") || name.Contains("sing-box") ||
            name.Contains("tailscale") || name.Contains("wireguard") || name.Contains("ssh") || name.Contains("frp"))
        {
            return "代理工具";
        }

        if (LocalPort is 2375 or 2376 or 5037 or 9222 or 9229 ||
            name.Contains("docker") || name.Contains("containerd") || name.Contains("wsl") || name.Contains("git") ||
            name.Contains("idea") || name.Contains("code") || name.Contains("adb") || name.Contains("cargo"))
        {
            return "开发工具";
        }

        return "应用";
    }

}
