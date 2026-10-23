using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PortKiller.Views;

namespace PortKiller.Features.KernelDiagnostics;

public partial class KernelDiagnosticsView : UserControl
{
    public KernelDiagnosticsView() => InitializeComponent();
    private MainWindow? Host => TopLevel.GetTopLevel(this) as MainWindow;
    private void OnCopyReport(object? sender, RoutedEventArgs e) => Host?.OnCopyReport(sender, e);
    private void OnOpenServices(object? sender, RoutedEventArgs e) => Host?.OnOpenServices(sender, e);
}
