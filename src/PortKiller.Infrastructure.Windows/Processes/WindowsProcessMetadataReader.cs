using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using PortKiller.Models;
using PortKiller.Native;

namespace PortKiller.Processes;

internal readonly record struct ExecutableStamp(long Length, long LastWriteTime);

internal interface IProcessMetadataReader
{
    ProcessObservation? Observe(ProcessReference process, ProcessObservation? previous);
    ExecutableStamp GetStamp(string path);
    ProcessMetadata Read(string path);
}

internal sealed class WindowsProcessMetadataReader : IProcessMetadataReader
{
    public ProcessObservation? Observe(ProcessReference process, ProcessObservation? previous)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            long start = ((long)created.DateTimeHigh << 32) | created.DateTimeLow;
            var identity = new ProcessIdentity(process.Pid, start);
            if (!identity.IsVerified) return null;
            if (previous?.Identity == identity && previous.ProcessName == process.ProcessName) return previous;
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, path, ref length)) return null;
            string fullPath = path.ToString();
            if (!Path.GetFileName(fullPath).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase)) return null;
            return new(identity, process.ProcessName, fullPath);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public ExecutableStamp GetStamp(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    public ProcessMetadata Read(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        string description = Clean(info.FileDescription), product = Clean(info.ProductName);
        string name = description.Length > 0 ? description : product;
        return new(name, description, product, name.Length > 0 ? ProcessMetadataState.Available : ProcessMetadataState.Unavailable);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = new StringBuilder(Math.Min(value.Length, 512));
        foreach (char c in value)
        {
            if (text.Length == 512) break;
            text.Append(char.IsControl(c) ? ' ' : c);
        }
        return text.ToString().Trim();
    }
}
