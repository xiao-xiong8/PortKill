using System.Diagnostics;
using System.Globalization;

namespace PortKiller.Updates;

/// <summary>A process-start identity shared by a publisher and its child tool in the same PID namespace.</summary>
public static class PublisherProcessIdentity
{
    public static long ReadStartToken(Process process)
    {
        // Linux Process.StartTime uses a per-process estimate of boot wall-clock time.
        // The raw kernel starttime field is stable across processes and clock adjustments.
        return OperatingSystem.IsLinux()
            ? ParseLinuxStartTime(File.ReadAllText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/stat"))
            : process.StartTime.ToUniversalTime().Ticks;
    }

    internal static long ParseLinuxStartTime(string stat)
    {
        int endOfName = stat.LastIndexOf(')');
        if (endOfName < 0) throw new InvalidDataException("The publisher process identity is unavailable.");
        ReadOnlySpan<char> fields = stat.AsSpan(endOfName + 1).Trim();
        // Fields after comm start at field 3 (state); starttime is field 22.
        for (int index = 0; index < 19; index++)
        {
            int separator = fields.IndexOf(' ');
            if (separator < 0) throw new InvalidDataException("The publisher process identity is incomplete.");
            fields = fields[(separator + 1)..].TrimStart();
        }
        int end = fields.IndexOf(' ');
        if (end >= 0) fields = fields[..end];
        if (!long.TryParse(fields, NumberStyles.None, CultureInfo.InvariantCulture, out long token) || token <= 0)
            throw new InvalidDataException("The publisher process start identity is invalid.");
        return token;
    }
}
