using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortKiller.Updates;

namespace PortKiller.Features.Updates;

/// <summary>更新窗口的呈现和交互；不持有窗口，不修改安装目录。</summary>
public partial class UpdateViewModel : ObservableObject, IDisposable
{
    private UpdateService? _service;
    private readonly CancellationToken _lifetime;
    private readonly Func<bool>? _beginInstall;
    private readonly Func<Task<string?>>? _install;
    private readonly Action? _cancelInstall;
    private readonly Func<bool, bool>? _saveAutomaticCheck;
    private readonly string _currentVersion;
    private bool _settingPreference, _disposed;
    private UpdateSnapshot _snapshot = new(UpdateStage.Idle, "检查并获取最新版本");
    private UpdateSnapshot? _pendingSnapshot;
    private int _updatePosted;
    private string[]? _notesSource;
    private DateTimeOffset? _lastCheckedAt;
    private IReadOnlyList<UpdateReleaseNoteItem> _releaseNoteItems = [new("更新", "此版本未提供更新说明。")];
    [ObservableProperty] private bool _automaticCheck = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReleaseNotesVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(Eyebrow))]
    [NotifyPropertyChangedFor(nameof(IsFailedStatus))]
    [NotifyPropertyChangedFor(nameof(ShowUpgradeArtwork))]
    private bool _isViewingReleaseNotes;
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _notice = "";

    public event Action? RequestHide;
    public event Action<string>? RequestCopy;
    public UpdateSnapshot Snapshot => _snapshot;
    public bool IsBusy => _snapshot.IsBusy || IsInstalling;
    public bool IsAvailable => _snapshot.Stage == UpdateStage.Available;
    public bool IsTransfer => _snapshot.Stage is UpdateStage.Downloading or UpdateStage.Paused or UpdateStage.Verifying;
    public bool IsPaused => _snapshot.Stage == UpdateStage.Paused;
    public bool IsVerifying => _snapshot.Stage == UpdateStage.Verifying;
    public string DownloadStepText => IsVerifying ? "✓ 下载完成" : "● 下载资源";
    public string VerifyStepText => IsVerifying ? "● 正在校验" : "● 校验文件";
    public bool IsReady => _snapshot.Stage == UpdateStage.Ready;
    public bool IsFailed => _snapshot.Stage == UpdateStage.Failed;
    public bool IsLatest => _snapshot.Stage == UpdateStage.Latest;
    public bool IsNeutral => !IsAvailable && !IsTransfer && !IsReady && !IsFailed && !IsLatest;
    public bool IsChecking => _snapshot.Stage == UpdateStage.Checking;
    public bool IsReleaseNotesVisible => IsAvailable || IsViewingReleaseNotes;
    public bool IsStatusVisible => !IsReleaseNotesVisible && !IsTransfer;
    public bool IsFailedStatus => IsFailed && !IsViewingReleaseNotes;
    public bool ShowUpgradeArtwork => IsReleaseNotesVisible || IsNeutral;
    public bool ShowNonTransferSecondary => IsAvailable || IsReady || IsFailed;
    public bool ShowRestartIcon => IsReady || IsFailed;
    public bool ShowAutomaticCheck => IsAvailable || IsNeutral;
    public bool HasReleaseNotes => _snapshot.Manifest?.ReleaseNotes is { Length: > 0 };
    public IReadOnlyList<UpdateReleaseNoteItem> ReleaseNoteItems => _releaseNoteItems;
    public string Eyebrow => IsViewingReleaseNotes ? "RELEASE NOTES" : IsTransfer ? "GETTING BETTER" : "NEW RELEASE";
    public string AutomaticCheckDescription => AutomaticCheck ? "每次启动时，自动为你检查新版本" : "已关闭，可随时手动检查更新";
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool CanPause => _snapshot.Stage == UpdateStage.Downloading;
    public bool CanPrimary => !IsBusy && !IsConfirming;
    public bool ShowSecondary => IsAvailable || IsTransfer || IsReady || IsFailed;
    public bool ShowProgress => IsTransfer || IsReady;
    public string CurrentVersion => "当前版本 " + _currentVersion;
    public string TargetVersion => _snapshot.Manifest?.Version ?? _currentVersion;
    public string Title => IsViewingReleaseNotes ? "本次更新内容" : IsInstalling ? "正在准备重启安装" : _snapshot.Stage switch
    {
        UpdateStage.Available => "发现新版本",
        UpdateStage.Downloading => "正在下载更新",
        UpdateStage.Paused => "下载已暂停",
        UpdateStage.Verifying => "正在校验更新包",
        UpdateStage.Ready => "一切就绪，焕新出发",
        UpdateStage.Latest => "已是最新，安心使用",
        UpdateStage.Failed => "更新暂时遇到一点问题",
        UpdateStage.Checking => "正在检查更新",
        UpdateStage.Disabled => "自动更新尚未配置",
        _ => "软件更新"
    };
    public string Subtitle => IsReleaseNotesVisible ? $"{CurrentVersion}    /    更新包 {FormatBytes(_snapshot.Manifest?.Size ?? 0)}"
        : IsTransfer ? $"版本 {TargetVersion}    /    {RemainingText}"
        : IsReady ? $"版本 {TargetVersion} 已下载并通过完整性校验" : CurrentVersion;
    public string StatusSubtitle => IsReady ? Subtitle : IsLatest ? CurrentVersion + (_lastCheckedAt is { } time
        ? "    /    上次检查 " + time.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) : "") : Body;
    public string ErrorHeading => _snapshot.Failure switch
    {
        UpdateFailure.Network => "网络连接中断或超时", UpdateFailure.Server => "更新服务暂时不可用",
        UpdateFailure.Integrity => "更新文件未通过校验", UpdateFailure.Storage => "无法写入更新文件",
        UpdateFailure.Configuration => "更新配置不可用", _ => "本次更新未能完成"
    };
    public string ErrorDetail => string.IsNullOrWhiteSpace(_snapshot.ErrorDetail) ? _snapshot.Message : _snapshot.ErrorDetail;
    public string Body => IsReady ? "重启后即可使用新版本。当前操作可以继续，安装失败会恢复旧版本。"
        : IsFailed ? _snapshot.Failure switch
        {
            UpdateFailure.Integrity => "更新内容未通过校验，已停止安装。请确认发布包后重新检查。",
            UpdateFailure.Network => "网络连接中断或请求超时，请检查网络后重试。当前版本可继续使用。",
            UpdateFailure.Server => "更新服务暂时不可用，请稍后重试。当前版本可继续使用。",
            UpdateFailure.Storage => "无法写入更新缓存，请检查磁盘空间与目录权限。",
            UpdateFailure.Configuration => "请检查运行目录的 update-settings.json 发布源与公钥配置。",
            _ => "本次更新未能完成，当前版本可继续使用。可以复制错误详情排查。"
        }
        : IsLatest ? "当前应用已是最新版本，感谢使用 PortKiller。"
        : _snapshot.Stage == UpdateStage.Disabled ? "请先配置服务器地址与签名公钥，配置完成后重新启动应用。"
        : _snapshot.Message;
    public IReadOnlyList<string> ReleaseNotes => _snapshot.Manifest?.ReleaseNotes is { Length: > 0 } notes
        ? notes : new[] { "此版本未提供更新说明。" };
    public string NotesCount => _snapshot.Manifest?.ReleaseNotes is { Length: > 0 } notes ? $"共 {notes.Length} 项" : "";
    public double Percent => _snapshot.Percent;
    public string PercentText => $"{Math.Round(Percent):0}%";
    public string PercentNumber => $"{Math.Round(Percent):0}";
    public string TransferStatus => IsPaused ? "进度已保存" : IsVerifying ? "下载完成，正在校验" : "正在为你准备新版本";
    public string DownloadedText => $"{FormatBytes(_snapshot.DownloadedBytes)} / {FormatBytes(_snapshot.Manifest?.Size ?? 0)}";
    public string SpeedText => IsPaused ? "已暂停" : IsVerifying ? "SHA-256 校验" : _snapshot.BytesPerSecond > 0 ? FormatBytes((long)_snapshot.BytesPerSecond) + "/s" : "正在连接…";
    public string RemainingText => IsPaused ? "点击继续下载" : IsVerifying ? "正在验证完整性"
        : _snapshot.Remaining is { } remaining ? remaining.TotalSeconds < 60 ? $"预计剩余 {Math.Ceiling(remaining.TotalSeconds):0} 秒" : $"预计剩余 {Math.Ceiling(remaining.TotalMinutes):0} 分钟" : "正在估算剩余时间";
    public string PrimaryText => IsInstalling ? "准备安装…" : IsAvailable ? "立即更新" : IsPaused ? "继续下载"
        : IsReady ? "立即重启" : IsFailed ? "重新检查" : IsLatest ? "完成" : IsChecking ? "检查中…" : "检查更新";
    public string SecondaryText => IsFailed ? "稍后再说" : IsAvailable ? "稍后提醒" : IsReady ? "稍后重启" : "后台运行";
    public string ConfirmText => IsInstalling ? "准备安装…" : "确认重启安装";
    public string FooterHint => IsTransfer ? "可在后台下载，不影响当前工作" : IsReady ? "由你确认重启安装" : IsLatest ? "所有功能均已准备就绪" : "当前版本仍可正常使用";
    public string DetailText => $"PortKiller {UpdateService.CurrentVersion}\n{_snapshot.Failure}\n{_snapshot.ErrorDetail}\n{_snapshot.Message}";

    public UpdateViewModel(CancellationToken lifetime = default, Func<bool>? beginInstall = null,
        Func<Task<string?>>? install = null, Action? cancelInstall = null, Func<bool, bool>? saveAutomaticCheck = null,
        string? currentVersion = null)
    {
        _lifetime = lifetime; _beginInstall = beginInstall; _install = install;
        _cancelInstall = cancelInstall; _saveAutomaticCheck = saveAutomaticCheck;
        _currentVersion = currentVersion ?? UpdateService.CurrentVersion;
    }

    public void Attach(UpdateService service)
    {
        if (_service != null) _service.Changed -= OnChanged;
        _service = service; _service.Changed += OnChanged;
        Apply(service.Snapshot);
    }

    private void OnChanged(UpdateSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pendingSnapshot, snapshot);
        if (Interlocked.Exchange(ref _updatePosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _updatePosted, 0);
            var latest = Interlocked.Exchange(ref _pendingSnapshot, null);
            if (!_disposed && latest != null) Apply(latest);
        }, DispatcherPriority.Background);
    }

    internal void Apply(UpdateSnapshot snapshot)
    {
        bool progressOnly = snapshot.Stage == _snapshot.Stage && IsTransfer
            && ReferenceEquals(snapshot.Manifest, _snapshot.Manifest);
        bool stageChanged = snapshot.Stage != _snapshot.Stage;
        _snapshot = snapshot; Notice = "";
        if (progressOnly)
        {
            foreach (string name in ProgressProperties) OnPropertyChanged(name);
            return;
        }
        if (stageChanged) IsViewingReleaseNotes = false;
        if (snapshot.Stage is UpdateStage.Latest or UpdateStage.Available) _lastCheckedAt = DateTimeOffset.UtcNow;
        var notes = snapshot.Manifest?.ReleaseNotes;
        if (!ReferenceEquals(notes, _notesSource))
        {
            _notesSource = notes;
            _releaseNoteItems = notes is { Length: > 0 } ? notes.Select(UpdateReleaseNoteItem.Parse).ToArray()
                : [new("更新", "此版本未提供更新说明。")];
        }
        // 阶段切换更新页面；同阶段进度仅通知进度字段，不重建更新说明或状态模板。
        OnPropertyChanged(string.Empty);
    }
    private static readonly string[] ProgressProperties = [nameof(Snapshot), nameof(Percent), nameof(PercentText),
        nameof(PercentNumber), nameof(DownloadedText), nameof(SpeedText), nameof(RemainingText), nameof(Subtitle)];

    public void SetAutomaticCheck(bool enabled)
    {
        _settingPreference = true; AutomaticCheck = enabled; _settingPreference = false;
    }

    partial void OnAutomaticCheckChanged(bool value)
    {
        OnPropertyChanged(nameof(AutomaticCheckDescription));
        if (!_settingPreference && _saveAutomaticCheck != null && !_saveAutomaticCheck(value))
        { SetAutomaticCheck(!value); Notice = "更新检查偏好保存失败，请重试。"; }
    }
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (!CanPrimary || _disposed) return;
        if (IsLatest) { RequestHide?.Invoke(); return; }
        if (IsReady) { RequestRestartConfirmation(); return; }
        if (_service == null) { Notice = "更新服务尚未就绪，请稍后重试。"; return; }
        if (IsAvailable || IsPaused) await _service.DownloadAvailableAsync(_lifetime);
        else await CheckAsync();
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (_disposed || IsBusy || IsConfirming || _service == null) return;
        int seconds = _service.CheckCooldownSeconds;
        if (seconds > 0) { Notice = $"请在 {seconds} 秒后重新检查更新。"; return; }
        await _service.CheckAsync(_lifetime, download: false);
    }

    public void RequestRestartConfirmation()
    {
        if (!IsReady || IsInstalling || IsConfirming) return;
        if (_beginInstall?.Invoke() != true)
        { Notice = "请先完成当前排查操作，再重启安装。"; return; }
        IsConfirming = true;
        OnPropertyChanged(nameof(CanPrimary));
    }

    [RelayCommand] private void Pause() => _service?.PauseDownload();
    [RelayCommand] private void CopyDetails() => RequestCopy?.Invoke(DetailText);
    [RelayCommand] private void ViewReleaseNotes() { if (HasReleaseNotes) IsViewingReleaseNotes = true; }
    [RelayCommand] private void ReturnToStatus() => IsViewingReleaseNotes = false;
    [RelayCommand] private void Secondary()
    {
        if (IsInstalling) return;
        Hide();
    }
    [RelayCommand] private void Hide()
    {
        if (IsInstalling) return;
        IsViewingReleaseNotes = false;
        CancelConfirmation();
        RequestHide?.Invoke();
    }
    [RelayCommand] private void CancelConfirmation()
    {
        if (IsInstalling) return;
        IsConfirming = false; _cancelInstall?.Invoke();
        OnPropertyChanged(nameof(CanPrimary));
    }
    [RelayCommand]
    private async Task ConfirmRestartAsync()
    {
        if (!IsConfirming || IsInstalling || _install == null) return;
        IsInstalling = true;
        OnPropertyChanged(string.Empty);
        try
        {
            string? error = await _install();
            if (error != null) Notice = error;
        }
        catch (Exception ex) { App.WriteLog("UpdateWindowInstall", ex); Notice = "安装未启动：" + ex.Message; }
        finally { IsInstalling = false; OnPropertyChanged(string.Empty); }
    }
    public void SetNotice(string notice) => Notice = notice;
    public static string FormatBytes(long bytes) => (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _pendingSnapshot, null);
        if (_service != null) _service.Changed -= OnChanged;
    }
}
