using System.Diagnostics;
using System.Text.Json;
using PortKiller.Features.Ports.Services;
using PortKiller.Models;
using PortKiller.Processes;
using PortKiller.Services;

namespace PortKiller.Regression;

/// <summary>独立进程诊断，不启动 UI、托盘或用户设置，不结束其他进程。</summary>
internal static class ApplicationNameProbe
{
    internal static async Task Run(int seconds, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var log = new StreamWriter(output, append: false) { AutoFlush = true };
        void Write(object sample) => log.WriteLine(JsonSerializer.Serialize(sample));
        Write(new { kind = "environment", time = DateTimeOffset.Now, seconds, os = Environment.OSVersion.VersionString,
            cpuCount = Environment.ProcessorCount, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            process = Environment.ProcessPath, scope = "real network scans + 1000-process metadata churn; no UI" });
        using var provider = new WindowsProcessMetadataProvider();
        using var session = provider.CreateSession();
        var scanner = new NetworkScannerService(); scanner.SetMetadataSession(session);
        var baseline = new NetworkScannerService();
        using var churn = new WindowsProcessMetadataProvider(new ChurnReader());
        using var churnSession = churn.CreateSession();
        var churnReferences = Enumerable.Range(10000, 1000).Select(pid => new ProcessReference(pid, "probe.exe")).ToArray();
        var scanTimes = new List<double>(); var baselineTimes = new List<double>();
        foreach (int size in new[] { 1000, 10000 })
        {
            var rows = Enumerable.Range(0, size).Select(i => ApplicationNameChecks.Row(10000 + i, 7000 + i % 1000, "probe.exe")).ToArray();
            var metadata = rows.Select(x => x.ProcessIdentity).Distinct().ToDictionary(x => x,
                x => new ProcessMetadata("测试应用 " + x.Pid, "共享文件描述", "Probe", ProcessMetadataState.Available));
            var times = new List<double>();
            for (int i = 0; i < 220; i++)
            {
                var query = new PortQuery(rows, metadata, i % 2 == 0 ? "测试应用" : "共享 443", "全部分类", "全部状态", 7, i % 3 == 0);
                long start = Stopwatch.GetTimestamp(); var result = PortConnectionQueryService.Evaluate(query);
                if (i >= 20) times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Program.Assert(result.Count == size, "性能数据集搜索结果不完整。");
            }
            Write(new { kind = "query", rows = size, processes = 1000, samples = times.Count,
                p50 = Percentile(times, .50), p95 = Percentile(times, .95), p99 = Percentile(times, .99) });
        }
        var clock = Stopwatch.StartNew();
        for (int iteration = 0; clock.Elapsed.TotalSeconds < seconds; iteration++)
        {
            long start;
            // 交替执行先后顺序，减少缓存和系统负载对单侧测量的偏置。
            ScanResult current;
            if (iteration % 2 == 0)
            {
                start = Stopwatch.GetTimestamp(); baseline.ScanAllPorts(); baselineTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                start = Stopwatch.GetTimestamp(); current = scanner.ScanAllPorts(); scanTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                start = Stopwatch.GetTimestamp(); current = scanner.ScanAllPorts(); scanTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                start = Stopwatch.GetTimestamp(); baseline.ScanAllPorts(); baselineTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            churnSession.Observe(iteration % 4 < 2 ? churnReferences : Array.Empty<ProcessReference>());
            if (iteration % 15 == 0)
            {
                using var process = Process.GetCurrentProcess(); process.Refresh();
                Write(new { kind = "sample", elapsed = clock.Elapsed.TotalSeconds, iteration, connections = current.TotalCount,
                    liveProcessMetadata = provider.Snapshot.Count, fileReads = provider.FileReads, churnProcesses = churn.Snapshot.Count,
                    churnFileReads = churn.FileReads, privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64,
                    managed = GC.GetTotalMemory(false), allocated = GC.GetTotalAllocatedBytes(), handles = process.HandleCount,
                    scanP95 = Percentile(scanTimes.Skip(Math.Min(20, Math.Max(0, scanTimes.Count - 1))).ToList(), .95),
                    baselineP95 = Percentile(baselineTimes.Skip(Math.Min(20, Math.Max(0, baselineTimes.Count - 1))).ToList(), .95) });
            }
            await Task.Delay(1000);
        }
        session.Observe(Array.Empty<ProcessReference>()); churnSession.Observe(Array.Empty<ProcessReference>());
        Write(new { kind = "complete", elapsed = clock.Elapsed.TotalSeconds, samples = scanTimes.Count,
            scanP95 = Percentile(scanTimes.Skip(20).ToList(), .95), baselineP95 = Percentile(baselineTimes.Skip(20).ToList(), .95),
            remainingProcessMetadata = provider.Snapshot.Count, remainingChurnMetadata = churn.Snapshot.Count });
        Console.WriteLine("应用名称采集与查询诊断完成：" + Path.GetFullPath(output));
    }
    internal static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort(); return values[(int)Math.Min(values.Count - 1, Math.Ceiling(percentile * values.Count) - 1)];
    }
    private sealed class ChurnReader : IProcessMetadataReader
    {
        public ProcessObservation? Observe(ProcessReference process, ProcessObservation? previous) =>
            new(new(process.Pid, 1 + Environment.TickCount64 / 2000), process.ProcessName, $"C:\\probe\\{process.Pid % 100}.exe");
        public ExecutableStamp GetStamp(string path) => new(100, 1);
        public ProcessMetadata Read(string path) => new("测试应用", "测试应用", "Probe", ProcessMetadataState.Available);
    }
}
