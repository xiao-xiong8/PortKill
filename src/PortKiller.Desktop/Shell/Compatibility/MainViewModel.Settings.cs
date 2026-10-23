using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.Services;

namespace PortKiller.ViewModels;

/// <summary>MainViewModel 分部类：设置与排版（主题 / 自启动 / 字号字体 / 设置页）。</summary>
public partial class MainViewModel
{
    private bool SaveSettings()
    {
        if (_suppressSettingsSave || !_started) return true;
        bool saved = SettingsService.Save(new AppSettings
        {
            Theme = SelectedTheme,
            AutoCheckForUpdates = AutoCheckForUpdates,
            AlwaysRunAsAdmin = AlwaysRunAsAdmin,
            IsAutoStart = IsAutoStart,
            GlobalFontSize = UiFontSize,
            MonoFontSize = MonoFontSize,
            ScaleHeadings = ScaleHeadings,
            UiFontFamily = UiFontFamilyName,
            MonoFontFamily = MonoFontFamilyName,
            ReduceMotion = ReduceMotion,
            IsAutoRefresh = IsAutoRefresh,
            SelectedInterval = SelectedInterval,
            SettingsTab = SettingsTab,
            ColWidths = string.Join(",", _colWidths.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ColOrder = string.Join(",", _colOrder),
            ColVisible = string.Join(",", _colVisible),
            IsHudVisible = IsHudVisible,
            // NaN 哨兵不能进 JSON（Serialize 会抛异常毁掉整份配置），落盘前转回 null
            HudX = double.IsNaN(HudX) ? null : HudX,
            HudY = double.IsNaN(HudY) ? null : HudY
        });
        if (!saved) ShowToast("设置保存失败", "配置文件无法写入，本次设置未持久保存。请检查目录权限并查看 error.log。", true);
        return saved;
    }

    private void ApplyThemeVariant(string themeName)
    {
        if (Application.Current == null) return;

        if (themeName.Equals("浅色模式", StringComparison.OrdinalIgnoreCase))
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        else if (themeName.Equals("深色模式", StringComparison.OrdinalIgnoreCase))
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        else
            Application.Current.RequestedThemeVariant = ThemeVariant.Default;
    }

    // ── 设置项命令 ──────────────────────────────────────────────────────
    [RelayCommand]
    public void SetTheme(string theme)
    {
        SelectedTheme = theme;
    }

    /// <summary>把排版设置恢复到设计稿默认档位。</summary>
    [RelayCommand]
    public void ResetTypography()
    {
        _suppressSettingsSave = true;
        UiFontSize = TypographyService.UiDefault;
        MonoFontSize = TypographyService.MonoDefault;
        ScaleHeadings = true;
        UiFontFamilyName = string.Empty;
        MonoFontFamilyName = string.Empty;

        var fallback = TypographyService.Fonts.FirstOrDefault(f => f.IsDefault);
        if (fallback != null)
        {
            SelectedUiFontOption = fallback;
            SelectedMonoFontOption = fallback;
        }

        _suppressSettingsSave = false;

        RebuildFontLists();
        ApplyTypography();

        // 上面全程抑制回写，这里必须补一次落盘 —— 否则「恢复默认」只改内存不写 config.json
        SaveSettings();
    }

    [RelayCommand]
    public void SetInterval(string interval)
    {
        SelectedInterval = interval;
    }

    partial void OnSelectedThemeChanged(string value)
    {
        ApplyThemeVariant(value);
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        SaveSettings();
    }

    partial void OnSelectedIntervalChanged(string value)
    {
        UpdateTimerInterval();
        OnPropertyChanged(nameof(IsInterval1));
        OnPropertyChanged(nameof(IsInterval2));
        OnPropertyChanged(nameof(IsInterval3));
        SaveSettings();
    }

    partial void OnIsAutoRefreshChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnIsAutoStartChanged(bool value)
    {
        if (_suppressSettingsSave) return;
        bool ok = AutoStartHelper.SetAutoStart(value);
        SaveSettings();
        ShowToast(
            ok ? (value ? "已开启开机自动启动" : "已关闭开机自动启动") : "开机自启动配置失败",
            ok ? "注册表 HKCU\\...\\Run 键已同步更新" : "可能受系统组策略限制，请手动配置",
            isError: !ok);
    }

    partial void OnAlwaysRunAsAdminChanged(bool value)
    {
        if (_suppressSettingsSave || !SaveSettings()) return;
        ShowToast(
            value ? "已开启「始终以管理员权限启动」" : "已关闭「始终以管理员权限启动」",
            value ? "下次启动将自动请求 UAC 提权" : "下次启动将以当前用户权限运行",
            isError: false);
    }

    // ══════════════════════════════════════════════════════════════════
    //  系统设置 · 顶部分组标签页（设计稿「设置中心 · 优化版」）
    //  四组一屏一景：启动与特权 / 外观与排版 / 字体 / 监控引擎。
    //  标签态与开关态全部由 VM 驱动 —— 原生 MVVM，零 code-behind 事件。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>当前打开的设置分组：startup / appearance / fonts / engine（启动时从 config.json 恢复）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsTabStartup))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTabAppearance))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTabFonts))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTabEngine))]
    private string _settingsTab = "startup";

    public bool IsSettingsTabStartup => SettingsTab == "startup";
    public bool IsSettingsTabAppearance => SettingsTab == "appearance";
    public bool IsSettingsTabFonts => SettingsTab == "fonts";
    public bool IsSettingsTabEngine => SettingsTab == "engine";

    [RelayCommand]
    public void SetSettingsTab(string tab)
    {
        if (!string.IsNullOrEmpty(tab)) SettingsTab = tab;
    }

    partial void OnSettingsTabChanged(string value)
    {
        // 切到「字体」组时才惰性枚举本机字体（首次进设置页不阻塞渲染）
        if (value == "fonts") EnsureFontList();
        SaveSettings();
    }

    // ── 分组 1 · 启动与特权 ─────────────────────────────────────────────

    public bool PrivilegeStatusIsAdmin => IsAdmin;

    public string PrivilegeStatusText => IsAdmin ? "管理员 · 可终止特权端口" : "普通用户 · 特权端口受限";

    public string PrivilegeStatusDetail => IsAdmin
        ? "已持有 SeDebugPrivilege，可终止由服务托管（nssm / winsw）或 SYSTEM 运行的进程。"
        : "80 / 443 / 445 等特权端口当前只能诊断；终止会被系统拒绝，需以管理员身份重启。";

    /// <summary>「了解两种模式差异」：不弹窗打断，用 Toast 一次把能力边界讲清。</summary>
    [RelayCommand]
    public void ExplainAdminModes()
    {
        ShowToast(
            "两种启动模式的能力差异",
            "管理员：可终止服务托管 / SYSTEM 进程，能抹杀 80 / 443 / 445 等特权端口；" +
            "普通用户：这些目标只能诊断与查看，终止请求会被系统拒绝。" +
            "选「否」不影响端口扫描、实时监听与普通进程的终止。");
    }

    // ── 分组 2 · 外观与排版 ─────────────────────────────────────────────

    /// <summary>
    /// 减少界面动效：开启后主窗口挂 .nomotion 类，全局摘掉过渡动画
    /// （悬浮渐变、开关滑块位移、Toast 淡入）。首次运行由 SettingsService
    /// 按系统「显示动画」偏好播种，之后以本页选择为准。
    /// 刻意不弹 Toast —— 一个「关掉动效」的开关再弹一个带动效的提示，逻辑上自相矛盾。
    /// </summary>
    [ObservableProperty]
    private bool _reduceMotion;

    partial void OnReduceMotionChanged(bool value) => SaveSettings();

    private const double UiPresetCompactPx = 11;
    private const double UiPresetStandardPx = 12;
    private const double UiPresetComfortPx = 13;
    private const double UiPresetLargePx = 14;

    /// <summary>界面字号的四个预设锚点（连续滑块之外可一键吸附的档位）。</summary>
    public bool IsUiPresetCompact => Math.Abs(UiFontSize - UiPresetCompactPx) < 0.01;
    public bool IsUiPresetStandard => Math.Abs(UiFontSize - UiPresetStandardPx) < 0.01;
    public bool IsUiPresetComfort => Math.Abs(UiFontSize - UiPresetComfortPx) < 0.01;
    public bool IsUiPresetLarge => Math.Abs(UiFontSize - UiPresetLargePx) < 0.01;

    [RelayCommand]
    public void SetUiFontSizePreset(string pxText)
    {
        if (double.TryParse(pxText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double px))
        {
            UiFontSize = px;
        }
    }

    /// <summary>
    /// 恢复设计稿默认：主题跟随系统 · 探测间隔 2 秒 · 界面 12 / 数据 12 · 内置字体栈。
    /// （原实现在 code-behind，现下沉到 VM，与其余设置项同源。）
    /// </summary>
    [RelayCommand]
    public void RestoreDefaults()
    {
        SelectedTheme = "跟随系统";
        SelectedInterval = "2 秒";
        IsAutoRefresh = true;
        if (ReduceMotion) ReduceMotion = false;
        ResetTypography();

        ShowToast(
            "已恢复默认设置",
            "主题跟随系统 · 界面字号 12px · 数据字号 12px · 内置字体栈 · 探测间隔 2 秒");
    }

    // ── 分组 4 · 监控引擎 ───────────────────────────────────────────────

    /// <summary>最近一次扫描的完成时刻，与端口数、耗时合成「最近一次扫描」读数。</summary>
    [ObservableProperty]
    private string _lastScanAtText = "尚未扫描";

    public string LastScanSummary => $"{LastScanAtText} · {TotalCount} 端口 · {ScanDurationText}";

    // ── 配置文件 ────────────────────────────────────────────────────────

    /// <summary>在资源管理器中定位 config.json（文件不存在时退化为打开所在目录）。</summary>
    [RelayCommand]
    public void OpenConfigFolder()
    {
        try
        {
            string path = SettingsService.ConfigFilePathForDisplay;
            string arg = System.IO.File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{System.IO.Path.GetDirectoryName(path)}\"";

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("无法打开配置文件", "资源管理器调用失败，可手动前往 %LocalAppData%\\PortKiller\\",
                isError: true);
            App.WriteLog("OpenConfigFolder", ex);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  排版：应用 / 节流 / 实测耗时
    // ══════════════════════════════════════════════════════════════════

    partial void OnUiFontSizeChanged(double value) => QueueTypographyApply();
    partial void OnMonoFontSizeChanged(double value) => QueueTypographyApply();
    partial void OnScaleHeadingsChanged(bool value) => QueueTypographyApply();

    partial void OnSelectedUiFontOptionChanged(FontOption? value)
    {
        if (value is null) return;
        UiFontFamilyName = value.FamilyName;
        QueueTypographyApply();
    }

    partial void OnSelectedMonoFontOptionChanged(FontOption? value)
    {
        if (value is null) return;
        MonoFontFamilyName = value.FamilyName;
        QueueTypographyApply();
    }

    /// <summary>
    /// 合并同一调度批次的设置变化，在 Background 优先级写入最新排版令牌。
    /// 不丢最后一次变化；不同调度批次仍可能重复布局，不能据此保证零掉帧。
    /// </summary>
    private void QueueTypographyApply()
    {
        if (_typographyApplyQueued || _suppressSettingsSave || _shuttingDown) return;
        _typographyApplyQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _typographyApplyQueued = false;
            if (!_shuttingDown) ApplyTypography();
        }, DispatcherPriority.Background);
    }

    private void ApplyTypography()
    {
        TypographyService.Apply(UiFontSize, MonoFontSize, ScaleHeadings, UiFontFamilyName, MonoFontFamilyName);
        SaveSettings();
    }

    private void OnTypographyApplied()
    {
        TypographyCostText = TypographyService.LastApplyMs > 0
            ? $"切换 {TypographyService.LastApplyMs:0.0} ms / 50 ms"
            : "排版无变化";

        OnPropertyChanged(nameof(TypographyWithinBudget));
    }

    // ══════════════════════════════════════════════════════════════════
    //  排版：本机字体枚举与筛选
    // ══════════════════════════════════════════════════════════════════

    /// <summary>首次进入设置页时才枚举字体，避免拖慢程序启动。</summary>
    private void EnsureFontList()
    {
        if (_fontListRequested) return;
        _fontListRequested = true;

        if (TypographyService.IsFontListReady)
        {
            FinishFontList();
            return;
        }

        IsFontListLoading = true;
        TypographyService.FontListReady += FinishFontList;
        TypographyService.RequestFontList();
    }

    private void FinishFontList()
    {
        TypographyService.FontListReady -= FinishFontList;
        IsFontListLoading = false;
        RebuildFontLists();
        SyncFontSelectionFromNames();
    }

    private void RebuildFontLists()
    {
        var all = TypographyService.Fonts;
        Fill(FilteredUiFonts, all, UiFontSearchText, SelectedUiFontOption);
        Fill(FilteredMonoFonts, all, MonoFontSearchText, SelectedMonoFontOption);
    }

    private static void Fill(ObservableCollection<FontOption> target, IReadOnlyList<FontOption> all,
        string? query, FontOption? keep)
    {
        target.Clear();

        // 当前选中项即使不匹配关键词也保留在列表顶部，避免筛选时选区丢失
        if (keep is not null && !MatchesQuery(keep, query)) target.Add(keep);

        foreach (var option in all)
        {
            if (MatchesQuery(option, query)) target.Add(option);
        }
    }

    private static bool MatchesQuery(FontOption option, string? query)
        => string.IsNullOrWhiteSpace(query)
           || option.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>把持久化下来的字体名还原成列表里的选中项；字体已被卸载时回落到内置默认。</summary>
    private void SyncFontSelectionFromNames()
    {
        var fonts = TypographyService.Fonts;
        if (fonts.Count == 0) return;

        var defaultOption = fonts.FirstOrDefault(f => f.IsDefault);

        var ui = fonts.FirstOrDefault(f => !f.IsDefault
                                           && string.Equals(f.FamilyName, UiFontFamilyName, StringComparison.OrdinalIgnoreCase))
                 ?? defaultOption;
        var mono = fonts.FirstOrDefault(f => !f.IsDefault
                                             && string.Equals(f.FamilyName, MonoFontFamilyName, StringComparison.OrdinalIgnoreCase))
                   ?? defaultOption;

        if (!ReferenceEquals(ui, SelectedUiFontOption)) SelectedUiFontOption = ui;
        if (!ReferenceEquals(mono, SelectedMonoFontOption)) SelectedMonoFontOption = mono;
    }

    partial void OnUiFontSearchTextChanged(string value) => QueueFontSearch(value);
    partial void OnMonoFontSearchTextChanged(string value) => QueueFontSearch(value);

    private void QueueFontSearch(string value)
    {
        _fontSearchDebounceCts?.Cancel();
        _fontSearchDebounceCts = new CancellationTokenSource();
        _ = DebounceFontSearchAsync(_fontSearchDebounceCts.Token);
    }

    /// <summary>110ms 防抖。async/await 替代 ContinueWith：异常可观察、回到 UI 线程无需 Post。</summary>
    private async Task DebounceFontSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(110, token);
            if (!_shuttingDown) RebuildFontLists();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>切换到设置页时按需枚举字体；切到内核诊断页时补算脏数据。</summary>
    partial void OnCurrentViewChanged(int value)
    {
        Shell.SelectLegacy(value);
        if (value == 2) EnsureFontList();
        if (value == 1) _ = LoadServiceStatesAsync();
        if (value == 1 && _kernelViewDirty)
        {
            RefreshKernelView();
            _kernelViewDirty = false;
        }
    }
}
