using System;
using Avalonia.Controls;

namespace PortKiller.Views;

public partial class MainWindow
{
    private void InitializeUpdateSignals()
    {
        if (!OperatingSystem.IsWindows()) return;
        Win32Properties.AddWndProcHookCallback(this, OnUpdatePowerMessage);
        Closed += (_, _) => Win32Properties.RemoveWndProcHookCallback(this, OnUpdatePowerMessage);
    }

    private IntPtr OnUpdatePowerMessage(IntPtr window, uint message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        // WM_POWERBROADCAST: resume critical, resume suspend, resume automatic.
        if (IsUpdateResumeMessage(message, parameter)) Vm?.NotifyUpdateEnvironmentChanged();
        return IntPtr.Zero;
    }

    internal static bool IsUpdateResumeMessage(uint message, nint parameter)
        => message == 0x0218 && parameter is 6 or 7 or 18;
}
