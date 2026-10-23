using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PortKiller.Services;

/// <summary>可选择的字体项（用于设置页字体列表）。</summary>
public sealed class FontOption
{
    public FontOption(string familyName, string displayName, FontFamily preview, bool isDefault)
    {
        FamilyName = familyName;
        DisplayName = displayName;
        Preview = preview;
        IsDefault = isDefault;
    }

    /// <summary>持久化到 config.json 的字体族名；空串代表内置字体栈。</summary>
    public string FamilyName { get; }

    /// <summary>列表里显示的名字。</summary>
    public string DisplayName { get; }

    /// <summary>用该字体自身渲染的预览。</summary>
    public FontFamily Preview { get; }

    public bool IsDefault { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 排版令牌的唯一真相源。
///
/// 设计稿 Design Tokens 给出的是一组固定字号阶梯（主标题 20 / 区块标题 14 / 卡片标题 13 /
/// 正文 12 / 辅助 11 / 数据等宽 12）。这里把它们角色化后用两条互相独立的轴驱动：
///   · 界面字号（GlobalFontSize）：派生 标题 / 统计 / 区块 / 卡片 / 正文 / 辅助 / 微标签
///   · 数据字号（MonoFontSize）：只驱动等宽数据字体（端口 / PID / 端点）
/// 所以滑动「界面字号」时，表格里的等宽数字不会跟着变，列宽与对齐保持稳定。
///
/// 全部令牌通过 DynamicResource 下发，切换时只写入 Application.Resources 的少数几个键，
/// 由 Avalonia 的资源失效机制一次完成传播 + 重排，避免逐控件改写属性带来的卡顿。
/// </summary>
public static class TypographyService
{
    public const double UiMin = 10;
    public const double UiMax = 16;
    public const double UiDefault = 12;

    public const double MonoMin = 10;
    public const double MonoMax = 14;
    public const double MonoDefault = 12;

    public const string DefaultUiFontStack = AppFontCollection.LatinFamily;

    public const string DefaultMonoFontStack = AppFontCollection.LatinFamily;

    // ── 资源键名（与 App.axaml 中的默认令牌一一对应）────────────────────
    public const string KeyFontUi = "AppFontUi";
    public const string KeyFontMono = "AppFontMono";
    public const string KeyTitle = "TypeTitle";
    public const string KeyStat = "TypeStat";
    public const string KeySection = "TypeSection";
    public const string KeyCard = "TypeCard";
    public const string KeyBody = "TypeBody";
    public const string KeyCaption = "TypeCaption";
    public const string KeyMicro = "TypeMicro";
    public const string KeyTiny = "TypeTiny";
    public const string KeyMono = "TypeMono";
    public const string KeyMonoCaption = "TypeMonoCaption";
    public const string KeyMonoPort = "TypeMonoPort";
    public const string KeyDialogTitle = "TypeDialogTitle";
    public const string KeyDialogBody = "TypeDialogBody";
    public const string KeyDialogCaption = "TypeDialogCaption";
    public const string KeyDialogActionWidth = "DialogActionWidth";

    /// <summary>硬性预算：任何一次排版切换都必须在 50ms 内完成并重排。</summary>
    public const double BudgetMs = 50.0;

    private static int _generation;
    private static Stopwatch? _clock;
    private static List<FontOption>? _fontCache;
    private static bool _fontCacheBuilding;
    private static FontFamily? _builtinUiFont;
    private static FontFamily? _builtinMonoFont;

    /// <summary>最近一次切换「资源写入 → 完成重排」的实测耗时（毫秒）。</summary>
    public static double LastApplyMs { get; private set; } = 0;

    /// <summary>最近一次切换是否落在 50ms 预算内。</summary>
    public static bool LastApplyWithinBudget => LastApplyMs > 0 && LastApplyMs <= BudgetMs;

    /// <summary>耗时测量完成后触发（UI 线程）。</summary>
    public static event Action? Applied;

    /// <summary>字体枚举完成后触发（UI 线程）。</summary>
    public static event Action? FontListReady;

    public static bool IsFontListReady => _fontCache != null;
    public static bool IsFontListBuilding => _fontCacheBuilding;

    // ══════════════════════════════════════════════════════════════════
    //  令牌计算
    // ══════════════════════════════════════════════════════════════════

    /// <summary>由界面字号 + 数据字号推导出全部排版令牌。</summary>
    public static Dictionary<string, double> Compute(double uiSize, double monoSize, bool scaleHeadings)
    {
        double s = Clamp(uiSize, UiMin, UiMax);
        double m = Clamp(monoSize, MonoMin, MonoMax);

        // 标题族：关闭「标题跟随」后回到设计稿原始档位，正文仍可自由缩放
        double title = scaleHeadings ? s + 8 : 20;
        double stat = scaleHeadings ? s + 7 : 19;
        double section = scaleHeadings ? s + 2 : 14;
        double card = scaleHeadings ? s + 1 : 13;

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [KeyTitle] = title,
            [KeyStat] = stat,
            [KeySection] = section,
            [KeyCard] = card,
            [KeyBody] = s,
            [KeyCaption] = Math.Max(10, s - 1),
            [KeyMicro] = Math.Max(9, s - 2),
            [KeyTiny] = Math.Max(8, s - 3),
            [KeyDialogTitle] = scaleHeadings ? Math.Max(18, s + 6) : 18,
            [KeyDialogBody] = Math.Max(14, s + 2),
            [KeyDialogCaption] = Math.Max(13, s + 1),
            [KeyDialogActionWidth] = Math.Max(144, 144 + (s - UiDefault) * 8),

            // 数据等宽：完全由数据字号驱动，不受界面字号影响
            [KeyMono] = m,
            [KeyMonoCaption] = Math.Max(9, m - 1),
            [KeyMonoPort] = m + 2,
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  应用
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 写入排版令牌并启动耗时测量。同一帧内的多次调用天然合并（调用方负责节流），
    /// 内部只做「比较 → 写入变化项」，未变化的令牌不会引起任何重排。
    /// </summary>
    public static void Apply(double uiSize, double monoSize, bool scaleHeadings,
        string? uiFontFamily, string? monoFontFamily)
    {
        var app = Application.Current;
        if (app is null) return;

        var res = app.Resources;
        bool dirty = false;

        foreach (var pair in Compute(uiSize, monoSize, scaleHeadings))
        {
            if (res.TryGetResource(pair.Key, null, out var existing)
                && existing is double d && Math.Abs(d - pair.Value) < 0.01)
            {
                continue;
            }

            res[pair.Key] = pair.Value;
            dirty = true;
        }

        // 先把 App.axaml 里声明的内置字体实例抓下来缓存（仅首次有效），
        // 之后无论用户切到哪个字体，都能一键还原到与设计稿完全一致的默认解析路径。
        _builtinUiFont ??= CaptureBuiltin(res, KeyFontUi, DefaultUiFontStack);
        _builtinMonoFont ??= CaptureBuiltin(res, KeyFontMono, DefaultMonoFontStack);

        // 使用内置字体栈时原样复用缓存实例，不重建 —— 避免默认态出现任何解析差异。
        var uiFont = string.IsNullOrWhiteSpace(uiFontFamily)
            ? _builtinUiFont
            : new FontFamily(uiFontFamily.Trim());
        if (SetFontResource(res, KeyFontUi, uiFont)) dirty = true;

        var monoFont = string.IsNullOrWhiteSpace(monoFontFamily)
            ? _builtinMonoFont
            : new FontFamily(monoFontFamily.Trim());
        if (SetFontResource(res, KeyFontMono, monoFont)) dirty = true;

        if (!dirty)
        {
            // 没有任何令牌变化 —— 直接记为 0，不制造无意义的重排
            if (LastApplyMs != 0)
            {
                LastApplyMs = 0;
                Applied?.Invoke();
            }
            return;
        }

        int gen = ++_generation;
        _clock = Stopwatch.StartNew();

        // Loaded 优先级低于 Render：回调执行时本轮的布局与渲染已经完成，
        // 因此这里的读数就是用户真实感知到的「切换耗时」。
        Dispatcher.UIThread.Post(() =>
        {
            if (gen != _generation) return;
            LastApplyMs = _clock?.Elapsed.TotalMilliseconds ?? 0;
            Applied?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    private static bool SetFontResource(IResourceDictionary res, string key, FontFamily font)
    {
        if (res.TryGetResource(key, null, out var existing)
            && existing is FontFamily current
            && string.Equals(current.ToString(), font.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        res[key] = font;
        return true;
    }

    private static FontFamily CaptureBuiltin(IResourceDictionary res, string key, string fallbackStack)
        => res.TryGetResource(key, null, out var value) && value is FontFamily family
            ? family
            : new FontFamily(fallbackStack);

    private static double Clamp(double v, double min, double max)
        => double.IsNaN(v) ? min : v < min ? min : v > max ? max : v;

    // ══════════════════════════════════════════════════════════════════
    //  本机字体枚举（惰性 + 单次 + 缓存）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 枚举本机字体；字体后端仍在 UI 线程访问，每约 4ms 让出一次输入和渲染调度。
    /// </summary>
    public static void RequestFontList()
    {
        if (_fontCache != null || _fontCacheBuilding) return;
        _fontCacheBuilding = true;

        Dispatcher.UIThread.Post(async () =>
        {
            try { _fontCache = await BuildFontListAsync(); }
            finally { _fontCacheBuilding = false; }
            FontListReady?.Invoke();
        }, DispatcherPriority.Background);
    }

    public static IReadOnlyList<FontOption> Fonts => _fontCache ?? (IReadOnlyList<FontOption>)Array.Empty<FontOption>();

    /// <summary>探针：该字体族能否真正创建出字形，不能则不进候选列表。</summary>
    private static bool IsUsable(string familyName)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(familyName)), out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按短时间片验证字体；测试可提供字体集合和验证器，检查输入是否能在加载中执行。</summary>
    internal static async Task<List<FontOption>> BuildFontListAsync(
        IEnumerable<FontFamily>? families = null, Func<string, bool>? validate = null)
    {
        var list = new List<FontOption>
        {
            new(string.Empty, "默认（Noto Sans SC / Inter）", new FontFamily(DefaultUiFontStack), true)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Inter" };

        try
        {
            validate ??= IsUsable;
            var slice = Stopwatch.StartNew();
            foreach (var family in families ?? FontManager.Current.SystemFonts)
            {
                string name = family.Name;
                // 少量字体族（DirectWrite 下的别名条目，如 Microsoft YaHei UI）无法直接
                // 实例化字形，渲染时才抛异常会直接杀掉进程 —— 这里提前探针，不可用的直接剔除。
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name) && validate(name))
                    list.Add(new FontOption(name, name, new FontFamily(name), false));
                if (slice.Elapsed.TotalMilliseconds >= 4)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    slice.Restart();
                }
            }
        }
        catch
        {
            // 字体枚举失败时至少保留内置默认项
        }

        var sorted = list.Skip(1)
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        list.RemoveRange(1, list.Count - 1);
        list.AddRange(sorted);
        return list;
    }
}
