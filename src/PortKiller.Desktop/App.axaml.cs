using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller;

public partial class App : Application
{
    public override void Initialize()
    {
        InstallCrashLogger();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
            desktop.ShutdownRequested += (_, _) => (desktop.MainWindow as MainWindow)?.PrepareClose();
            desktop.Exit += (_, _) =>
            {
                (desktop.MainWindow as MainWindow)?.PrepareClose();
                Native.SingleInstanceGuard.Release();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 兜底异常记录：把未处理异常写入 %LocalAppData%\PortKiller\error.log，
    /// 便于在 WinExe（无控制台）下定位渲染期异常。
    /// </summary>
    private static void InstallCrashLogger()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteLog("AppDomain", e.ExceptionObject as Exception);
            // 崩溃退出兜底：进程即将异常终止，不会再走 MainWindow.OnRequestShutdown，
            // 托盘图标的 NIM_DELETE 也就发不出去。这里尽力补删一次，避免 explorer
            // 残留「幽灵图标」（要鼠标划过才消失）。
            TryRemoveTrayIconsBestEffort();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteLog("Task", e.Exception);
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            WriteLog("UIThread", e.Exception);
        };
    }

    /// <summary>
    /// 崩溃退出前的最后一搏。进程因未处理异常终止时不会走 MainWindow.OnRequestShutdown，
    /// 托盘图标的 NIM_DELETE 发不出去，explorer 会残留「幽灵图标」（鼠标划过才消失）。
    /// 这里复用 Avalonia 公开 API 取回活着的 TrayIcon 并 Dispose —— 其内部用注册时
    /// 同一个 Win32Platform 共享窗口句柄 + uID 调 Shell_NotifyIcon(NIM_DELETE)，
    /// hWnd/uID 天然匹配，能可靠移除。全程 try/catch，绝不让清理动作引发二次崩溃。
    /// </summary>
    private static void TryRemoveTrayIconsBestEffort()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;

            var icons = TrayIcon.GetIcons(app);
            if (icons is null) return;

            foreach (var icon in icons)
            {
                try { icon?.Dispose(); } catch { /* 单个失败不影响其余图标清理 */ }
            }
        }
        catch
        {
            // 清理失败保持静默：此刻已在崩溃路径上，不能再抛任何异常
        }
    }

    internal static void WriteLog(string source, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PortKiller");
            Directory.CreateDirectory(dir);

            string logPath = Path.Combine(dir, "error.log");
            PortKiller.Services.OperationLog.TrimIfNeeded(logPath); // 防无界增长

            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, line, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 记录失败时保持静默，绝不因日志而崩溃
        }
    }
}
