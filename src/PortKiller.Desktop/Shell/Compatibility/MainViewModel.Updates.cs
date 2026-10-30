using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortKiller.Services;
using PortKiller.Updates;
using PortKiller.Features.Updates;

namespace PortKiller.ViewModels;

public partial class MainViewModel
{
    private UpdateService? _updates;
    private Task? _updateTask;
    private Task? _updateMaintenanceTask;
    private ClientReportService? _clientReports;
    private UpdateService.InstallationLease? _installationLease;
    public UpdateViewModel UpdateUi { get; private set; } = null!;
    public event Action? RequestShowUpdates;
    [ObservableProperty] private bool _autoCheckForUpdates = true;
    [ObservableProperty] private string _updateStatus = "等待检查应用更新";
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private bool _isUpdateConfirmVisible;
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private bool _isUpdateToast;
    public string UpdateToastActionText => UpdateUi?.Snapshot.Stage switch
    {
        UpdateStage.Available => "立即更新",
        UpdateStage.Paused => "继续下载",
        UpdateStage.Ready => "重启更新",
        UpdateStage.Downloading or UpdateStage.Verifying => "查看进度",
        UpdateStage.Failed => "重试更新",
        _ => "查看更新"
    };
    public string ApplicationVersion => "当前版本 " + UpdateService.CurrentVersion;
    public string VersionLabel => _updates?.VersionLabel ?? "v" + UpdateService.CurrentVersion;
    public string VersionLabelAot => _updates?.VersionLabelAot ?? VersionLabel + " · Native AOT";

    public bool IsUpdateBadgeVisible => UpdateUi?.Snapshot.Stage is UpdateStage.Available
        or UpdateStage.Downloading or UpdateStage.Paused or UpdateStage.Verifying
        or UpdateStage.Ready or UpdateStage.Failed;
    public bool IsUpdateBadgeAvailable => UpdateUi?.Snapshot.Stage == UpdateStage.Available;
    public bool IsUpdateBadgeDownloading => UpdateUi?.Snapshot.Stage is UpdateStage.Downloading or UpdateStage.Verifying;
    public bool IsUpdateBadgePaused => UpdateUi?.Snapshot.Stage == UpdateStage.Paused;
    public bool IsUpdateBadgeReady => UpdateUi?.Snapshot.Stage == UpdateStage.Ready;
    public bool IsUpdateBadgeFailed => UpdateUi?.Snapshot.Stage == UpdateStage.Failed;
    public string UpdateBadgeText
    {
        get
        {
            var snapshot = UpdateUi?.Snapshot;
            return snapshot?.Stage switch
            {
                UpdateStage.Available => "新版本",
                UpdateStage.Downloading => snapshot.Percent > 0 ? $"{Math.Round(snapshot.Percent):0}%" : "下载中",
                UpdateStage.Paused => "继续下载",
                UpdateStage.Verifying => "校验中",
                UpdateStage.Ready => "重启更新",
                UpdateStage.Failed => "更新重试",
                _ => string.Empty
            };
        }
    }
    public string UpdateBadgeTooltip
    {
        get
        {
            var snapshot = UpdateUi?.Snapshot;
            string? target = snapshot?.Manifest?.Version;
            return snapshot?.Stage switch
            {
                UpdateStage.Available => target is null ? "发现新版本，点击开始下载" : $"发现新版本 v{target}，点击开始下载",
                UpdateStage.Downloading => target is null ? "正在下载更新，点击查看进度" : $"正在下载 v{target}，点击查看进度",
                UpdateStage.Paused => "更新下载已暂停，点击继续",
                UpdateStage.Verifying => "正在校验更新包，点击查看进度",
                UpdateStage.Ready => target is null ? "更新已准备好，点击重启安装" : $"v{target} 已准备好，点击重启安装",
                UpdateStage.Failed => "更新遇到问题，点击查看详情并重试",
                _ => "打开软件更新"
            };
        }
    }
    public string UpdateBadgeMenuText
    {
        get
        {
            var snapshot = UpdateUi?.Snapshot;
            return snapshot?.Stage switch
            {
                UpdateStage.Available => snapshot.Manifest?.Version is { } version ? $"发现新版本 v{version}" : "发现新版本",
                UpdateStage.Downloading or UpdateStage.Verifying => "查看更新进度",
                UpdateStage.Paused => "继续下载更新",
                UpdateStage.Ready => snapshot.Manifest?.Version is { } version ? $"重启并更新到 v{version}" : "重启并更新",
                UpdateStage.Failed => "重试更新",
                _ => "打开软件更新"
            };
        }
    }

    private void RefreshUpdateBadgeProperties()
    {
        OnPropertyChanged(nameof(IsUpdateBadgeVisible));
        OnPropertyChanged(nameof(IsUpdateBadgeAvailable));
        OnPropertyChanged(nameof(IsUpdateBadgeDownloading));
        OnPropertyChanged(nameof(IsUpdateBadgePaused));
        OnPropertyChanged(nameof(IsUpdateBadgeReady));
        OnPropertyChanged(nameof(IsUpdateBadgeFailed));
        OnPropertyChanged(nameof(UpdateBadgeText));
        OnPropertyChanged(nameof(UpdateBadgeTooltip));
        OnPropertyChanged(nameof(UpdateBadgeMenuText));
        OnPropertyChanged(nameof(UpdateToastActionText));
    }

    private void InitializeUpdateUi()
    {
        UpdateUi = new UpdateViewModel(_lifetime.Token,
            beginInstall: () =>
            {
                if (IsOperationBlocked || IsExitConfirmVisible || _updates == null) return false;
                _installationLease = _updates.TryAcquireInstallation();
                if (_installationLease == null) return false;
                IsUpdateConfirmVisible = true; return true;
            },
            install: InstallConfirmedUpdateAsync,
            cancelInstall: ReleaseInstallation,
            saveAutomaticCheck: value =>
            {
                bool previous = AutoCheckForUpdates; AutoCheckForUpdates = value;
                if (SaveSettings()) { _updates?.NotifyEnvironmentChanged(); return true; }
                AutoCheckForUpdates = previous; return false;
            });
        // ViewModel 合并快照的优先级比服务 Status 回调低；在 UI 快照真正应用后
        // 刷新入口，避免徽章落后一帧或最终阶段永久停在旧状态。
        UpdateUi.PropertyChanged += OnUpdateUiChanged;
    }

    private void OnUpdateUiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(UpdateUi.Snapshot))
            RefreshUpdateBadgeProperties();
    }

    [RelayCommand] private void OpenUpdates() => RequestShowUpdates?.Invoke();

    private void StartUpdates()
    {
        try
        {
            string install = AppContext.BaseDirectory;
            var options = UpdateOptions.Load(install);
            try
            {
                if (options.GetClientReportUri() is { } reportUri)
                    _clientReports = new ClientReportService(reportUri, options.UseSystemProxy,
                        store: new ClientIdentityStore(installDirectory: install),
                        log: code => App.WriteLog("ClientReport", new InvalidOperationException(code)));
            }
            catch { /* Optional reporting configuration never disables the updater. */ }
            _updates = new UpdateService(options, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKiller", "updates"),
                installDirectory: install);
            _updates.Status += OnUpdateStatus;
            UpdateUi.Attach(_updates);
            RefreshUpdateBadgeProperties();
            _updateTask = _updates.RunAsync(_lifetime.Token, () => AutoCheckForUpdates);
            NetworkChange.NetworkAvailabilityChanged += OnUpdateNetworkChanged;
            _updateMaintenanceTask = CompleteUpdateStartupAsync(install);
        }
        catch (Exception ex)
        {
            App.WriteLog("UpdateConfiguration", ex); UpdateStatus = "更新配置无效，请检查 update-settings.json";
            UpdateUi.Apply(new(UpdateStage.Failed, UpdateStatus, Failure: UpdateFailure.Configuration, ErrorDetail: ex.Message));
        }
    }

    private async Task CompleteUpdateStartupAsync(string install)
    {
        UpdateCompletionNotice? completion = null;
        try
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKiller", "updates");
            bool healthLaunch = Array.IndexOf(Environment.GetCommandLineArgs(), "--update-health") >= 0;
            var notice = await UpdateMaintenance.RunAsync(install, cache, UpdateService.CurrentVersion, _lifetime.Token, healthLaunch)
                .ConfigureAwait(false);
            completion = notice;
            // Persist before the completion notice can be consumed; this does not perform network I/O.
            if (_clientReports is { } queuedReports)
                await queuedReports.QueueAsync(UpdateService.CurrentVersion, notice?.Outcome ?? "started", _lifetime.Token)
                    .ConfigureAwait(false);
            if (notice != null)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_shuttingDown) return;
                    UpdateStatus = notice.Message;
                    ShowToast(notice.IsError ? "更新已恢复" : "软件更新完成", notice.Message, notice.IsError);
                    // 先交给 UI，再异步确认已读。关闭/异常若发生在两者之间，下次启动
                    // 会再次显示回执；不等待 UI Dispatcher，避免关闭时与同步停机互锁。
                    _ = Task.Run(() => UpdateMaintenance.MarkConsumed(install, cache, notice, UpdateService.CurrentVersion));
                });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { App.WriteLog("UpdateStartupMaintenance", ex); }
        finally
        {
            // Network work is neither awaited by startup maintenance nor by shutdown.
            if (_clientReports is { } reports)
            {
                var reportToken = _lifetime.Token;
                _ = Task.Run(async () =>
                {
                    if (completion == null)
                        await reports.ReportAsync(UpdateService.CurrentVersion, "started", reportToken);
                    else
                        await reports.RetryPendingAsync(reportToken);
                });
            }
        }
    }

    private void OnUpdateNetworkChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (!args.IsAvailable) return;
        _updates?.NotifyEnvironmentChanged();
        if (_clientReports is { } reports)
            _ = Task.Run(() => reports.RetryPendingAsync(_lifetime.Token, networkAvailable: true));
    }

    internal void NotifyUpdateEnvironmentChanged() => _updates?.NotifyEnvironmentChanged();

    private void ReleaseInstallation()
    {
        IsUpdateConfirmVisible = false;
        _installationLease?.Dispose(); _installationLease = null;
        _updates?.NotifyEnvironmentChanged();
    }

    private void StopUpdates()
    {
        UpdateUi.PropertyChanged -= OnUpdateUiChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnUpdateNetworkChanged;
        _installationLease?.Dispose(); _installationLease = null;
        if (_updateTask != null)
        {
            try { _updateTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        _updateMaintenanceTask?.GetAwaiter().GetResult();
        _clientReports?.Dispose();
    }

    private int _updateStatusPosted;
    private void OnUpdateStatus(string status, bool error)
    {
        if (Interlocked.Exchange(ref _updateStatusPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
        Interlocked.Exchange(ref _updateStatusPosted, 0);
        if (_shuttingDown) return;
        var snapshot = _updates?.Snapshot;
        if (snapshot == null || snapshot.Stage == UpdateStage.Checking) return;
        bool firstReady = !IsUpdateReady && snapshot.Stage == UpdateStage.Ready && _updates?.Ready != null;
        UpdateStatus = snapshot.Message; IsUpdateReady = _updates?.Prepared != null && snapshot.Stage == UpdateStage.Ready;
        RefreshUpdateBadgeProperties();
        string? version = snapshot.Manifest?.Version;
        if (firstReady && _lastNotifiedReady != version)
        {
            _lastNotifiedReady = version;
            ShowUpdateToast("应用更新已下载", "更新已准备好，确认重启后安装。");
        }
        else if (_updates?.Snapshot.Stage == UpdateStage.Available && _lastNotifiedUpdate != _updates.Available?.Version)
        {
            _lastNotifiedUpdate = _updates.Available?.Version;
            ShowUpdateToast("发现应用新版本", $"v{version} 已发布，可以开始下载更新。");
        }
        });
    }
    private string? _lastNotifiedUpdate;
    private string? _lastNotifiedReady;

    private void ShowUpdateToast(string title, string description)
    {
        ShowToast(title, description);
        IsUpdateToast = true;
    }

    [RelayCommand]
    private Task OpenUpdateEntryAsync()
    {
        if (_shuttingDown || IsExitConfirmVisible || IsInstallingUpdate) return Task.CompletedTask;
        RequestShowUpdates?.Invoke();
        if (UpdateUi.Snapshot.Stage is UpdateStage.Available or UpdateStage.Paused)
        {
            // 下载由服务在后台继续；不要让 AsyncRelayCommand 在整段网络请求期间保持运行，
            // 否则标题栏徽章、Toast 和托盘入口会一起被禁用，无法重新打开进度窗口。
            _ = RunUpdateEntryInBackgroundAsync();
            return Task.CompletedTask;
        }
        return UpdateUi.OpenOrRunUpdateAsync();
    }

    private async Task RunUpdateEntryInBackgroundAsync()
    {
        try { await UpdateUi.OpenOrRunUpdateAsync(); }
        catch (Exception ex)
        {
            App.WriteLog("OpenUpdateEntry", ex);
            if (!_shuttingDown) Dispatcher.UIThread.Post(() =>
                ShowToast("更新未能启动", "当前版本仍可使用，请打开更新窗口重试。", true));
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        RequestShowUpdates?.Invoke();
        if (IsUpdateConfirmVisible || IsInstallingUpdate) return;
        await UpdateUi.CheckAsync();
    }

    [RelayCommand]
    private void RequestInstallUpdate()
    {
        if (IsOperationBlocked || IsExitConfirmVisible || IsInstallingUpdate)
        { ShowToast("请先完成当前操作", "关闭待确认操作或等待端口释放结束后，再安装更新。", true); return; }
        if (IsUpdateReady) { RequestShowUpdates?.Invoke(); UpdateUi.RequestRestartConfirmation(); }
    }
    [RelayCommand] private void CancelInstallUpdate() => UpdateUi.CancelConfirmationCommand.Execute(null);

    [RelayCommand]
    private async Task ConfirmInstallUpdateAsync()
        => _ = await InstallConfirmedUpdateAsync();

    private async Task<string?> InstallConfirmedUpdateAsync()
    {
        if (_operationBusy || _shuttingDown || IsFamilyConfirmVisible || IsInterceptVisible
            || IsExitConfirmVisible || IsInstallingUpdate || !IsUpdateConfirmVisible || _installationLease == null)
            return "当前操作尚未完成，请稍后重试。";
        if (!SaveSettings()) return "设置保存失败，已保留当前程序，请检查配置目录权限。";
        IsInstallingUpdate = true;
        try
        {
            // 准备/复检可能读几十 MB，只在后台线程执行。
            string package = _installationLease.Update.PackagePath;
            await Task.Run(() => UpdateInstaller.ArmAsync(AppContext.BaseDirectory, package, _lifetime.Token));
            IsUpdateConfirmVisible = false;
            RequestShutdown?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            if (_clientReports is { } reports)
            {
                var reportToken = _lifetime.Token;
                _ = Task.Run(() => reports.ReportAsync(UpdateService.CurrentVersion, "failed", reportToken));
            }
            ReleaseInstallation(); ReportFailure("更新安装未启动", ex); return "更新安装未启动：" + ex.Message;
        }
        finally { IsInstallingUpdate = false; }
    }
}
