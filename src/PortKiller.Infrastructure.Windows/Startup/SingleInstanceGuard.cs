using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PortKiller.Native;

/// <summary>
/// 单实例守卫（命名互斥体 + 已有实例激活）。
///
/// 为什么必须单实例 —— 双开会出现两个托盘图标、两套实时监听同时扫描，
/// 且两边都能发起强杀指令，互相竞争同一批目标进程，审计日志也会交叉污染。
///
/// 互斥体用 Global\ 前缀：整台电脑只允许一个实例，且显式授予不同权限级别访问权。
/// UAC 提权重启前必须调用 Release()，否则提权副本会因互斥体未释放而自杀。
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = @"Global\PortKiller_SingleInstance";
    private const string WindowTitlePrefix = "PortKiller";

    private static Mutex? _mutex;

    /// <summary>用于取消提权后恢复原实例持有状态。</summary>
    public static bool IsOwned => _mutex != null;

    /// <summary>
    /// 尝试取得整台电脑唯一实例权。提权接管时旧实例正在释放互斥体，不能把
    /// 这个短暂的竞争误判为「已有实例」并激活旧窗口，因此会短暂重试且不
    /// 调用 ActivateExistingInstance。普通启动仍保持原有的二次启动行为。
    /// </summary>
    public static bool TryAcquire(bool handoff = false)
    {
        if (handoff)
        {
            return TryAcquireWithRetry(MutexName);
        }

        if (TryAcquireCore(MutexName, out var error)) return true;
        if (error != null) PortKiller.Diagnostics.DiagnosticLog.Write("SingleInstance", error);
        ActivateExistingInstance();
        return false;
    }

    /// <summary>仅供独立回归程序使用，避免测试抢占用户实例的互斥体。</summary>
    internal static bool TryAcquireForTest(string mutexName, bool handoff = false)
        => handoff ? TryAcquireWithRetry(mutexName) : TryAcquireCore(mutexName, out _);

    private static bool TryAcquireWithRetry(string mutexName)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (TryAcquireCore(mutexName, out lastError)) return true;
            Thread.Sleep(50);
        }

        if (lastError != null) PortKiller.Diagnostics.DiagnosticLog.Write("SingleInstanceHandoff", lastError);
        return false;
    }

    private static bool TryAcquireCore(string mutexName, out Exception? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = new PlatformNotSupportedException("单实例互斥体仅支持 Windows。");
            return false;
        }
        if (_mutex != null) return true;

        Mutex? mutex = null;
        try
        {
            // Explicit ACL keeps standard and elevated launches on the same lock.
            mutex = MutexAcl.Create(false, mutexName, out _, CreateMutexSecurity());
            try
            {
                if (!mutex.WaitOne(0)) return false;
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership even when it reports an abandoned owner.
            }

            _mutex = mutex;
            mutex = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            try { mutex?.Dispose(); } catch { /* ignore cleanup failure */ }
        }
    }

    [SupportedOSPlatform("windows")]
    private static MutexSecurity CreateMutexSecurity()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>释放实例权（UAC 提权重启前调用，让提权副本能顺利接管）。</summary>
    public static void Release()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex == null) return;

        try { mutex.ReleaseMutex(); } catch { /* ownership may already be gone */ }
        try { mutex.Dispose(); } catch { /* ignore cleanup failure */ }
    }

    /// <summary>
    /// 找到已有实例的主窗口并拉到前台。窗口可能正收在托盘（Hide 状态），
    /// FindWindow 对隐藏窗口同样有效，用 SW_SHOW 恢复可见。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            IntPtr target = IntPtr.Zero;

            EnumThreadWndProc callback = (hwnd, _) =>
            {
                var sb = new StringBuilder(256);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                if (sb.ToString().StartsWith(WindowTitlePrefix, StringComparison.Ordinal))
                {
                    target = hwnd;
                    return false; // 命中即停止枚举
                }
                return true;
            };

            EnumWindows(callback, IntPtr.Zero);
            if (target == IntPtr.Zero) return;

            ShowWindow(target, IsIconic(target) ? SW_RESTORE : SW_SHOW);
            SetForegroundWindow(target);
        }
        catch
        {
            // 激活失败不影响「拒绝二次启动」的决定
        }
    }

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumThreadWndProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
}
