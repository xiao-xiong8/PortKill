namespace PortKiller.Features.Settings;

public sealed record FontLicenseDocument(
    string Family, string Usage, string Weights, string SourceUrl, string Text)
{
    public string LicenseName => "SIL Open Font License 1.1";
}
