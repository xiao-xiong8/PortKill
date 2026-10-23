using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PortKiller.Features.Updates;

/// <summary>关闭/后台运行仅隐藏窗口；任务与 ViewModel 跟随应用生命周期。</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel _viewModel;
    private bool _closingForShutdown;

    public UpdateWindow() : this(new UpdateViewModel()) { }

    public UpdateWindow(UpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestHide += HideWindow;
        viewModel.RequestCopy += CopyDetail;
        Closing += (_, e) =>
        {
            if (_closingForShutdown) return;
            e.Cancel = true;
            if (!viewModel.IsInstalling) { viewModel.CancelConfirmationCommand.Execute(null); Hide(); }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || viewModel.IsInstalling) return;
            if (viewModel.IsConfirming) viewModel.CancelConfirmationCommand.Execute(null);
            else Hide();
            e.Handled = true;
        };
    }

    public void ShowFor(Window owner)
    {
        if (!IsVisible) Show(owner);
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideWindow() { if (!_viewModel.IsInstalling) Hide(); }
    private async void CopyDetail(string detail)
    {
        try
        {
            if (Clipboard != null) { await Clipboard.SetTextAsync(detail); _viewModel.SetNotice("错误详情已复制。"); }
        }
        catch (Exception ex) { App.WriteLog("CopyUpdateDetails", ex); _viewModel.SetNotice("复制失败，请查看本机 error.log。"); }
    }
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() != null)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            BeginMoveDrag(e);
        }
    }
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        _viewModel.RequestHide -= HideWindow; _viewModel.RequestCopy -= CopyDetail;
        Close();
    }
}
