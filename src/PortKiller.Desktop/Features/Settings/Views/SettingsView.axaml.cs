using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortKiller.Features.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private FontLicensesWindow? _fontLicenses;
    private void OnShowFontLicenses(object? sender, RoutedEventArgs e)
    {
        if (_fontLicenses != null)
        {
            if (_fontLicenses.WindowState == WindowState.Minimized) _fontLicenses.WindowState = WindowState.Normal;
            _fontLicenses.Activate(); return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            _fontLicenses = new FontLicensesWindow();
            _fontLicenses.Closed += (_, _) => _fontLicenses = null;
            _fontLicenses.Show(owner);
        }
        catch (Exception ex) { App.WriteLog("FontLicenses", ex); }
    }
}
