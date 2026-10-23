using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PortKiller.Shell;

public sealed partial class ToolEntry : ObservableObject
{
    private readonly ToolRegistration _registration;
    private Control? _view;
    private Geometry? _icon;

    internal ToolEntry(ToolRegistration registration)
    {
        _registration = registration;
    }

    public string Id => _registration.Id;
    public string Title => _registration.Title;
    public Geometry Icon => _icon ??= StreamGeometry.Parse(_registration.IconPath);
    public int? LegacyIndex => _registration.LegacyIndex;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _badge = "";

    internal Control? View => _view ??= _registration.ViewFactory?.Invoke();
    internal void Activate()
    {
        IsActive = true;
        if (!LegacyIndex.HasValue && View?.DataContext is IToolPageLifecycle lifecycle) lifecycle.Activate();
    }
    internal void Deactivate()
    {
        IsActive = false;
        if (_view?.DataContext is IToolPageLifecycle lifecycle) lifecycle.Deactivate();
    }
    internal void DisposeView()
    {
        if (_view?.DataContext is IDisposable lifetime) lifetime.Dispose();
        if (_view is IDisposable disposable) disposable.Dispose();
    }
}

/// <summary>导航及扩展页面生命周期。业务模块不依赖此 UI 类型。</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private ToolEntry _selected;
    private bool _disposed;
    public ShellViewModel(IEnumerable<ToolRegistration> registrations)
    {
        var items = registrations.ToArray();
        if (items.Length == 0 || items.Any(x => string.IsNullOrWhiteSpace(x.Id)
            || x.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            || string.IsNullOrWhiteSpace(x.Title)
            || (!x.LegacyIndex.HasValue && x.ViewFactory == null))
            || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length
            || items.Where(x => x.LegacyIndex.HasValue).Select(x => x.LegacyIndex).Distinct().Count()
                != items.Count(x => x.LegacyIndex.HasValue))
            throw new ArgumentException("工具需要唯一的稳定 ID、标题及页面工厂或兼容映射。", nameof(registrations));
        Tools = Array.AsReadOnly(items.Select(x => new ToolEntry(x)).ToArray());
        _selected = Tools[0];
        _selected.Activate();
    }

    public IReadOnlyList<ToolEntry> Tools { get; }
    public ToolEntry Selected => _selected;
    public bool IsExtensionSelected => !_selected.LegacyIndex.HasValue;
    public Control? ExtensionView => IsExtensionSelected ? _selected.View : null;
    public event Action<ToolEntry>? SelectionChanged;

    public bool Select(string id)
    {
        if (_disposed) return false;
        var next = Tools.FirstOrDefault(x => x.Id == id);
        if (next == null) return false;
        if (ReferenceEquals(next, _selected)) return true;
        _selected.Deactivate();
        _selected = next;
        _selected.Activate();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(IsExtensionSelected));
        OnPropertyChanged(nameof(ExtensionView));
        SelectionChanged?.Invoke(_selected);
        return true;
    }

    public void SelectLegacy(int index)
    {
        var entry = Tools.FirstOrDefault(x => x.LegacyIndex == index);
        if (entry != null) Select(entry.Id);
    }

    public void UpdatePortCount(int count)
    {
        var ports = Tools.FirstOrDefault(x => x.Id == "ports");
        if (ports != null) ports.Badge = count.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selected.Deactivate();
        foreach (var tool in Tools) tool.DisposeView();
    }
}
