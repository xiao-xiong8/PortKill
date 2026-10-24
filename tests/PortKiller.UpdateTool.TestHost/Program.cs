using System.Diagnostics;
using System.Text.Json;

if (args.Contains("--portkill-test-child-hang", StringComparer.Ordinal))
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

int configIndex = Array.IndexOf(args, "--config");
if (configIndex < 0 || configIndex + 1 >= args.Length) return;
using var config = JsonDocument.Parse(await File.ReadAllTextAsync(args[configIndex + 1]));
string mode = config.RootElement.GetProperty("testMode").GetString() ?? "";
if (mode == "output")
{
    Console.Write(new string('x', 2_000_000));
    return;
}
if (mode == "spawn")
{
    string pidPath = config.RootElement.GetProperty("pidPath").GetString()!;
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--portkill-test-child-hang" }
    })!;
    await File.WriteAllTextAsync(pidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
