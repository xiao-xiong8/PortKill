using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PortKiller.Features.Settings;

public partial class FontLicensesWindow : Window
{
    public FontLicensesViewModel ViewModel { get; }

    public FontLicensesWindow() : this(new FontLicensesViewModel()) { }

    public FontLicensesWindow(FontLicensesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        this.FindControl<ListBox>("FontDocuments")!.SelectionChanged += OnDocumentChanged;
        DataContext = viewModel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void OnDocumentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("LicenseReader") is { } reader) reader.Offset = default;
    }

    private async void OnCopyLicense(object? sender, RoutedEventArgs e)
    {
        var document = ViewModel.SelectedDocument;
        try
        {
            if (Clipboard == null) { ViewModel.Status = "剪贴板暂不可用，可在阅读区选中文字后复制。"; return; }
            await Clipboard.SetTextAsync(document.Text);
            ViewModel.Status = $"已复制 {document.Family} 的完整授权原文";
        }
        catch (Exception ex) { App.WriteLog("CopyFontLicense", ex); ViewModel.Status = "复制未完成，请稍后重试。"; }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ViewModel.SelectedDocument.SourceUrl) { UseShellExecute = true });
            ViewModel.Status = "已请求浏览器打开官方来源";
        }
        catch (Exception ex) { App.WriteLog("OpenFontSource", ex); ViewModel.Status = "未能打开浏览器，可选择并复制上方来源地址。"; }
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() != null)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
