using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

/// <summary>独立副本等主进程退出后安装；完整备份+持久日志支持失败和中断恢复。
/// 仅支持当前 win-x64 AOT 四文件发布格式；不覆盖用户设置或发布配置。</summary>
public static class UpdateInstaller
{
    internal static readonly string[] Files = ["PortKiller.exe", "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll"];
    private static string Root(string install) => Path.Combine(install, ".portkiller-update");
    private static string JournalPath(string install) => Path.Combine(Root(install), "transaction.json");
    private static FileStream Lock(string install)
    {
        Directory.CreateDirectory(Root(install));
        return new FileStream(Path.Combine(Root(install), "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static async Task ArmAsync(string install, string package, CancellationToken token)
    {
        if (RuntimeFeature.IsDynamicCodeSupported || !OperatingSystem.IsWindows()
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new InvalidOperationException("自动安装仅支持 win-x64 Native AOT 发布包，开发运行不替换文件。");
        using (Lock(install))
        {
            if (File.Exists(JournalPath(install))) throw new IOException("尚有待恢复的更新事务。");
            // 提前验证目标可写，避免退出后才发现 Program Files/只读目录权限不足。
            string probe = Path.Combine(install, ".update-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            CleanupWorkers(install);
        }
        string worker = Path.Combine(Root(install), "worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worker);
        foreach (string file in Files) File.Copy(Path.Combine(install, file), Path.Combine(worker, file));
        File.Copy(package, Path.Combine(worker, "package.zip"));
        File.Copy(package + ".manifest", Path.Combine(worker, "manifest.json"));
        var start = new ProcessStartInfo(Path.Combine(worker, "PortKiller.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = worker };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(install);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装器。");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(worker, "armed")))
        {
            if (process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(15))
                throw new IOException("更新安装器未就绪，程序保持运行。请查看更新日志。");
            await Task.Delay(100, token);
        }
    }

    private static void CleanupWorkers(string install)
    {
        string root = Path.GetFullPath(Root(install)) + Path.DirectorySeparatorChar;
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(path);
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !(name.StartsWith("worker-", StringComparison.Ordinal) || name.StartsWith("recovery-", StringComparison.Ordinal))
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { Directory.Delete(fullPath, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static int RunWorker(string install, int parentPid)
    {
        try
        {
            install = Path.GetFullPath(install);
            using var lease = Lock(install);
            if (File.Exists(JournalPath(install))) throw new IOException("已有待恢复的更新事务。");
            using var parent = Process.GetProcessById(parentPid);
            string worker = AppContext.BaseDirectory;
            var options = UpdateOptions.Load(install); options.Validate();
            byte[] envelope = File.ReadAllBytes(Path.Combine(worker, "manifest.json"));
            var manifest = UpdateManifest.Verify(envelope, options);
            // 安装副本再次检查本机已接受的序号，防止缓存文件在用户确认后被回放替换。
            UpdateTrustStore.Accept(manifest, UpdateTrustStore.DefaultPath);
            if (UpdateManifest.ParseVersion(manifest.Version) <= UpdateManifest.ParseVersion(UpdateService.CurrentVersion))
                throw new InvalidDataException("拒绝降级更新。");
            string package = Path.Combine(worker, "package.zip");
            if (!UpdateService.IsValidPackageAsync(package, manifest, CancellationToken.None).GetAwaiter().GetResult())
                throw new InvalidDataException("安装前更新包复检失败。");
            string stage = Path.Combine(worker, "stage");
            ExtractPackage(package, stage);
            File.WriteAllText(Path.Combine(worker, "armed"), "ready");
            if (!parent.WaitForExit(60000)) throw new TimeoutException("主进程未在 60 秒内退出，取消安装。");
            var journal = new UpdateJournal { Version = manifest.Version, Token = Guid.NewGuid().ToString("N") };
            Process? candidate = null;
            try
            {
                ApplyFiles(install, stage, journal);
                candidate = StartApplication(install, journal.Token);
                var watch = Stopwatch.StartNew();
                string health = Path.Combine(Root(install), "healthy-" + journal.Token);
                while (!File.Exists(health))
                {
                    if (candidate.HasExited || watch.Elapsed > TimeSpan.FromSeconds(45))
                        throw new IOException("新版本未通过启动确认。");
                    Thread.Sleep(100);
                }
                journal.Phase = "committed"; WriteJournal(install, journal);
                File.WriteAllText(Path.Combine(Root(install), "result.txt"), $"更新成功：{journal.Version}");
                File.Delete(JournalPath(install)); File.Delete(health);
            }
            catch
            {
                if (candidate is { HasExited: false }) { candidate.Kill(); candidate.WaitForExit(5000); }
                Rollback(install);
                File.WriteAllText(Path.Combine(Root(install), "result.txt"), $"版本 {journal.Version} 安装失败，已恢复旧版本。");
                // 释放锁后才能正常启动旧版本。
                lease.Dispose();
                StartApplication(install, null).Dispose();
                throw;
            }
            finally { candidate?.Dispose(); }
            return 0;
        }
        catch (Exception ex) { UpdateLog.Write("UpdateInstall", ex); return 1; }
    }

    private static Process StartApplication(string install, string? healthToken)
    {
        var start = new ProcessStartInfo(Path.Combine(install, "PortKiller.exe"))
        { UseShellExecute = false, WorkingDirectory = install };
        if (healthToken != null) { start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(healthToken); }
        return Process.Start(start) ?? throw new IOException("无法重新启动程序。");
    }

    internal static void ExtractPackage(string package, string stage)
    {
        Directory.CreateDirectory(stage);
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count != Files.Length
            || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length
            || zip.Entries.Any(x => !Files.Contains(x.FullName, StringComparer.Ordinal)
                || x.Length is <= 0 or > 134217728)
            || zip.Entries.Sum(x => x.Length) > 268435456)
            throw new InvalidDataException("更新 ZIP 必须仅包含当前 AOT 发布所需的四个根目录文件。");
        foreach (var entry in zip.Entries) entry.ExtractToFile(Path.Combine(stage, entry.FullName), true);
    }

    internal static void ApplyFiles(string install, string stage, UpdateJournal journal, Action<int>? afterReplace = null)
    {
        string backup = Path.Combine(Root(install), "backup"); Directory.CreateDirectory(backup);
        foreach (string file in Files)
        {
            if (!File.Exists(Path.Combine(stage, file))) throw new InvalidDataException("更新内容不完整。");
            CopyDurable(Path.Combine(install, file), Path.Combine(backup, file));
        }
        // 日志在首个替换前落盘，崩溃后根据完整备份恢复所有二进制。
        WriteJournal(install, journal);
        for (int i = 0; i < Files.Length; i++)
        {
            string target = Path.Combine(install, Files[i]);
            string temporary = target + ".update-new";
            CopyDurable(Path.Combine(stage, Files[i]), temporary);
            File.Replace(temporary, target, null);
            afterReplace?.Invoke(i);
        }
        journal.Phase = "awaiting-health"; WriteJournal(install, journal);
    }

    internal static void Rollback(string install)
    {
        if (!File.Exists(JournalPath(install))) return;
        var journal = ReadJournal(install);
        if (journal.Phase != "committed")
        {
            foreach (string file in Files)
            {
                string target = Path.Combine(install, file), temporary = target + ".update-restore";
                CopyDurable(Path.Combine(Root(install), "backup", file), temporary);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
        }
        File.Delete(JournalPath(install));
    }

    public static bool RecoverBeforeStartup(string install, string[] args)
    {
        // 普通安装/开发运行不创建目录，兼容只读发布位置。
        if (!Directory.Exists(Root(install))) return true;
        try
        {
            // 普通启动只读探测安装锁；一次管理员更新不能使后续普通启动要求写目录权限。
            string lockPath = Path.Combine(Root(install), "lock");
            if (!File.Exists(lockPath) && !File.Exists(JournalPath(install))) return true;
            using var lease = File.Exists(lockPath)
                ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : Lock(install);
            if (File.Exists(JournalPath(install)))
            {
                // 当前 exe 也可能是待回滚文件，必须由独立副本等本进程退出后恢复。
                string worker = Path.Combine(Root(install), "recovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(worker);
                foreach (string file in Files)
                    File.Copy(Path.Combine(Root(install), "backup", file), Path.Combine(worker, file));
                var start = new ProcessStartInfo(Path.Combine(worker, "PortKiller.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = worker };
                start.ArgumentList.Add("--recover-update"); start.ArgumentList.Add(install);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(start)?.Dispose();
                return false;
            }
            return true;
        }
        catch (IOException)
        {
            // 只有安装器启动的匹配版本可以在安装锁持有期间进入 UI。
            if (!File.Exists(JournalPath(install))) return false;
            var journal = ReadJournal(install);
            return journal.Phase == "awaiting-health" && args.Length == 2 && args[0] == "--update-health"
                && args[1] == journal.Token
                && UpdateManifest.ParseVersion(journal.Version) == UpdateManifest.ParseVersion(UpdateService.CurrentVersion);
        }
    }

    public static int RunRecoveryWorker(string install, int parentPid)
    {
        try
        {
            // 启动父进程在返回前仍持锁，先等它退出。
            try { using var parent = Process.GetProcessById(parentPid); if (!parent.WaitForExit(60000)) return 1; }
            catch (ArgumentException) { }
            using (Lock(install))
            {
                Rollback(install);
                File.WriteAllText(Path.Combine(Root(install), "result.txt"), "检测到中断的更新，已恢复可用版本。");
            }
            StartApplication(install, null).Dispose();
            return 0;
        }
        catch (Exception ex) { UpdateLog.Write("UpdateRecovery", ex); return 1; }
    }

    public static void ConfirmHealth()
    {
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length != 2 || args[0] != "--update-health") return;
        string install = AppContext.BaseDirectory;
        var journal = ReadJournal(install);
        if (journal.Token != args[1] || journal.Phase != "awaiting-health") return;
        File.WriteAllText(Path.Combine(Root(install), "healthy-" + journal.Token), "ready");
    }

    private static UpdateJournal ReadJournal(string install)
        => JsonSerializer.Deserialize(File.ReadAllText(JournalPath(install)), UpdateJsonContext.Default.UpdateJournal)
            ?? throw new InvalidDataException("更新恢复日志无效。");
    private static void WriteJournal(string install, UpdateJournal journal)
    {
        string path = JournalPath(install), temporary = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, UpdateJsonContext.Default.UpdateJournal);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static void CopyDurable(string source, string target)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
}
