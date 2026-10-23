using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Services;

public class HttpSysDiagnosticResult
{
    public int Port { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> MatchedUrls { get; set; } = new();
    public List<string> SuspiciousServices { get; set; } = new();
    public string RawDiagnosticOutput { get; set; } = string.Empty;
}

public class HttpSysDiagnosticService
{
    private static readonly Dictionary<string, string> CommonHttpServices = new(StringComparer.OrdinalIgnoreCase)
    {
        { "W3SVC", "World Wide Web Publishing Service (IIS 网页服务)" },
        { "WAS", "Windows Process Activation Service (IIS 进程激活服务)" },
        { "WinRM", "Windows Remote Management (WS-Management)" },
        { "ReportServer", "SQL Server Reporting Services" },
        { "MsDepSvc", "Web Deployment Agent Service" },
        { "PeerDistSvc", "BranchCache 缓存服务" },
        { "SSDPSRV", "SSDP Discovery (通用即插即用服务)" },
        { "iphlpsvc", "IP Helper 辅助服务" }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SERVICE_RUNNING = 0x00000004;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    /// <summary>并发读取 URL 保留规则及实际注册状态；保留查询失败证据。</summary>
    public virtual async Task<HttpSysDiagnosticResult> DiagnosePortAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口范围必须是 1 到 65535。");
        var servicesTask = Task.Run(GetActiveHttpServices, cancellationToken);
        var aclTask = QueryNetshAsync("http show urlacl", cancellationToken);
        var stateTask = QueryNetshAsync("http show servicestate view=requestq verbose=yes", cancellationToken);
        await Task.WhenAll(servicesTask, aclTask, stateTask).ConfigureAwait(false);
        var acl = await aclTask;
        var state = await stateTask;
        var services = await servicesTask;
        var urls = MatchPortLines(acl.Output, port);
        var registered = MatchPortLines(state.Output, port);
        return new HttpSysDiagnosticResult
        {
            Port = port,
            MatchedUrls = urls,
            SuspiciousServices = services,
            RawDiagnosticOutput = state.Output,
            Summary = BuildReport(port, (acl, state), (urls, registered), services)
        };
    }

    /// <summary>解析 URL 的完整端口数字，80 不匹配 8080，支持通配主机及 IPv6。</summary>
    public static List<string> MatchPortLines(string output, int port)
    {
        var result = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            foreach (Match match in Regex.Matches(line, @"https?://(?:\[[^\]]+\]|[^/\s:]+)(?::(?<port>\d+))?(?:/|\s|$)", RegexOptions.IgnoreCase))
            {
                int candidate = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value)
                    : match.Value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
                if (candidate == port) { result.Add(line.Trim()); break; }
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>区分实际注册证据与权限配置；常见服务只作为排查线索。</summary>
    private static string BuildReport(int port,
        ((bool Success, string Output) Acl, (bool Success, string Output) State) queries,
        (List<string> Acl, List<string> State) matches, List<string> services)
    {
        var report = new StringBuilder($"端口 {port} 的内核占用诊断\n\n");
        report.AppendLine(!queries.State.Success ? "根因尚未确定：HTTP 注册状态查询失败。"
            : matches.State.Count > 0 ? "已发现该端口的 HTTP.sys 实际注册记录。"
            : "尚未发现该端口的 HTTP.sys 注册证据，不能仅凭 PID 4 判定具体组件。");
        report.AppendLine("URLACL 仅表示 URL 使用权限，不证明当前监听。运行中的常见服务也不等于占用者。\n");
        report.AppendLine("匹配的实际注册地址：\n" + string.Join("\n", matches.State));
        report.AppendLine("匹配的 URL 保留规则：\n" + string.Join("\n", matches.Acl));
        report.AppendLine("可能关联的运行服务（尚未与目标端口关联）：\n" + string.Join("\n", services));
        if (!queries.Acl.Success) report.AppendLine("URLACL 查询失败：" + queries.Acl.Output);
        report.AppendLine("\n下一步：依据请求队列中的进程 ID 确认所属服务，再到服务控制台处理；不要仅凭服务名称停止无关服务。");
        report.AppendLine("\n实际注册状态原始证据（含请求队列与进程 ID）：\n" + queries.State.Output);
        return report.ToString();
    }

    public static void OpenServicesManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "services.msc",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 枚举 8 类 Web 依赖服务的安装与运行状态，供「内核诊断」视图直接渲染表格。
    /// 全部走原生服务管理器，单次调用 &lt;1ms。
    /// </summary>
    public static List<PortKiller.Models.DependentServiceItem> GetServiceStates()
    {
        var list = new List<PortKiller.Models.DependentServiceItem>();
        IntPtr hScManager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (hScManager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取系统服务管理器失败。");

        try
        {
            foreach (var (name, description) in CommonHttpServices)
                list.Add(ReadServiceState(hScManager, name, description));
        }
        finally { CloseServiceHandle(hScManager); }
        return list;
    }

    /// <summary>服务不存在与无读取权限分开处理，不能把拒绝访问显示成未安装。</summary>
    private static PortKiller.Models.DependentServiceItem ReadServiceState(IntPtr manager, string name, string description)
    {
        IntPtr handle = OpenService(manager, name, SERVICE_QUERY_STATUS);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1060) throw new Win32Exception(error, $"读取服务 {name} 失败 [Win32 {error}]。");
            return new() { ServiceName = name, DisplayName = description, IsInstalled = false };
        }
        try
        {
            var status = new SERVICE_STATUS();
            if (!QueryServiceStatus(handle, ref status))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取服务 {name} 的运行状态失败。");
            return new() { ServiceName = name, DisplayName = description,
                IsInstalled = true, IsRunning = status.dwCurrentState == SERVICE_RUNNING };
        }
        finally { CloseServiceHandle(handle); }
    }

    /// <summary>复用服务状态读取，候选服务列表不承担占用判定。</summary>
    private static List<string> GetActiveHttpServices()
        => GetServiceStates().Where(x => x.IsRunning).Select(x => $"{x.ServiceName} - {x.DisplayName}").ToList();

    /// <summary>诊断命令有统一超时，stdout/stderr 并行读取以免管道阻塞。</summary>
    private static async Task<(bool Success, string Output)> QueryNetshAsync(string arguments, CancellationToken cancellationToken)
    {
        const int timeoutSeconds = 5;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(CultureInfoCurrent());
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"), Arguments = arguments,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding,
            UseShellExecute = false, CreateNoWindow = true
        }};
        try
        {
            if (!process.Start()) return (false, "无法启动系统诊断命令。");
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string text = (await output) + (await error);
            return (process.ExitCode == 0, $"[退出码 {process.ExitCode}]\n{text}");
        }
        catch (OperationCanceledException)
        {
            TryStopDiagnostic(process);
            cancellationToken.ThrowIfCancellationRequested();
            return (false, $"诊断命令超时（{timeoutSeconds} 秒）：{arguments}");
        }
        catch (Exception ex)
        {
            PortKiller.Diagnostics.DiagnosticLog.Write("HttpSysDiagnostic", ex);
            return (false, "诊断命令执行失败：" + ex.Message);
        }
    }

    /// <summary>取消时只结束本次创建的诊断进程。</summary>
    private static void TryStopDiagnostic(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) { PortKiller.Diagnostics.DiagnosticLog.Write("DiagnosticStop", ex); }
    }

    private static int CultureInfoCurrent()
    {
        try
        {
            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        }
        catch
        {
            return Encoding.UTF8.CodePage;
        }
    }
}
