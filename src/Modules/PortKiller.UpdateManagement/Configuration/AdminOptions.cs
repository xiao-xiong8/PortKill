using Microsoft.Extensions.Configuration;

namespace PortKiller.UpdateManagement.Configuration;

public sealed class UpdateToolOptions
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string VerificationConfigPath { get; set; } = "";
    public string ReleaseRoot { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 45;
    public int PublicCheckTimeoutSeconds { get; set; } = 300;
    public int MaximumOutputBytes { get; set; } = 65_536;

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath)
            || string.IsNullOrWhiteSpace(VerificationConfigPath) || !Path.IsPathFullyQualified(VerificationConfigPath)
            || TimeoutSeconds is < 1 or > 55 || PublicCheckTimeoutSeconds is < 1 or > 600
            || MaximumOutputBytes is < 1024 or > 1_048_576)
            throw new InvalidOperationException("Publisher UpdateTool configuration is invalid.");
        ExecutablePath = Path.GetFullPath(ExecutablePath);
        VerificationConfigPath = Path.GetFullPath(VerificationConfigPath);
    }

    public void ValidatePublishing()
    {
        if (!Enabled) throw new InvalidOperationException("Publisher UpdateTool is disabled.");
        Validate();
        ValidateSource();
    }

    public void ValidateSource()
    {
        if (string.IsNullOrWhiteSpace(ReleaseRoot) || !Path.IsPathFullyQualified(ReleaseRoot)
            || string.IsNullOrWhiteSpace(VerificationConfigPath) || !Path.IsPathFullyQualified(VerificationConfigPath)
            || Path.GetPathRoot(Path.GetFullPath(ReleaseRoot)) == Path.GetFullPath(ReleaseRoot))
            throw new InvalidOperationException("Publisher release source must use configured absolute paths.");
        ReleaseRoot = Path.GetFullPath(ReleaseRoot);
    }
}

public sealed class AdminOptions
{
    public string MonitoringSnapshotDirectory { get; set; } = "";
    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKill.UpdateAdmin");
    public string DatabasePath => Path.Combine(DataRoot, "admin.sqlite");
    public string DataProtectionPath => Path.Combine(DataRoot, "auth-keys");
    public string RuntimeLockPath => Path.Combine(DataRoot, "runtime.lock");
    public int PersistentCookieDays { get; set; } = 14;
    public int IdleMinutes { get; set; } = 10_080;
    public int AbsoluteHours { get; set; } = 720;
    public int PendingMinutes { get; set; } = 10;
    public int StrongAuthenticationMinutes { get; set; } = 5;
    public int LoginIpPerMinute { get; set; } = 10;
    public int MfaPerMinute { get; set; } = 10;
    public int ApiPerIpPerMinute { get; set; } = 240;
    public int LoginAccountIpFailureLimit { get; set; } = 5;
    public int LoginAccountIpLockoutMinutes { get; set; } = 15;
    public int AuditRetentionDays { get; set; } = 90;
    public int AuditMaximumRows { get; set; } = 100_000;
    public int JobEventRetentionDays { get; set; } = 30;
    public int JobEventMaximumRowsPerJob { get; set; } = 500;
    public int JobEventMaximumRows { get; set; } = 100_000;
    public int JobStreamMaximumConnections { get; set; } = 100;
    public int JobStreamMaximumConnectionsPerUser { get; set; } = 5;
    public int JobStreamPollMilliseconds { get; set; } = 500;
    public int JobStreamHeartbeatSeconds { get; set; } = 10;
    public int JobStreamWriteTimeoutSeconds { get; set; } = 5;
    public int JobStreamLifetimeSeconds { get; set; } = 600;
    public int MaxUploadPackageBytes { get; set; } = 268_435_456;
    public int MaxActiveUploadsPerUser { get; set; } = 2;
    public int MaxActiveUploadsTotal { get; set; } = 100;
    public long MaxReservedUploadBytesPerUser { get; set; } = 629_145_600;
    public long MaxReservedUploadBytesTotal { get; set; } = 2_147_483_648;
    public int UploadExpirationHours { get; set; } = 24;
    public int RejectedArchiveRetentionDays { get; set; } = 30;
    public int UnpublishedArchiveRetentionDays { get; set; } = 7;
    public string StagingPath => Path.Combine(DataRoot, "staging");
    public string ArchivePath => Path.Combine(DataRoot, "archive");

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(MonitoringSnapshotDirectory) && !Path.IsPathFullyQualified(MonitoringSnapshotDirectory))
            throw new InvalidOperationException("Administration:MonitoringSnapshotDirectory must be an absolute path.");
        if (string.IsNullOrWhiteSpace(DataRoot) || !Path.IsPathFullyQualified(DataRoot))
            throw new InvalidOperationException("Administration:DataRoot must be an absolute path.");
        DataRoot = Path.GetFullPath(DataRoot);
        if (Path.GetPathRoot(DataRoot) == DataRoot || PersistentCookieDays is < 1 or > 30
            || IdleMinutes is < 1 or > 43_200
            || AbsoluteHours is < 1 or > 720 || IdleMinutes > AbsoluteHours * 60
            || PendingMinutes is < 1 or > 15
            || StrongAuthenticationMinutes is < 1 or > 10 || AuditRetentionDays is < 1 or > 3650
            || LoginIpPerMinute is < 1 or > 120 || MfaPerMinute is < 1 or > 120
            || ApiPerIpPerMinute is < 1 or > 10_000
            || LoginAccountIpFailureLimit is < 1 or > 20
            || LoginAccountIpLockoutMinutes is < 1 or > 1440
            || AuditMaximumRows is < 100 or > 10_000_000 || MaxUploadPackageBytes is < 1 or > 268_435_456
            || MaxActiveUploadsPerUser is < 1 or > 20 || MaxActiveUploadsTotal is < 1 or > 1000
            || MaxReservedUploadBytesPerUser < MaxUploadPackageBytes || MaxReservedUploadBytesPerUser > 10_737_418_240
            || MaxReservedUploadBytesTotal < MaxUploadPackageBytes || MaxReservedUploadBytesTotal > 107_374_182_400
            || UploadExpirationHours is < 1 or > 168 || RejectedArchiveRetentionDays is < 1 or > 3650
            || UnpublishedArchiveRetentionDays is < 1 or > 3650)
            throw new InvalidOperationException("Administration storage or session limits are invalid.");
        if (JobEventRetentionDays is < 1 or > 3650 || JobEventMaximumRowsPerJob is < 1 or > 10_000
            || JobEventMaximumRows < JobEventMaximumRowsPerJob || JobEventMaximumRows > 1_000_000
            || JobStreamMaximumConnections is < 1 or > 1000
            || JobStreamMaximumConnectionsPerUser is < 1 or > 20
            || JobStreamMaximumConnectionsPerUser > JobStreamMaximumConnections
            || JobStreamPollMilliseconds is < 100 or > 1000 || JobStreamHeartbeatSeconds is < 1 or > 30
            || JobStreamWriteTimeoutSeconds is < 1 or > 15 || JobStreamLifetimeSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Administration job event limits are invalid.");
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
