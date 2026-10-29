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
using PortKiller.Updates;

namespace PortKiller.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly NetworkScannerService _scannerService;
    private readonly ProcessManagerService _processService;
    private readonly ProcessDetailService _detailService;
    private readonly HttpSysDiagnosticService _diagnosticService;
    /// <summary>刷新和复检共享扫描队列。</summary>
    private readonly ScanCoordinator _scans;
    /// <summary>三个释放入口共用结果统计和复检逻辑。</summary>
    private readonly PortReleaseService _releaseService;
    /// <summary>窗口退出后取消在途查询和尚未执行的操作。</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>初始化只执行一次。</summary>
    private bool _started;
    /// <summary>当前扫描任务，用于合并重复刷新请求。</summary>
    private Task? _refreshTask;
    /// <summary>扫描中收到的强制刷新请求，合并到下一轮。</summary>
    private bool _refreshRequested;
    /// <summary>防止不同释放入口同时操作同一批连接。</summary>
    private bool _operationBusy;
    /// <summary>准备、执行与待确认范围共用入口保护，防止快捷键覆盖待确认目标。</summary>
    private bool IsOperationBlocked => _operationBusy || _shuttingDown || IsFamilyConfirmVisible || IsInterceptVisible || IsInstallingUpdate || IsUpdateConfirmVisible;
    /// <summary>详情查询序号；焦点切换后旧查询不得覆盖新详情。</summary>
    private int _inspectorVersion;
    /// <summary>最近一次操作报告，复制时不再误用当前行数据。</summary>
    private string _lastOperationReport = string.Empty;

    private IReadOnlyList<PortConnection> _allItems = Array.Empty<PortConnection>();
    private ulong _lastFingerprint = 0;
    private DispatcherTimer? _liveTimer;
    private CancellationTokenSource? _searchDebounceCts;
    private bool _suppressSettingsSave;
    private DispatcherTimer? _toastTimer;

    // ── 视图切换：0 端口监控 / 1 内核诊断 / 2 系统设置 ──────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPortsViewVisible))]
    [NotifyPropertyChangedFor(nameof(IsKernelViewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsViewVisible))]
    [NotifyPropertyChangedFor(nameof(CurrentViewTitle))]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private int _currentView = 0;

    public bool IsPortsViewVisible => CurrentView == 0;
    public bool IsKernelViewVisible => CurrentView == 1;
    public bool IsSettingsViewVisible => CurrentView == 2;

    /// <summary>
    /// 详情面板仅在「端口监控」视图出现。
    /// 原型中系统设置页为整幅内容区，不带右侧进程详情栏。
    /// </summary>
    public bool ShowInspector => InspectorVisible && IsPortsViewVisible;

    /// <summary>标题栏面包屑的当前页名（原型：PortKiller / 系统设置）。</summary>
    public PortKiller.Shell.ShellViewModel Shell { get; }
    public string CurrentViewTitle => Shell.Selected.Title;

    [RelayCommand]
    private void SwitchTool(string id) => Shell.Select(id);

    private void OnToolSelected(PortKiller.Shell.ToolEntry entry)
    {
        CurrentView = entry.LegacyIndex ?? -1;
        OnPropertyChanged(nameof(CurrentViewTitle));
    }

    [RelayCommand]
    public void SwitchView(string indexText)
    {
        if (int.TryParse(indexText, out int idx)) CurrentView = idx;
    }

    // ── 列表与筛选 ──────────────────────────────────────────────────────
    public ConnectionCollection FilteredItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowAllClearState))]
    private int _filteredCount;

    public bool HasRows => FilteredCount > 0;

    // ── 系统状态（系统状态规范：任何数据界面都不得「空白无解释」）────────
    /// <summary>空状态 · 无匹配：系统有端口，只是被筛选条件过滤干净了。</summary>
    public bool ShowEmptyState => !HasScanError && !ShowSkeleton && FilteredCount == 0 && TotalCount > 0;

    /// <summary>空状态 · 全清：系统本来就没有活跃占用，属于正向结果。</summary>
    public bool ShowAllClearState => !HasScanError && !ShowSkeleton && FilteredCount == 0 && TotalCount == 0;

    /// <summary>加载中 · 骨架屏：首屏或强制刷新且尚无任何数据时占位。</summary>
    public bool ShowSkeleton => IsScanning && TotalCount == 0 && !HasScanError;

    /// <summary>错误态：底层内核 API 调用失败（如 ACCESS_DENIED）。</summary>
    [ObservableProperty] private bool _hasScanError;
    [ObservableProperty] private string _scanErrorTitle = string.Empty;
    [ObservableProperty] private string _scanErrorDescription = string.Empty;

    public string SkeletonSubText => $"GetExtendedTcpTable / UdpTable · 上一次快照 {ScanDurationText}";

    [ObservableProperty]
    private string _searchText = string.Empty;

    public List<string> CategoryOptions { get; } = new()
    {
        "全部分类", "Web / API", "数据库", "开发工具", "代理工具", "内核服务"
    };

    [ObservableProperty]
    private string _selectedCategory = "全部分类";

    public List<string> StateOptions { get; } = new()
    {
        "全部状态", "LISTENING", "ESTABLISHED", "TIME_WAIT"
    };

    [ObservableProperty]
    private string _selectedState = "全部状态";

    // ── 顶部统计条 ──────────────────────────────────────────────────────
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _listeningCount;
    [ObservableProperty] private int _establishedCount;
    [ObservableProperty] private int _timeWaitCount;
    [ObservableProperty] private int _kernelCount;

    [ObservableProperty] private string _scanDurationText = "0.0 ms";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrivilegeStatusText))]
    [NotifyPropertyChangedFor(nameof(PrivilegeStatusDetail))]
    [NotifyPropertyChangedFor(nameof(PrivilegeStatusIsAdmin))]
    private bool _isAdmin;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "就绪。已开启实时监听与增量感知。";

    // ── 批量选择 ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    public bool HasSelection => SelectedCount > 0;

    public string SelectionSummary
    {
        get
        {
            // 单次遍历同时去重 PID 与端口（原先三次 LINQ 枚举）
            var pids = new HashSet<int>();
            var ports = new HashSet<int>();
            foreach (var x in FilteredItems)
            {
                if (!x.IsChecked) continue;
                if (x.Pid > 0) pids.Add(x.Pid);
                ports.Add(x.LocalPort);
            }
            return $"已选中 {pids.Count} 个进程 · 预计释放 {ports.Count} 个端口";
        }
    }

    // ── Toast 反馈 ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastTitle = string.Empty;
    [ObservableProperty] private string _toastDescription = string.Empty;
    [ObservableProperty] private bool _toastIsError;
    [ObservableProperty] private bool _toastHasDetailAction;

    // ── 右侧详情面板 ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _inspectorVisible;
    [ObservableProperty] private string _inspName = "—";
    [ObservableProperty] private string _inspApplicationName = "—";
    [ObservableProperty] private string _inspMeta = "未选中任何进程";
    [ObservableProperty] private string _inspPillText = "—";
    [ObservableProperty] private bool _inspPillListening;
    [ObservableProperty] private bool _inspPillEstablished;
    [ObservableProperty] private bool _inspPillTimeWait;
    [ObservableProperty] private bool _inspPillKernel;
    [ObservableProperty] private bool _inspPillIdle = true;
    [ObservableProperty] private string _inspExePath = "—";
    [ObservableProperty] private string _inspStartTime = "—";
    [ObservableProperty] private string _inspParent = "—";
    [ObservableProperty] private string _inspChildCountText = "子进程树";
    [ObservableProperty] private int _inspCommandLinePort;
    [ObservableProperty] private bool _inspCanKill;
    [ObservableProperty] private bool _inspCanDiagnose;

    public ObservableCollection<PortChipItem> InspPorts { get; } = new();
    public ObservableCollection<ProcessTreeNodeViewModel> InspChildren { get; } = new();

    private PortConnectionItem? _focusedItem;

    // ── 内核诊断视图 ────────────────────────────────────────────────────
    public ObservableCollection<PortConnectionItem> KernelItems { get; } = new();
    public ObservableCollection<DependentServiceItem> DependentServices { get; } = new();

    [ObservableProperty] private bool _kernelEmpty = true;
    [ObservableProperty] private string _kernelHeading = "未检测到内核级端口占用";
    [ObservableProperty] private string _kernelSubheading = "PID 4 (System) 当前没有持有任何 Web 端口";
    [ObservableProperty] private string _diagnosticReport = string.Empty;

    // ── 设置：主题 / 监控 ───────────────────────────────────────────────
    [ObservableProperty] private string _selectedTheme = "跟随系统";
    [ObservableProperty] private bool _isAutoRefresh = true;
    [ObservableProperty] private string _selectedInterval = "2 秒";
    [ObservableProperty] private bool _isAutoStart = true;
    [ObservableProperty] private bool _alwaysRunAsAdmin;

    public bool IsThemeSystem => SelectedTheme == "跟随系统";
    public bool IsThemeDark => SelectedTheme == "深色模式";
    public bool IsThemeLight => SelectedTheme == "浅色模式";

    // ══════════════════════════════════════════════════════════════════
    //  排版设置 · 三条互相独立的轴
    //   ① 界面字号：标题 / 正文 / 辅助说明的基准
    //   ② 数据字号：仅等宽数据列（端口 / PID / 本地地址）
    //   ③ 字体族：枚举本机全部字体，选中即刻全局生效
    //  三者都通过 TypographyService 写排版令牌，不逐控件改属性
    // ══════════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiFontSizeText))]
    [NotifyPropertyChangedFor(nameof(IsUiPresetCompact))]
    [NotifyPropertyChangedFor(nameof(IsUiPresetStandard))]
    [NotifyPropertyChangedFor(nameof(IsUiPresetComfort))]
    [NotifyPropertyChangedFor(nameof(IsUiPresetLarge))]
    private double _uiFontSize = TypographyService.UiDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonoFontSizeText))]
    private double _monoFontSize = TypographyService.MonoDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingScaleText))]
    private bool _scaleHeadings = true;

    /// <summary>预览卡「当前生效值」用：标题缩放的人话描述（bool 不适合直接拼串）。</summary>
    public string HeadingScaleText => ScaleHeadings ? "标题跟随正文" : "标题锁定档位";

    public string UiFontSizeText => $"{UiFontSize:0.#} px";
    public string MonoFontSizeText => $"{MonoFontSize:0.#} px";

    [RelayCommand]
    public void IncreaseUiFontSize()
    {
        if (UiFontSize < 16.0)
            UiFontSize = Math.Min(16.0, Math.Round(UiFontSize + 0.5, 1));
    }

    [RelayCommand]
    public void DecreaseUiFontSize()
    {
        if (UiFontSize > 10.0)
            UiFontSize = Math.Max(10.0, Math.Round(UiFontSize - 0.5, 1));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiFontDisplay))]
    private string _uiFontFamilyName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonoFontDisplay))]
    private string _monoFontFamilyName = string.Empty;

    public string UiFontDisplay => string.IsNullOrWhiteSpace(UiFontFamilyName) ? "Noto Sans SC / Inter" : UiFontFamilyName;
    public string MonoFontDisplay => string.IsNullOrWhiteSpace(MonoFontFamilyName) ? "Inter（等宽数字）" : MonoFontFamilyName;

    /// <summary>最近一次排版切换的实测耗时读数（预算 50 ms）。</summary>
    [ObservableProperty] private string _typographyCostText = "切换耗时 —";

    public bool TypographyWithinBudget => TypographyService.LastApplyWithinBudget;

    // 字体枚举结果与选择
    public ObservableCollection<FontOption> FilteredUiFonts { get; } = new();
    public ObservableCollection<FontOption> FilteredMonoFonts { get; } = new();

    [ObservableProperty] private bool _isFontListLoading = true;
    [ObservableProperty] private string _uiFontSearchText = string.Empty;
    [ObservableProperty] private string _monoFontSearchText = string.Empty;
    [ObservableProperty] private FontOption? _selectedUiFontOption;
    [ObservableProperty] private FontOption? _selectedMonoFontOption;

    private bool _typographyApplyQueued;
    private bool _fontListRequested;
    private CancellationTokenSource? _fontSearchDebounceCts;

    public bool IsInterval1 => SelectedInterval == "1 秒";
    public bool IsInterval2 => SelectedInterval == "2 秒";
    public bool IsInterval3 => SelectedInterval == "3 秒";

    /// <summary>构造只保存依赖，系统查询由窗口显示后的 StartAsync 执行。</summary>
    public MainViewModel() : this(Bootstrap.ApplicationComposition.CreatePortServices()) { }

    internal MainViewModel(HttpSysDiagnosticService diagnosticService, NetworkScannerService? scannerService = null)
        : this(Bootstrap.ApplicationComposition.CreatePortServices(diagnosticService, scannerService)) { }

    public MainViewModel(PortKiller.Modules.Ports.PortToolServices services)
    {
        _portServices = services;
        InitializeProcessMetadata();
        Shell = new PortKiller.Shell.ShellViewModel(Bootstrap.ApplicationComposition.CreateToolCatalog());
        Shell.SelectionChanged += OnToolSelected;
        Shell.UpdatePortCount(0);
        _scannerService = services.Scanner;
        _processService = services.Processes;
        _detailService = services.Details;
        _diagnosticService = services.Diagnostics;
        _scans = services.Scans;
        _releaseService = services.Release;
        InitializeUpdateUi();
    }

    /// <summary>首帧之后加载设置与数据，避免构造函数阻塞窗口显示。</summary>
    public async Task StartAsync()
    {
        if (_started || _shuttingDown) return;
        _started = true;
        try
        {
            _suppressSettingsSave = true;
            var settings = await Task.Run(SettingsService.Load, _lifetime.Token);
            if (_shuttingDown) return;
            RestoreSettings(settings);
            IsAdmin = SecurityHelper.IsRunningAsAdministrator();
            IsAutoStart = await Task.Run(AutoStartHelper.IsAutoStartEnabled, _lifetime.Token);
            if (_shuttingDown) return;
            ApplyThemeVariant(SelectedTheme);
            InitColumnsFromSettings(settings);
            TypographyService.Applied += OnTypographyApplied;
            ApplyTypography();
            _suppressSettingsSave = false;
            _backgroundQueriesEnabled = true;
            await RefreshCoreAsync(true);
            if (!_shuttingDown) InitLiveWatcher();
            if (!_shuttingDown)
            {
                UpdateInstaller.ConfirmHealth();
                StartUpdates();
            }
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { ReportFailure("初始化失败", ex); }
        finally { _suppressSettingsSave = false; }
    }

    /// <summary>恢复界面偏好；调用期间屏蔽保存，防止默认值覆盖用户配置。</summary>
    private void RestoreSettings(AppSettings settings)
    {
        SelectedTheme = settings.Theme;
        AutoCheckForUpdates = settings.AutoCheckForUpdates;
        UpdateUi.SetAutomaticCheck(AutoCheckForUpdates);
        AlwaysRunAsAdmin = settings.AlwaysRunAsAdmin;
        _autoStartPreference.Load(settings.IsAutoStart);
        IsAutoRefresh = settings.IsAutoRefresh;
        SelectedInterval = settings.SelectedInterval;
        UiFontSize = settings.GlobalFontSize;
        MonoFontSize = settings.MonoFontSize;
        ScaleHeadings = settings.ScaleHeadings;
        UiFontFamilyName = settings.UiFontFamily;
        MonoFontFamilyName = settings.MonoFontFamily;
        ReduceMotion = settings.ReduceMotion;
        SettingsTab = settings.SettingsTab;
        HudX = settings.HudX ?? double.NaN;
        HudY = settings.HudY ?? double.NaN;
        IsHudVisible = settings.IsHudVisible;
    }

    /// <summary>异步操作最外层统一记录异常并保留完整原因。</summary>
    private void ReportFailure(string action, Exception error)
    {
        App.WriteLog(action, error);
        if (_shuttingDown) return;
        string reason = error is AggregateException aggregate
            ? string.Join("；", aggregate.Flatten().InnerExceptions.Select(x => x.Message)) : error.Message;
        ShowToast(action, reason, true);
        StatusMessage = action + "：" + reason;
    }
}
