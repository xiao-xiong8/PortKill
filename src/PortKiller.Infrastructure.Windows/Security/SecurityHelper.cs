using System;
using System.Diagnostics;
using System.Security.Principal;

namespace PortKiller.Native;

public static class SecurityHelper
{
    /// <summary>只由父进程传给提权副本，避免副本再次进入普通启动提权分支。</summary>
    public const string ElevationHandoffArgument = "--elevated-restart";

    public static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动提权副本；退出由界面生命周期执行，取消时恢复原实例守卫。</summary>
    public static bool RestartAsAdministrator()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Environment.ProcessPath)) return false;
        bool owned = SingleInstanceGuard.IsOwned;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add(ElevationHandoffArgument);
              if (Array.IndexOf(Environment.GetCommandLineArgs(), "--memory-log") >= 0)
                  startInfo.ArgumentList.Add("--memory-log");
            using var child = Process.Start(startInfo);
            if (child == null) throw new InvalidOperationException("Windows 未创建管理员进程。");
            SingleInstanceGuard.Release();
            return true;
        }
        catch (Exception ex)
        {
            PortKiller.Diagnostics.DiagnosticLog.Write("Elevation", ex);
            if (owned) SingleInstanceGuard.TryAcquire();
            return false;
        }
    }
}
