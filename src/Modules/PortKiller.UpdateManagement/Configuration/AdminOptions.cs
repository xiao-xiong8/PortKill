using Microsoft.Extensions.Configuration;

namespace PortKiller.UpdateManagement.Configuration;

public sealed class UpdateToolOptions
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string VerificationConfigPath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 45;
    public int MaximumOutputBytes { get; set; } = 65_536;

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath)
            || string.IsNullOrWhiteSpace(VerificationConfigPath) || !Path.IsPathFullyQualified(VerificationConfigPath)
            || TimeoutSeconds is < 1 or > 55 || MaximumOutputBytes is < 1024 or > 1_048_576)
            throw new InvalidOperationException("Publisher UpdateTool configuration is invalid.");
        ExecutablePath = Path.GetFullPath(ExecutablePath);
        VerificationConfigPath = Path.GetFullPath(VerificationConfigPath);
    }
}

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
    public int AuditRetentionDays { get; set; } = 90;
    public int AuditMaximumRows { get; set; } = 100_000;
    public int MaxUploadPackageBytes { get; set; } = 268_435_456;
    public int MaxActiveUploadsPerUser { get; set; } = 2;
    public int MaxActiveUploadsTotal { get; set; } = 100;
    public long MaxReservedUploadBytesPerUser { get; set; } = 629_145_600;
    public long MaxReservedUploadBytesTotal { get; set; } = 2_147_483_648;
    public int UploadExpirationHours { get; set; } = 24;
    public string StagingPath => Path.Combine(DataRoot, "staging");
    public string ArchivePath => Path.Combine(DataRoot, "archive");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataRoot) || !Path.IsPathFullyQualified(DataRoot))
            throw new InvalidOperationException("Administration:DataRoot must be an absolute path.");
        DataRoot = Path.GetFullPath(DataRoot);
        if (Path.GetPathRoot(DataRoot) == DataRoot || IdleMinutes is < 1 or > 120
            || AbsoluteHours is < 1 or > 24 || PendingMinutes is < 1 or > 15
            || StrongAuthenticationMinutes is < 1 or > 10 || AuditRetentionDays is < 1 or > 3650
            || AuditMaximumRows is < 100 or > 10_000_000 || MaxUploadPackageBytes is < 1 or > 268_435_456
            || MaxActiveUploadsPerUser is < 1 or > 20 || MaxActiveUploadsTotal is < 1 or > 1000
            || MaxReservedUploadBytesPerUser < MaxUploadPackageBytes || MaxReservedUploadBytesPerUser > 10_737_418_240
            || MaxReservedUploadBytesTotal < MaxUploadPackageBytes || MaxReservedUploadBytesTotal > 107_374_182_400
            || UploadExpirationHours is < 1 or > 168)
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
