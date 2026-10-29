using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Media;
using System;
using System.Linq;
using PortKiller.Native;
using PortKiller.Services;
using PortKiller.Updates;

namespace PortKiller;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        PortKiller.Diagnostics.DiagnosticLog.Sink = App.WriteLog;
        UpdateLog.Sink = App.WriteLog;
        // 安装器必须先于 Avalonia、单实例和 UAC 初始化，避免加载待替换的原生 DLL。
        if (args.Length == 3 && args[0] is "--apply-update" or "--recover-update" && int.TryParse(args[2], out int parentPid))
        {
            Environment.ExitCode = args[0] == "--apply-update"
                ? UpdateInstaller.RunWorker(args[1], parentPid)
                : UpdateInstaller.RunRecoveryWorker(args[1], parentPid);
            return;
        }
        try { if (!UpdateInstaller.RecoverBeforeStartup(AppContext.BaseDirectory, args)) return; }
        catch (Exception ex) { App.WriteLog("UpdateRecovery", ex); Environment.ExitCode = 1; return; }

        if (args.Length > 0 && (args.Contains("--verify") || args.Contains("--bench")))
        {
            App.WriteLog("Verification", new InvalidOperationException(
                "验证已迁移到独立测试项目，请运行 dotnet run --project tests/PortKiller.Regression/PortKiller.Regression.csproj -- --ui。"));
            Environment.ExitCode = 2;
            return;
        }

        bool elevationHandoff = args.Contains(SecurityHelper.ElevationHandoffArgument, StringComparer.Ordinal);

        // 先查单实例，再决定是否提权，重复打开已运行程序不再反复弹 UAC。
        // 提权副本使用接管模式：父进程刚释放互斥体时只等待接管，不激活旧窗口。
        if (!elevationHandoff && !SingleInstanceGuard.TryAcquire()) return;
        try
        {
            var settings = SettingsService.Load();
            if (elevationHandoff && !SecurityHelper.IsRunningAsAdministrator())
            {
                App.WriteLog("Elevation", new InvalidOperationException(
                    "提权接管副本未获得管理员令牌，已停止启动，避免循环重启。"));
                return;
            }

            if (!elevationHandoff && settings.AlwaysRunAsAdmin && !SecurityHelper.IsRunningAsAdministrator()
                && SecurityHelper.RestartAsAdministrator()) return;

            if (!SingleInstanceGuard.IsOwned && !SingleInstanceGuard.TryAcquire(elevationHandoff)) return;
            // 设置是持久偏好；每次正常启动同步当前 EXE 路径，移动安装目录后也能修复旧启动项。
            // 仅生产入口写真实 Run 键，独立回归宿主不会执行此处。
            if (!AutoStartHelper.SetAutoStart(settings.IsAutoStart))
                App.WriteLog("AutoStartSync", new InvalidOperationException("无法同步开机自动启动设置。"));
            PrivilegeHelper.EnableSeDebugPrivilege();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally { SingleInstanceGuard.Release(); }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .ConfigureFonts(manager => manager.AddFontCollection(new AppFontCollection()))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = AppFontCollection.LatinFamily,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(AppFontCollection.ChineseFamily) }]
            })
            // ══════════════════════════════════════════════════════════════
            //  Windows 显示与渲染开关
            // ══════════════════════════════════════════════════════════════
            .With(new Win32PlatformOptions
            {
                // 渲染后端回退链：ANGLE(OpenGL ES) → Vulkan → WGL → 软件光栅。
                // 末端必须保留 Software，否则在缺 av_libglesv2.dll 或显卡驱动异常的
                // 机器上会直接黑屏；退化到软件光栅只影响 GPU 加速，不影响任何功能。
                RenderingMode =
                [
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Vulkan,
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.Software
                ],

                // 合成后端回退链。WinUI 组合器是唯一支持无边框窗口
                // 「圆角 + 背景模糊」的原生路径，优先级必须最高。
                CompositionMode =
                [
                    Win32CompositionMode.WinUIComposition,
                    Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface
                ],

                // 本窗口 WindowDecorations=None + TransparencyLevelHint=Transparent，
                // 由 DWM 背板按 12px 生成圆角，与根 Border 的 CornerRadius 对齐，
                // 消除窗口边缘「内容已圆角 / 背板仍直角」露出的白边。
                WinUICompositionBackdropCornerRadius = 12,

                // 弹出层（ComboBox 下拉 / 右键菜单 / ToolTip）内嵌进窗口渲染。
                // 无边框窗口下原生弹窗是独立 HWND，会脱离窗口圆角与层级、并在窗口
                // 重绘时闪一下；内嵌后落点、层级与主题完全跟随窗口。
                OverlayPopups = true
            })
            .LogToTrace();

}
