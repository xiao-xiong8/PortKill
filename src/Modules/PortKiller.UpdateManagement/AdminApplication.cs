namespace PortKiller.UpdateManagement;

public static class AdminApplication
{
    public const string Name = "PortKill Update Administration";
    public static string Version => typeof(AdminApplication).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("程序集版本未配置。");
}
