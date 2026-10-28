using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using PortKiller.Views;

namespace PortKiller.Features.Ports;

public partial class PortsView : UserControl
{
    public PortsView() => InitializeComponent();
    private bool _compactToolbar;

    private void OnToolbarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid toolbar || e.NewSize.Width <= 0) return;
        bool compact = e.NewSize.Width < 920;
        if (compact == _compactToolbar) return;
        _compactToolbar = compact;
        toolbar.ColumnDefinitions = new ColumnDefinitions(compact ? "Auto,Auto,*,Auto,Auto,Auto" : "Auto,Auto,Auto,*,Auto,Auto,Auto");
        toolbar.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto");
        // 搜索独占第一行，筛选与操作按钮进入第二行，避免右侧按钮被挤出窗口。
        SearchField.Width = compact ? double.NaN : 330;
        Grid.SetColumnSpan(SearchField, compact ? 6 : 1);
        Grid.SetRow(CategoryFilter, compact ? 1 : 0);
        Grid.SetColumn(CategoryFilter, compact ? 0 : 1);
        Grid.SetRow(StateFilter, compact ? 1 : 0);
        Grid.SetColumn(StateFilter, compact ? 1 : 2);
        Grid.SetRow(ToolbarRefreshButton, compact ? 1 : 0);
        Grid.SetColumn(ToolbarRefreshButton, compact ? 3 : 4);
        Grid.SetRow(ToolbarColumnsButton, compact ? 1 : 0);
        Grid.SetColumn(ToolbarColumnsButton, compact ? 4 : 5);
        Grid.SetRow(ToolbarReleaseButton, compact ? 1 : 0);
        Grid.SetColumn(ToolbarReleaseButton, compact ? 5 : 6);
    }

    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;
    private void OnPortListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer body && this.FindControl<ScrollViewer>("PortHeaderScroller") is { } header)
        {
            if (this.FindControl<Panel>("PortHeaderContent") is { } content && body.Extent.Width > 0)
                content.Width = body.Extent.Width;
            if (header.Offset.X != body.Offset.X) header.Offset = new Vector(body.Offset.X, 0);
        }
    }
    private void OnPortHeaderScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer header && this.FindControl<ScrollViewer>("PortListScroller") is { } body
            && body.Offset.X != header.Offset.X)
            body.Offset = new Vector(header.Offset.X, body.Offset.Y);
    }
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
