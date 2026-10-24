using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.Security;

/// <summary>Fences live workers even when a database lease expires during a long pause.</summary>
public static class PublishExecutionLease
{
    public static FileStream? TryAcquire(AdminOptions options)
    {
        AdminRuntimeLease.EnsureDataDirectory(options.DataRoot);
        string path = Path.Combine(options.DataRoot, "publisher-execution.lock");
        AdminRuntimeLease.RequireLocalPath(path);
        FileStream handle;
        try { handle = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            bool blocked = false;
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { blocked = true; }
            if (!blocked) throw new AdminFailure("locking_unavailable", 503, "发布存储环境不能可靠协调执行锁。");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
}
