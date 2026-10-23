namespace PortKiller.Updates;

internal sealed class TrustedRelease
{
    public int Schema { get; set; } = 1;
    public long Sequence { get; set; }
    public string Version { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
}
