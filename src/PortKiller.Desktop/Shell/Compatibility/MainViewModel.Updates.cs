using System;
using System.IO;
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
    public UpdateViewModel UpdateUi { get; private set; } = null!;
    public event Action? RequestShowUpdates;
    [ObservableProperty] private bool _autoCheckForUpdates = true;
    [ObservableProperty] private string _updateStatus = "自动更新未配置";
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private bool _isUpdateConfirmVisible;
    [ObservableProperty] private bool _isInstallingUpdate;
    public string ApplicationVersion => "当前版本 " + UpdateService.CurrentVersion;
    public string VersionLabel => _updates?.VersionLabel ?? "v" + UpdateService.CurrentVersion;
    public string VersionLabelAot => _updates?.VersionLabelAot ?? VersionLabel + " · Native AOT";

    private void InitializeUpdateUi()
    {
        UpdateUi = new UpdateViewModel(_lifetime.Token,
            beginInstall: () =>
            {
                if (IsOperationBlocked || IsExitConfirmVisible || _updates?.Ready == null) return false;
                IsUpdateConfirmVisible = true; return true;
            },
            install: InstallConfirmedUpdateAsync,
            cancelInstall: () => IsUpdateConfirmVisible = false,
            saveAutomaticCheck: value =>
            {
                bool previous = AutoCheckForUpdates; AutoCheckForUpdates = value;
                if (SaveSettings()) return true;
                AutoCheckForUpdates = previous; return false;
            });
    }

    [RelayCommand] private void OpenUpdates() => RequestShowUpdates?.Invoke();

    private void StartUpdates()
    {
        try
        {
            string install = AppContext.BaseDirectory;
            var options = UpdateOptions.Load(install);
            _updates = new UpdateService(options, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKiller", "updates"));
            _updates.Status += OnUpdateStatus;
            UpdateUi.Attach(_updates);
            _ = _updates.RunAsync(_lifetime.Token, () => AutoCheckForUpdates && !IsUpdateConfirmVisible && !IsInstallingUpdate);
            string result = Path.Combine(install, ".portkiller-update", "result.txt");
            if (File.Exists(result)) UpdateStatus = File.ReadAllText(result);
        }
        catch (Exception ex)
        {
            App.WriteLog("UpdateConfiguration", ex); UpdateStatus = "更新配置无效，请检查 update-settings.json";
            UpdateUi.Apply(new(UpdateStage.Failed, UpdateStatus, Failure: UpdateFailure.Configuration, ErrorDetail: ex.Message));
        }
    }

    private void OnUpdateStatus(string status, bool error) => Dispatcher.UIThread.Post(() =>
    {
        if (_shuttingDown) return;
        bool firstReady = !IsUpdateReady && _updates?.Ready != null;
        UpdateStatus = status; IsUpdateReady = _updates?.Ready != null;
        if (firstReady) ShowToast("应用更新已下载", "打开系统设置的更新窗口，可确认重启安装。");
        else if (_updates?.Snapshot.Stage == UpdateStage.Available && _lastNotifiedUpdate != _updates.Available?.Version)
        {
            _lastNotifiedUpdate = _updates.Available?.Version;
            ShowToast("发现应用新版本", "打开系统设置的更新窗口，可查看更新内容并下载。");
        }
    });
    private string? _lastNotifiedUpdate;

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
            || IsExitConfirmVisible || IsInstallingUpdate || !IsUpdateConfirmVisible || _updates?.ReadyPackage == null)
            return "当前操作尚未完成，请稍后重试。";
        if (!SaveSettings()) return "设置保存失败，已保留当前程序，请检查配置目录权限。";
        IsInstallingUpdate = true;
        try
        {
            // 准备/复检可能读几十 MB，只在后台线程执行。
            await Task.Run(() => UpdateInstaller.ArmAsync(AppContext.BaseDirectory, _updates.ReadyPackage, _lifetime.Token));
            IsUpdateConfirmVisible = false;
            RequestShutdown?.Invoke();
            return null;
        }
        catch (Exception ex) { ReportFailure("更新安装未启动", ex); return "更新安装未启动：" + ex.Message; }
        finally { IsInstallingUpdate = false; }
    }
}
