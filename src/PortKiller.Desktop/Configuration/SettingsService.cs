using System;
using System.IO;
using System.Text.Json;
using PortKiller.Native;

namespace PortKiller.Services;

public class AppSettings
{
    /// <summary>仅控制自动检测；关闭后仍可手动检测，下载始终由用户启动。</summary>
    public bool AutoCheckForUpdates { get; set; } = true;
    // 默认主题跟随系统/主机
    public string Theme { get; set; } = "跟随系统";

    // 是否始终以管理员身份运行
    public bool AlwaysRunAsAdmin { get; set; } = false;

    // 是否开机自动启动
    public bool IsAutoStart { get; set; } = false;

    /// <summary>
    /// 界面字号基准（设计稿字号阶梯中的「正文与控件 12px」）。
    /// 标题、区块标题、辅助说明均由该基准派生，数据等宽字体不受其影响。
    /// </summary>
    public double GlobalFontSize { get; set; } = 12.0;

    /// <summary>
    /// 数据等宽字号基准（端口 / PID / 端点地址）。默认 12px，与界面字号解耦，
    /// 保证调整界面字号时表格数字列宽与对齐不跳动。
    /// </summary>
    public double MonoFontSize { get; set; } = 12.0;

    /// <summary>标题与统计数字是否跟随界面字号同比例缩放。</summary>
    public bool ScaleHeadings { get; set; } = true;

    /// <summary>界面字体族名。空字符串表示使用内置字体栈。</summary>
    public string UiFontFamily { get; set; } = string.Empty;

    /// <summary>数据等宽字体族名。空字符串表示使用内置等宽字体栈。</summary>
    public string MonoFontFamily { get; set; } = string.Empty;

    /// <summary>
    /// 减少界面动效：关闭悬浮过渡、开关滑块位移与 Toast 淡入。
    /// 首次运行（配置文件尚不存在）时沿用系统「显示动画」偏好
    /// （Win32 SPI_GETCLIENTAREAANIMATION，见 Native/SystemPreferences），
    /// 之后以用户在本页的选择为准。
    /// </summary>
    public bool ReduceMotion { get; set; }

    /// <summary>实时监听开关与频率</summary>
    public bool IsAutoRefresh { get; set; } = true;
    public string SelectedInterval { get; set; } = "1 秒";

    /// <summary>
    /// 上次打开的系统设置分组：startup / appearance / fonts / engine。
    /// 设置页有四个分组，记住上次停留处可以省掉每次重进的二次点击。
    /// </summary>
    public string SettingsTab { get; set; } = "startup";

    // ── 表格列状态（列宽拖拽与列顺序 · 持久化） ────────────────────────
    /// <summary>8 个数据列的像素宽度，CSV；所有业务列均可拖拽调整。默认 96,132,132,220,78,180,132,144。</summary>
    public string ColWidths { get; set; } = ColumnSettingsMigrator.Widths;

    /// <summary>8 个数据列的视觉顺序（逻辑列下标排列），CSV；默认 0,1,2,7,3,4,5,6（应用名称 ID 7 位于进程名之前）。</summary>
    public string ColOrder { get; set; } = ColumnSettingsMigrator.Order;

    /// <summary>8 个数据列的可见性，CSV（bool.Parse 格式）；默认全可见。旧版配置无此字段，由 Normalize 兜底。</summary>
    public string ColVisible { get; set; } = ColumnSettingsMigrator.Visible;

    // ── 桌面悬浮窗 HUD ─────────────────────────────────────────────────
    public bool IsHudVisible { get; set; } = false;

    /// <summary>
    /// HUD 位置。必须用 double?（null = 从未拖动过，落位到工作区右下角）：
    /// 旧实现默认 double.NaN，而 JSON 规范没有 NaN 字面量，
    /// JsonSerializer.Serialize 遇到 NaN 直接抛 InvalidOperationException，
    /// 被 Save 的静默 catch 吞掉后 → 全部设置（含管理员开关）从那天起再没落盘成功过。
    /// </summary>
    public double? HudX { get; set; }
    public double? HudY { get; set; }
}

public static class SettingsService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortKiller");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (settings != null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteLog("SettingsLoad", ex);
        }

        var fallback = new AppSettings();

        // 首次运行（配置文件尚不存在）：减少动效沿用系统「显示动画」偏好，
        // 让「关掉系统动画的用户」一进来就不被本工具的过渡动效打扰（WCAG 2.3）。
        fallback.ReduceMotion = !SystemPreferences.AreAnimationsEnabled();

        Normalize(fallback);
        return fallback;
    }

    /// <summary>把历史版本或越界的配置值收敛回合法区间。</summary>
    private static void Normalize(AppSettings s)
    {
        // NaN 通不过任何比较判断，必须显式拦截，否则会一路穿透 Clamp 再炸掉下一次 Save
        if (double.IsNaN(s.GlobalFontSize) || double.IsInfinity(s.GlobalFontSize)) s.GlobalFontSize = TypographyService.UiDefault;
        if (double.IsNaN(s.MonoFontSize) || double.IsInfinity(s.MonoFontSize)) s.MonoFontSize = TypographyService.MonoDefault;

        s.GlobalFontSize = Clamp(s.GlobalFontSize <= 0 ? TypographyService.UiDefault : s.GlobalFontSize,
            TypographyService.UiMin, TypographyService.UiMax);
        s.MonoFontSize = Clamp(s.MonoFontSize <= 0 ? TypographyService.MonoDefault : s.MonoFontSize,
            TypographyService.MonoMin, TypographyService.MonoMax);

        if (string.IsNullOrWhiteSpace(s.Theme)) s.Theme = "跟随系统";
        if (string.IsNullOrWhiteSpace(s.SelectedInterval)) s.SelectedInterval = "1 秒";
        if (!IsKnownSettingsTab(s.SettingsTab)) s.SettingsTab = "startup";
        s.UiFontFamily = (s.UiFontFamily ?? string.Empty).Trim().Trim('\'', '"');
        s.MonoFontFamily = (s.MonoFontFamily ?? string.Empty).Trim().Trim('\'', '"');
        // 旧的系统默认字体迁移到内嵌原型字体；其他显式自选字体继续保留。
        if (IsLegacyDefaultFont(s.UiFontFamily)) s.UiFontFamily = string.Empty;
        if (IsLegacyDefaultFont(s.MonoFontFamily)) s.MonoFontFamily = string.Empty;
        ColumnSettingsMigrator.Migrate(s);
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    private static bool IsLegacyDefaultFont(string name) =>
        name.Contains("Microsoft YaHei", StringComparison.OrdinalIgnoreCase) || name.Contains("微软雅黑", StringComparison.Ordinal)
        || name.Equals("Consolas", StringComparison.OrdinalIgnoreCase) || name.Equals("Cascadia Mono", StringComparison.OrdinalIgnoreCase);

    /// <summary>设置页分组白名单：旧配置或手改的脏值一律收敛回首组。</summary>
    private static bool IsKnownSettingsTab(string? tab)
        => tab is "startup" or "appearance" or "fonts" or "engine";

    /// <summary>先写临时文件再原子替换，失败交给界面说明，不把失败伪装成已保存。</summary>
    public static bool Save(AppSettings settings) => SaveToPath(settings, ConfigFilePath);

    /// <summary>按指定配置路径保存，独立验证可使用自己的临时目录。</summary>
    internal static bool SaveToPath(AppSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            // 序列化前兜底清洗：任何 NaN/Infinity 都会让 System.Text.Json 抛异常，
            // 进而导致「整个配置文件」持久化失败（历史事故：HUD 坐标 NaN 静默毁掉全部设置）
            Sanitize(settings);

            string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
            WriteAtomically(path, json);
            return true;
        }
        catch (Exception ex)
        {
            // 绝不静默：设置持久化失败必须可在 error.log 追溯
            App.WriteLog("SettingsSave", ex);
            return false;
        }
    }

    /// <summary>同目录替换避免崩溃时把原配置截断；保留上一份文件用于排查。</summary>
    internal static void WriteAtomically(string path, string json)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
        else File.Move(temporary, path);
    }

    private static void Sanitize(AppSettings s)
    {
        if (s.HudX is double hx && (double.IsNaN(hx) || double.IsInfinity(hx))) s.HudX = null;
        if (s.HudY is double hy && (double.IsNaN(hy) || double.IsInfinity(hy))) s.HudY = null;
        if (double.IsNaN(s.GlobalFontSize) || double.IsInfinity(s.GlobalFontSize)) s.GlobalFontSize = TypographyService.UiDefault;
        if (double.IsNaN(s.MonoFontSize) || double.IsInfinity(s.MonoFontSize)) s.MonoFontSize = TypographyService.MonoDefault;
    }
}
