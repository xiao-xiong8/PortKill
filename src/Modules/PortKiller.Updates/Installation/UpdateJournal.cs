namespace PortKiller.Updates;

internal sealed class UpdateJournal
{
    public string Version { get; set; } = "";
    public string Token { get; set; } = "";
    public string Phase { get; set; } = "prepared";
    public string PreviousVersion { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string WorkerName { get; set; } = "";
}
