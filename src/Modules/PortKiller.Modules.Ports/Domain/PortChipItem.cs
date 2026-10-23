namespace PortKiller.Models;

public class PortChipItem
{
    public string Port { get; set; } = string.Empty;
    public string StateText { get; set; } = string.Empty;
    public bool IsListening { get; set; }
}
