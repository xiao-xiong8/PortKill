namespace PortKiller.Updates;

internal sealed class UpdateJournal
{
    public string Version { get; set; } = "";
    public string Token { get; set; } = "";
    public string Phase { get; set; } = "prepared";
}
