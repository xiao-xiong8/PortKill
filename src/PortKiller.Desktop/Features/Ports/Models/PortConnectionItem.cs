using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PortKiller.Native;

namespace PortKiller.Models;

/// <summary>
/// 网络连接显示行。基于纯快照添加选中态、属性通知与显示样式。
/// </summary>
[ObservableObject]
public partial class PortConnectionItem : PortConnection
{
    public static PortConnectionItem FromSnapshot(PortConnection snapshot)
    {
        if (snapshot is PortConnectionItem row) return row;
        return new PortConnectionItem
        {
            Protocol = snapshot.Protocol,
            IpVersion = snapshot.IpVersion,
            LocalAddress = snapshot.LocalAddress,
            LocalPort = snapshot.LocalPort,
            RemoteAddress = snapshot.RemoteAddress,
            RemotePort = snapshot.RemotePort,
            State = snapshot.State,
            RawState = snapshot.RawState,
            RawLocalAddr = snapshot.RawLocalAddr,
            RawLocalPort = snapshot.RawLocalPort,
            RawRemoteAddr = snapshot.RawRemoteAddr,
            RawRemotePort = snapshot.RawRemotePort,
            Pid = snapshot.Pid,
            ProcessCreationTime = snapshot.ProcessCreationTime,
            ProcessName = snapshot.ProcessName,
            ParentPid = snapshot.ParentPid,
            ProcessPath = snapshot.ProcessPath,
            IsSystemProtected = snapshot.IsSystemProtected,
            IsHttpSys = snapshot.IsHttpSys,
        };
    }

    /// <summary>
    /// 全字段行键（懒缓存），供比较和诊断使用；ApplySnapshot 更新状态时失效。
    /// 列表归并使用类型化 Identity，避免每秒拼接全表字符串。
    /// </summary>
    private string? _rowKeyCache;
    private ProcessMetadata? _metadata;
    private static readonly PropertyChangedEventArgs ApplicationNameChanged = new(nameof(ApplicationName));
    private static readonly PropertyChangedEventArgs ApplicationDisplayChanged = new(nameof(ApplicationNameDisplay));
    private static readonly PropertyChangedEventArgs ApplicationTipChanged = new(nameof(ApplicationNameTip));
    public string ApplicationName => !IsTimeWait ? _metadata?.ApplicationName ?? "" : "";
    public string ApplicationNameDisplay => ApplicationName.Length > 0 ? ApplicationName : "—";
    public string ApplicationNameTip => ApplicationName.Length > 0 ? ApplicationName :
        !IsTimeWait && _metadata?.State == ProcessMetadataState.Pending ? "应用名称读取中" : "应用名称不可用";
    internal void ApplyMetadata(ProcessMetadata? metadata)
    {
        if (_metadata == metadata) return;
        _metadata = metadata;
        OnPropertyChanged(ApplicationNameChanged);
        OnPropertyChanged(ApplicationDisplayChanged);
        OnPropertyChanged(ApplicationTipChanged);
    }
    public string RowKey => _rowKeyCache ??=
        $"{Protocol}|{IpVersion}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}|{State}|{Pid}|{ProcessName}";

    /// <summary>只在 UI 线程归并时更新。身份字段不变；状态、保护标志及派生绑定同步失效。</summary>
    internal bool ApplySnapshot(PortConnection next)
    {
        bool displayChanged = State != next.State || RawState != next.RawState ||
            IsSystemProtected != next.IsSystemProtected || IsHttpSys != next.IsHttpSys;
        bool stateChanged = State != next.State;
        bool rawStateChanged = RawState != next.RawState;
        bool protectedChanged = IsSystemProtected != next.IsSystemProtected;
        bool httpChanged = IsHttpSys != next.IsHttpSys;
        State = next.State;
        RawState = next.RawState;
        IsSystemProtected = next.IsSystemProtected;
        IsHttpSys = next.IsHttpSys;
        RawLocalAddr = next.RawLocalAddr;
        RawLocalPort = next.RawLocalPort;
        RawRemoteAddr = next.RawRemoteAddr;
        RawRemotePort = next.RawRemotePort;
        if (ParentPid != next.ParentPid) { ParentPid = next.ParentPid; OnPropertyChanged(nameof(ParentPid)); }
        if (ProcessPath != next.ProcessPath) { ProcessPath = next.ProcessPath; OnPropertyChanged(nameof(ProcessPath)); }
        if (!displayChanged) return false;
        _rowKeyCache = null;
        InvalidateCategoryCache();
        if (stateChanged) OnPropertyChanged(nameof(State));
        if (rawStateChanged) OnPropertyChanged(nameof(RawState));
        if (protectedChanged) OnPropertyChanged(nameof(IsSystemProtected));
        if (httpChanged) OnPropertyChanged(nameof(IsHttpSys));
        foreach (string name in SnapshotProperties) OnPropertyChanged(name);
        return true;
    }

    private static readonly string[] SnapshotProperties =
    {
        nameof(RowKey), nameof(IsTimeWait), nameof(IsListening), nameof(IsEstablished), nameof(IsKernelHeld),
        nameof(ApplicationName), nameof(ApplicationNameDisplay), nameof(ApplicationNameTip),
        nameof(ServiceCategory), nameof(IsCatWeb), nameof(IsCatDb), nameof(IsCatDev), nameof(IsCatNet),
        nameof(IsCatSys), nameof(IsCatApp), nameof(IsPillListen), nameof(IsPillEstablished), nameof(IsPillTimeWait),
        nameof(IsPillKernel), nameof(IsPillIdle), nameof(StatusBadge), nameof(ProcessDisplay),
        nameof(ShowKillAction), nameof(ShowReleaseAction), nameof(ShowDiagnoseAction),
        nameof(ServiceColor), nameof(ServiceBadgeBg), nameof(StatusBadgeColor), nameof(StatusBadgeBg)
    };

    // ── 行状态（由视图模型驱动，非只读绑定） ─────────────────────────────
    /// <summary>批量勾选态 —— 直接驱动行底色与勾选框外观。</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>焦点态 —— 决定右侧详情面板展示哪一条。</summary>
    [ObservableProperty]
    private bool _isFocused;

    // ── 智能服务语义分类 ─────────────────────────────────────────────────
    // 结果懒缓存：IsCatWeb/IsCatDb/… 6 个样式开关绑定 + 筛选 + 排序都会反复读
    // ServiceCategory，端口/进程名启发式匹配每行只允许真正跑一次。
    // 分类 → 样式类开关（视图层据此套用语义色，天然跟随主题切换）
    public bool IsCatWeb => ServiceCategory == "Web / API";
    public bool IsCatDb => ServiceCategory == "数据库";
    public bool IsCatDev => ServiceCategory == "开发工具";
    public bool IsCatNet => ServiceCategory == "代理工具";
    public bool IsCatSys => ServiceCategory == "内核服务";
    public bool IsCatApp => ServiceCategory == "应用";

    // 状态 → 样式类开关
    public bool IsPillListen => !IsKernelHeld && IsListening;
    public bool IsPillEstablished => !IsKernelHeld && !IsListening && IsEstablished;
    public bool IsPillTimeWait => !IsKernelHeld && IsTimeWait;
    public bool IsPillKernel => IsKernelHeld;
    public bool IsPillIdle => !IsKernelHeld && !IsListening && !IsEstablished && !IsTimeWait;

    // ── 状态标记文本 ─────────────────────────────────────────────────────
    public string StatusBadge
    {
        get
        {
            if (IsTimeWait) return "TIME_WAIT";
            if (IsHttpSys) return "HTTP.SYS";
            if (IsSystemProtected) return "SYSTEM";
            if (IsListening) return "LISTENING";
            return string.IsNullOrWhiteSpace(State) ? "UNKNOWN" : State;
        }
    }

    /// <summary>进程列展示文本：TIME_WAIT 孤儿连接没有存活进程。</summary>
    public string ProcessDisplay =>
        IsTimeWait ? "连接已关闭 · TIME_WAIT" : ProcessName;

    // ── 操作列语义 ───────────────────────────────────────────────────────
    /// <summary>普通进程 → 玫瑰色「终止」</summary>
    public bool ShowKillAction => !IsKernelHeld && !IsTimeWait;
    /// <summary>TIME_WAIT → 橙色「释放」</summary>
    public bool ShowReleaseAction => IsTimeWait;
    /// <summary>内核占用 → 中性「诊断」</summary>
    public bool ShowDiagnoseAction => IsKernelHeld;

    // ── 兼容旧调用（命令行自检程序引用的色值属性） ───────────────────────
    public string ServiceColor => ServiceCategory switch
    {
        "Web / API" => "#38BDF8",
        "数据库" => "#FBBF24",
        "开发工具" => "#A78BFA",
        "代理工具" => "#34D399",
        "内核服务" => "#FB7185",
        _ => "#A1A1AA"
    };

    public string ServiceBadgeBg => ServiceCategory switch
    {
        "Web / API" => "#1F0EA5E9",
        "数据库" => "#1FF59E0B",
        "开发工具" => "#1F8B5CF6",
        "代理工具" => "#1F10B981",
        "内核服务" => "#1FF43F5E",
        _ => "#1FA1A1AA"
    };

    public string StatusBadgeColor
    {
        get
        {
            if (IsKernelHeld) return "#F87171";
            if (IsListening) return "#34D399";
            if (IsTimeWait) return "#FB923C";
            if (IsEstablished) return "#60A5FA";
            return "#A1A1AA";
        }
    }

    public string StatusBadgeBg
    {
        get
        {
            if (IsKernelHeld) return "#29EF4444";
            if (IsListening) return "#2910B981";
            if (IsTimeWait) return "#29F97316";
            if (IsEstablished) return "#293B82F6";
            return "#24A1A1AA";
        }
    }
}
