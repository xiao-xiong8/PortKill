using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PortKiller.Updates;

/// <summary>Per-user installation credentials. Nothing containing a token may be logged.</summary>
public sealed class ClientIdentityStore
{
    private readonly string _path;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly string? _installDirectory;
    private ClientIdentityCollection _identities = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PortKiller.ClientIdentity.v1");

    public ClientIdentityStore(string? path = null, string? installDirectory = null) : this(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortKiller", "client-identity.json"),
        Protect, Unprotect) { _installDirectory = installDirectory == null ? null : Path.GetFullPath(installDirectory); }

    internal ClientIdentityStore(string path, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    { _path = path; _protect = protect; _unprotect = unprotect; }

    private static byte[] Protect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(value, Entropy, DataProtectionScope.CurrentUser);
    }

    internal FileStream AcquireLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal ClientIdentity LoadOrCreate()
    {
        if (!File.Exists(_path)) { _identities = new(); return Create(); }
        var info = new FileInfo(_path);
        if (info.Length is < 1 or > 16384) throw new InvalidDataException("Invalid client identity.");
        var envelope = JsonSerializer.Deserialize(File.ReadAllBytes(_path), ClientReportJsonContext.Default.ClientIdentityEnvelope)
            ?? throw new InvalidDataException("Invalid client identity.");
        byte[] clear = _unprotect(Convert.FromBase64String(envelope.ProtectedIdentity));
        try
        {
            _identities = JsonSerializer.Deserialize(clear, ClientReportJsonContext.Default.ClientIdentityCollection)
                ?? throw new InvalidDataException("Invalid client identity.");
            var identity = _identities.Items.SingleOrDefault(x => x.InstallDirectory == (_installDirectory ?? ""));
            if (identity == null || identity.InstallationScope != GetInstallationScope()) return Create();
            if (!Guid.TryParseExact(identity.InstallationId, "N", out _) || identity.ClientToken.Length != 43)
                throw new InvalidDataException("Invalid client identity.");
            return identity;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal ClientIdentity Create()
    {
        var identity = new ClientIdentity
        {
            InstallationId = Guid.NewGuid().ToString("N"),
            ClientToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            InstallDirectory = _installDirectory ?? "",
            InstallationScope = GetInstallationScope()
        };
        Save(identity);
        return identity;
    }

    internal void Save(ClientIdentity identity)
    {
        _identities.Items.RemoveAll(x => x.InstallDirectory == identity.InstallDirectory);
        _identities.Items.Add(identity);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(_identities, ClientReportJsonContext.Default.ClientIdentityCollection);
        byte[] encrypted;
        try { encrypted = _protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new ClientIdentityEnvelope
        { ProtectedIdentity = Convert.ToBase64String(encrypted) }, ClientReportJsonContext.Default.ClientIdentityEnvelope);
        string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(json); stream.Flush(true); }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string GetInstallationScope()
    {
        if (_installDirectory == null) return "";
        // This non-secret marker survives four-file updates and disappears with uninstall.
        string marker = Path.Combine(_installDirectory, ".portkiller-client-instance");
        if (!File.Exists(marker))
        {
            try
            {
                using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
                stream.Flush(true);
            }
            catch (IOException) when (File.Exists(marker)) { }
            catch (UnauthorizedAccessException)
            {
                // Read-only deployments still report; a freshly recreated directory gets a new scope.
                return "readonly-" + Directory.GetCreationTimeUtc(_installDirectory).Ticks;
            }
        }
        if (new FileInfo(marker).Length != 32) throw new InvalidDataException("Invalid installation marker.");
        string scope = File.ReadAllText(marker);
        if (!Guid.TryParseExact(scope, "N", out _)) throw new InvalidDataException("Invalid installation marker.");
        return scope;
    }
}

internal sealed class ClientIdentity
{
    public string InstallationId { get; set; } = "";
    public string ClientToken { get; set; } = "";
    public bool Registered { get; set; }
    public string InstallDirectory { get; set; } = "";
    public string InstallationScope { get; set; } = "";
}

internal sealed class ClientIdentityCollection
{
    public List<ClientIdentity> Items { get; set; } = new();
}

internal sealed class ClientIdentityEnvelope
{
    public string ProtectedIdentity { get; set; } = "";
}
