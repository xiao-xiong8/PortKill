using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.Persistence;

public sealed class AdminRuntimeLease : IDisposable
{
    private readonly FileStream _handle;
    private AdminRuntimeLease(FileStream handle) => _handle = handle;
    public static AdminRuntimeLease Shared(AdminOptions options) => Acquire(options, false);
    public static AdminRuntimeLease Exclusive(AdminOptions options) => Acquire(options, true);
    private static AdminRuntimeLease Acquire(AdminOptions options, bool exclusive)
    {
        options.Validate(); RequireLocalPath(options.DataRoot);
        EnsureDataDirectory(options.DataRoot);
        RequireLocalPath(options.RuntimeLockPath);
        FileStream handle;
        try { handle = new(options.RuntimeLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
        catch (IOException) { throw new AdminFailure("administration_in_use", 409, "管理服务或维护命令正在运行，请先停止 API 和 Worker 后重试。"); }
        try
        {
            // Fail closed on filesystems/runtimes where sharing locks have been disabled or aren't enforced.
            bool blocked = false;
            try { using var probe = new FileStream(options.RuntimeLockPath, FileMode.Open, FileAccess.ReadWrite, exclusive ? FileShare.ReadWrite : FileShare.None); }
            catch (IOException) { blocked = true; }
            if (!blocked) throw new AdminFailure("locking_unavailable", 409, "存储环境不能可靠协调维护锁，请使用支持文件锁的本地磁盘。");
            return new(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public static void EnsureDataDirectory(string path)
    {
        RequireLocalPath(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(path) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new AdminFailure("private_storage_required", 400, "管理数据目录不能对其他系统用户开放，请先设置专用账号或组权限。");
        }
    }

    public static void RequireLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new Uri(Path.GetFullPath(path)).IsUnc)
            throw new AdminFailure("invalid_storage_path", 400, "必须使用本地磁盘绝对路径。");
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AdminFailure("invalid_storage_path", 400, "存储路径不能经过符号链接或重解析点。");
    }
    public void Dispose() => _handle.Dispose();
}

public static class AdminDatabaseReadiness
{
    public static async Task RequireAsync(UpdateAdminDbContext database, AdminOptions options, CancellationToken token = default)
    {
        if (!File.Exists(options.DatabasePath)) throw new AdminFailure("database_not_initialized", 503, "请先通过维护命令执行数据库迁移。");
        string[] expected = database.Database.GetMigrations().ToArray();
        string[] applied = (await database.Database.GetAppliedMigrationsAsync(token)).ToArray();
        if (!expected.SequenceEqual(applied)) throw new AdminFailure("database_migration_required", 503, "数据库版本不匹配，请停止管理服务后执行迁移。");
    }
}

public sealed class AdminRuntimeService(IServiceScopeFactory scopes, AdminOptions options) : IHostedService, IDisposable
{
    private AdminRuntimeLease? _lease;
    public async Task StartAsync(CancellationToken token)
    {
        _lease = AdminRuntimeLease.Shared(options);
        try
        {
            using var scope = scopes.CreateScope();
            await AdminDatabaseReadiness.RequireAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>(), options, token);
        }
        catch { Dispose(); throw; }
    }
    public Task StopAsync(CancellationToken token)
    {
        using var connection = new SqliteConnection(AdminDatabase.ConnectionString(options)); SqliteConnection.ClearPool(connection);
        Dispose(); return Task.CompletedTask;
    }
    public void Dispose() { _lease?.Dispose(); _lease = null; }
}
