using System;
using System.IO;
using Microsoft.Win32;

namespace PortKiller.Services;

public static class AutoStartHelper
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PortKiller";

    public static bool IsAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            if (key == null) return false;

            object? val = key.GetValue(AppName);
            return val != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutoStart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return false;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\"");
                    return true;
                }
                return false;
            }
            else
            {
                key.DeleteValue(AppName, false);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}
