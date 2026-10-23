using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PortKiller.Features.Settings;

public partial class FontLicensesViewModel : ObservableObject
{
    public const string DefaultStatus = "内置授权文档 · 可离线阅读";
    public IReadOnlyList<FontLicenseDocument> Documents { get; }

    [ObservableProperty] private FontLicenseDocument _selectedDocument;
    [ObservableProperty] private string _status = DefaultStatus;

    public FontLicensesViewModel()
    {
        Documents = FontLicenseCatalog.Load();
        _selectedDocument = Documents[0];
    }

    partial void OnSelectedDocumentChanged(FontLicenseDocument value) => Status = DefaultStatus;
}
