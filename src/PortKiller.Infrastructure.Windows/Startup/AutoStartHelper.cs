using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PortKiller.Services;

public static class AutoStartHelper
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PortKiller";

    public static bool IsAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsAutoStartEnabled(Registry.CurrentUser, RunRegistryKey, Environment.ProcessPath);
    }

    public static bool SetAutoStart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return SetAutoStart(enable, Registry.CurrentUser, RunRegistryKey, Environment.ProcessPath);
    }

    // 独立注册表子项供回归验证使用，测试不写真实 Run 键。
    [SupportedOSPlatform("windows")]
    internal static bool IsAutoStartEnabled(RegistryKey root, string registryPath, string? executable)
    {
        try
        {
            string? command = BuildCommand(executable);
            if (command == null) return false;
            using var key = root.OpenSubKey(registryPath, false);
            return key?.GetValue(AppName) is string value
                && string.Equals(value, command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PortKiller.Diagnostics.DiagnosticLog.Write("AutoStartRead", ex);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool SetAutoStart(bool enable, RegistryKey root, string registryPath, string? executable)
    {
        try
        {
            string? command = BuildCommand(executable);
            // 不把 dotnet.exe、测试宿主或缺失的文件注册成开机启动项。
            if (command == null) return false;

            if (enable)
            {
                using var key = root.CreateSubKey(registryPath, true);
                if (key == null) return false;
                key.SetValue(AppName, command, RegistryValueKind.String);
                return IsAutoStartEnabled(root, registryPath, executable);
            }
            else
            {
                using var key = root.OpenSubKey(registryPath, true);
                key?.DeleteValue(AppName, false);
                return key?.GetValue(AppName) == null;
            }
        }
        catch (Exception ex)
        {
            PortKiller.Diagnostics.DiagnosticLog.Write("AutoStartWrite", ex);
            return false;
        }
    }

    private static string? BuildCommand(string? executable)
        => !string.IsNullOrWhiteSpace(executable) && File.Exists(executable)
            && string.Equals(Path.GetFileName(executable), "PortKiller.exe", StringComparison.OrdinalIgnoreCase)
                ? $"\"{executable}\"" : null;
}
