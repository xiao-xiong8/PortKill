using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.Persistence;

public sealed record AdminBackupManifest(int Schema, string ApplicationVersion, long CreatedUtcMs, string DatabaseSha256,
    string[] Migrations, Dictionary<string, string> AuthenticationKeys);
public sealed record AdminRestoreResult(string? PreviousDatabasePath, int RevokedSessions);

public sealed class AdminBackupService(AdminOptions options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string ConnectionString(string path, bool readOnly = false) => new SqliteConnectionStringBuilder
    { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString();
    private static UpdateAdminDbContext Context(string path) => new(new DbContextOptionsBuilder<UpdateAdminDbContext>().UseSqlite(ConnectionString(path)).Options);
    private static void PrivateDirectory(string path)
    {
        AdminRuntimeLease.RequireLocalPath(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, mode); File.SetUnixFileMode(path, mode);
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
    private static async Task CheckDatabaseAsync(string path, CancellationToken token)
    {
        await using var connection = new SqliteConnection(ConnectionString(path, true)); await connection.OpenAsync(token);
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals((string?)await command.ExecuteScalarAsync(token), "ok", StringComparison.Ordinal))
            throw new AdminFailure("backup_invalid", 400, "数据库完整性校验失败。");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token)) throw new AdminFailure("backup_invalid", 400, "数据库关联校验失败。");
    }

    public async Task BackupAsync(string output, CancellationToken token = default)
    {
        using var lease = AdminRuntimeLease.Shared(options);
        AdminRuntimeLease.RequireLocalPath(output); output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new AdminFailure("backup_exists", 409, "备份目标已存在，请使用新的目录。");
        await using (var database = Context(options.DatabasePath)) await AdminDatabaseReadiness.RequireAsync(database, options, token);
        PrivateDirectory(output); PrivateDirectory(Path.Combine(output, "auth-keys"));
        string copy = Path.Combine(output, "admin.sqlite");
        await using (var source = new SqliteConnection(ConnectionString(options.DatabasePath, true)))
        await using (var destination = new SqliteConnection(ConnectionString(copy)))
        {
            await source.OpenAsync(token); await destination.OpenAsync(token);
            token.ThrowIfCancellationRequested(); source.BackupDatabase(destination);
            using var command = destination.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE;"; await command.ExecuteNonQueryAsync(token);
        }
        await CheckDatabaseAsync(copy, token);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(options.DataProtectionPath))
            foreach (string file in Directory.EnumerateFiles(options.DataProtectionPath, "*.xml").Order())
            {
                if (keys.Count >= 4096) throw new AdminFailure("backup_invalid", 400, "认证密钥数量超出限制。");
                byte[] content = await ReadKeyAsync(file, token);
                string name = Path.GetFileName(file); ValidateKeyName(name);
                await File.WriteAllBytesAsync(Path.Combine(output, "auth-keys", name), content, token);
                keys.Add(name, Convert.ToHexString(SHA256.HashData(content)));
            }
        await using var snapshot = Context(copy);
        string[] migrations = (await snapshot.Database.GetAppliedMigrationsAsync(token)).ToArray();
        var manifest = new AdminBackupManifest(1, AdminApplication.Version, clock.GetUtcNow().ToUnixTimeMilliseconds(), await HashAsync(copy, token), migrations, keys);
        string temporary = Path.Combine(output, "backup.json.pending");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json), token);
        // A manifest appears only when the database snapshot and every key have been completed.
        File.Move(temporary, Path.Combine(output, "backup.json"));
    }

    private static void ValidateKeyName(string name)
    {
        bool key = name.Length == 44 && name.StartsWith("key-", StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(4, 36), "D", out _);
        bool revocation = name.Length is > 15 and < 100 && name.StartsWith("revocation-", StringComparison.Ordinal)
            && name.All(character => char.IsAsciiLetterOrDigit(character) || "-._".Contains(character));
        if (!name.EndsWith(".xml", StringComparison.Ordinal) || !key && !revocation)
            throw new AdminFailure("backup_invalid", 400, "认证密钥名称无效。");
    }
    private static async Task<byte[]> ReadKeyAsync(string path, CancellationToken token)
    {
        AdminRuntimeLease.RequireLocalPath(path);
        if (new FileInfo(path).Length is <= 0 or > 1_048_576) throw new AdminFailure("backup_invalid", 400, "认证密钥大小无效。");
        byte[] content = await File.ReadAllBytesAsync(path, token);
        using var input = new MemoryStream(content);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        string expectedRoot = Path.GetFileName(path).StartsWith("revocation-", StringComparison.Ordinal) ? "revocation" : "key";
        if (XDocument.Load(reader).Root?.Name != expectedRoot) throw new AdminFailure("backup_invalid", 400, "认证密钥格式无效。");
        return content;
    }

    public async Task<AdminRestoreResult> RestoreAsync(string input, CancellationToken token = default)
    {
        using var lease = AdminRuntimeLease.Exclusive(options);
        AdminRuntimeLease.RequireLocalPath(input); input = Path.GetFullPath(input);
        if (string.Equals(input, options.DataRoot, StringComparison.OrdinalIgnoreCase)) throw new AdminFailure("backup_invalid", 400, "恢复源不能是当前数据目录。");
        string manifestPath = Path.Combine(input, "backup.json"); AdminRuntimeLease.RequireLocalPath(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1_048_576) throw new AdminFailure("backup_invalid", 400, "备份清单不存在或过大。");
        var manifest = JsonSerializer.Deserialize<AdminBackupManifest>(await File.ReadAllTextAsync(manifestPath, token), Json);
        if (manifest is not { Schema: 1, AuthenticationKeys.Count: <= 4096, Migrations: not null, DatabaseSha256.Length: 64 })
            throw new AdminFailure("backup_invalid", 400, "备份清单版本或内容无效。");
        var keyContent = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in manifest.AuthenticationKeys)
        {
            ValidateKeyName(pair.Key);
            byte[] content = await ReadKeyAsync(Path.Combine(input, "auth-keys", pair.Key), token);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), pair.Value, StringComparison.Ordinal))
                throw new AdminFailure("backup_invalid", 400, "认证密钥摘要不匹配。");
            string destination = Path.Combine(options.DataProtectionPath, pair.Key); AdminRuntimeLease.RequireLocalPath(destination);
            if (File.Exists(destination) && !string.Equals(await HashAsync(destination, token), pair.Value, StringComparison.Ordinal))
                throw new AdminFailure("backup_key_conflict", 409, "现有认证密钥与备份冲突。");
            keyContent.Add(pair.Key, content);
        }
        string source = Path.Combine(input, "admin.sqlite"); AdminRuntimeLease.RequireLocalPath(source);
        string staged = Path.Combine(options.DataRoot, ".restore-" + Guid.NewGuid().ToString("N") + ".sqlite");
        File.Copy(source, staged, overwrite: false);
        // Verify the copied bytes, so a changed source cannot bypass the manifest check.
        if (!string.Equals(await HashAsync(staged, token), manifest.DatabaseSha256, StringComparison.Ordinal))
            throw new AdminFailure("backup_invalid", 400, "数据库摘要不匹配。");
        await CheckDatabaseAsync(staged, token);
        int revoked;
        await using (var database = Context(staged))
        {
            string[] expected = database.Database.GetMigrations().ToArray();
            string[] applied = (await database.Database.GetAppliedMigrationsAsync(token)).ToArray();
            if (!expected.SequenceEqual(applied) || !applied.SequenceEqual(manifest.Migrations))
                throw new AdminFailure("backup_schema_mismatch", 409, "备份数据库与当前程序的迁移版本不匹配。");
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            revoked = await database.AdminSessions.Where(x => x.RevokedUtcMs == null).ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedUtcMs, now), token);
            database.AuditEvents.Add(new AuditEvent { Action = "database.restored", Result = "success", CreatedUtcMs = now, CorrelationId = Guid.NewGuid().ToString("N") });
            await database.SaveChangesAsync(token);
            await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;", token);
        }
        PrivateDirectory(options.DataProtectionPath);
        foreach (var pair in keyContent)
        {
            string destination = Path.Combine(options.DataProtectionPath, pair.Key);
            if (!File.Exists(destination)) await File.WriteAllBytesAsync(destination, pair.Value, token);
        }
        string? previous = null;
        if (File.Exists(options.DatabasePath))
        {
            using (var pooled = new SqliteConnection(AdminDatabase.ConnectionString(options))) SqliteConnection.ClearPool(pooled);
            await using (var current = new SqliteConnection(ConnectionString(options.DatabasePath)))
            {
                await current.OpenAsync(token); using var checkpoint = current.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var result = await checkpoint.ExecuteReaderAsync(token);
                if (!await result.ReadAsync(token) || result.GetInt32(0) != 0)
                    throw new AdminFailure("administration_in_use", 409, "数据库仍有活动写入，不能恢复。");
            }
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                string sidecar = options.DatabasePath + suffix; AdminRuntimeLease.RequireLocalPath(sidecar);
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            previous = options.DatabasePath + ".pre-restore-" + Guid.NewGuid().ToString("N");
            File.Replace(staged, options.DatabasePath, previous);
        }
        else File.Move(staged, options.DatabasePath);
        return new(previous, revoked);
    }
}
