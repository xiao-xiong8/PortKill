using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PortKiller.Views;

public partial class MainWindow
{
    private ContentControl? _activeConfirmationCard;
    private IInputElement? _focusBeforeConfirmation;

    private void InitializeConfirmationDialogs()
    {
        InterceptOverlay.PropertyChanged += OnConfirmationVisibilityChanged;
        FamilyOverlay.PropertyChanged += OnConfirmationVisibilityChanged;
        ExitOverlay.PropertyChanged += OnConfirmationVisibilityChanged;
        Opened += (_, _) => SynchronizeConfirmationFocus();
        AddHandler(KeyDownEvent, OnConfirmationPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, OnConfirmationGotFocus);
    }

    private void OnConfirmationVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) SynchronizeConfirmationFocus();
    }

    private void SynchronizeConfirmationFocus()
    {
        if (_exitConfirmed || Vm == null) return;
        var next = ExitOverlay.IsVisible ? ExitCard
            : InterceptOverlay.IsVisible ? InterceptCard
            : FamilyOverlay.IsVisible ? FamilyCard : null;
        if (ReferenceEquals(next, _activeConfirmationCard)) return;
        if (_activeConfirmationCard == null && next != null)
            _focusBeforeConfirmation = FocusManager?.GetFocusedElement();
        _activeConfirmationCard = next;
        if (next != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_exitConfirmed && ReferenceEquals(next, _activeConfirmationCard) && next.IsEffectivelyVisible)
                    ConfirmationCancelButton()?.Focus();
            }, DispatcherPriority.Input);
        }
        else
        {
            var previous = _focusBeforeConfirmation;
            _focusBeforeConfirmation = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_exitConfirmed && _activeConfirmationCard == null
                    && previous is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control
                    && TopLevel.GetTopLevel(control) == this)
                    control.Focus();
            }, DispatcherPriority.Input);
        }
    }

    private Button? ConfirmationCancelButton() => _activeConfirmationCard == ExitCard ? ExitCancelButton
        : _activeConfirmationCard == InterceptCard ? InterceptCancelButton
        : _activeConfirmationCard == FamilyCard ? FamilyCancelButton : null;

    private void OnConfirmationGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_activeConfirmationCard is { } card && e.Source is Control control
            && !ReferenceEquals(control, card) && !control.GetVisualAncestors().Contains(card))
            ConfirmationCancelButton()?.Focus();
    }

    private void OnConfirmationPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_activeConfirmationCard is not { } card) return;
        if (e.Key == Key.Escape)
        {
            if (card == ExitCard) Vm?.CancelExitCommand.Execute(null);
            else if (card == InterceptCard) Vm?.CancelInterceptCommand.Execute(null);
            else Vm?.CancelFamilyKillCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            var candidates = card.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToArray();
            if (candidates.Length > 0)
            {
                int current = Array.IndexOf(candidates, FocusManager?.GetFocusedElement());
                int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                int index = current < 0 ? (step > 0 ? 0 : candidates.Length - 1)
                    : (current + step + candidates.Length) % candidates.Length;
                candidates[index].Focus(NavigationMethod.Tab);
            }
            e.Handled = true;
        }
    }
}
