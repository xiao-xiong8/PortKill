using Microsoft.Extensions.Configuration;

namespace PortKiller.UpdateManagement.Configuration;

public sealed class AdminOptions
{
    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKill.UpdateAdmin");
    public string DatabasePath => Path.Combine(DataRoot, "admin.sqlite");
    public string DataProtectionPath => Path.Combine(DataRoot, "auth-keys");
    public string RuntimeLockPath => Path.Combine(DataRoot, "runtime.lock");
    public int IdleMinutes { get; set; } = 30;
    public int AbsoluteHours { get; set; } = 8;
    public int PendingMinutes { get; set; } = 10;
    public int StrongAuthenticationMinutes { get; set; } = 5;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataRoot) || !Path.IsPathFullyQualified(DataRoot))
            throw new InvalidOperationException("Administration:DataRoot must be an absolute path.");
        DataRoot = Path.GetFullPath(DataRoot);
        if (Path.GetPathRoot(DataRoot) == DataRoot || IdleMinutes is < 1 or > 120
            || AbsoluteHours is < 1 or > 24 || PendingMinutes is < 1 or > 15
            || StrongAuthenticationMinutes is < 1 or > 10)
            throw new InvalidOperationException("Administration storage or session limits are invalid.");
    }
}

public static class AdminHostConfiguration
{
    public static AdminOptions Load(ConfigurationManager configuration, string[] args)
    {
        string? path = Environment.GetEnvironmentVariable("PORTKILL_ADMIN_CONFIG");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("PORTKILL_ADMIN_CONFIG must be absolute.");
            configuration.AddJsonFile(path, optional: false, reloadOnChange: false)
                .AddEnvironmentVariables().AddCommandLine(args);
        }
        var options = new AdminOptions();
        configuration.GetSection("Administration").Bind(options);
        options.Validate();
        return options;
    }
}
