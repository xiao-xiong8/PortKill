using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PortKiller.Views;

namespace PortKiller.Features.Ports;

public partial class PortsView : UserControl
{
    public PortsView() => InitializeComponent();
    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;
    private void OnClearAllFilters(object? sender, RoutedEventArgs e) => Host?.OnClearAllFilters(sender, e);
    private void OnClearSearch(object? sender, RoutedEventArgs e) => Host?.OnClearSearch(sender, e);
    private void OnColHandlePressed(object? sender, PointerPressedEventArgs e) => Host?.OnColHandlePressed(sender, e);
    private void OnColumnSettingsClick(object? sender, RoutedEventArgs e) => Host?.OnColumnSettingsClick(sender, e);
    private void OnDiagnoseRowClick(object? sender, RoutedEventArgs e) => Host?.OnDiagnoseRowClick(sender, e);
    private void OnDismissToast(object? sender, RoutedEventArgs e) => Host?.OnDismissToast(sender, e);
    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => Host?.OnHeaderPressed(sender, e);
    private void OnHeaderSortPressed(object? sender, PointerPressedEventArgs e) => Host?.OnHeaderSortPressed(sender, e);
    private void OnKillRowClick(object? sender, RoutedEventArgs e) => Host?.OnKillRowClick(sender, e);
    private void OnReleaseRowClick(object? sender, RoutedEventArgs e) => Host?.OnReleaseRowClick(sender, e);
    private void OnRetryScan(object? sender, RoutedEventArgs e) => Host?.OnRetryScan(sender, e);
    private void OnRowPressed(object? sender, PointerPressedEventArgs e) => Host?.OnRowPressed(sender, e);
    private void OnStatCardPressed(object? sender, PointerPressedEventArgs e) => Host?.OnStatCardPressed(sender, e);
}
