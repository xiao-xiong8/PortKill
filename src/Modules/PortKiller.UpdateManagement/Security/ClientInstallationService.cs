using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.Updates;

namespace PortKiller.UpdateManagement.Security;

public sealed class ClientInstallationService(UpdateAdminDbContext database, TimeProvider clock, AdminAuditService audit)
{
    private static readonly string[] Results = ["started", "installed", "rolled_back", "failed"];

    public async Task<ClientInstallationRegisterResponse> RegisterAsync(ClientInstallationRegisterRequest request, CancellationToken token)
    {
        ValidateInstallationId(request.InstallationId);
        string version = NormalizeVersion(request.Version);
        string machine = NormalizeMachineName(request.MachineName);
        if (request.ProtocolVersion != 1) throw new AdminFailure("unsupported_client_protocol", 400, "客户端协议版本不受支持。");
        string rawToken = request.ClientToken ?? Base64Url(RandomNumberGenerator.GetBytes(32));
        if (rawToken.Length != 43 || rawToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new AdminFailure("invalid_client_token", 400, "客户端令牌格式无效。");
        var existing = await database.ClientInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.InstallationId == request.InstallationId, token);
        if (existing != null)
        {
            // A lost registration response can be retried with the locally protected secret.
            // Knowing an installation ID alone never permits replacing its credential.
            if (request.ClientToken != null && existing.RevokedUtcMs == null && existing.TokenHash == HashToken(rawToken))
                return new(request.InstallationId, rawToken);
            throw new AdminFailure("installation_already_registered", 409, "安装实例已经注册，请使用现有令牌上报。");
        }
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        database.ClientInstallations.Add(new ClientInstallation
        {
            InstallationId = request.InstallationId,
            TokenHash = HashToken(rawToken),
            MachineName = machine,
            ClientVersion = version,
            LastReportResult = "started",
            RegisteredUtcMs = now,
            LastReportedUtcMs = now
        });
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        { throw new AdminFailure("installation_already_registered", 409, "安装实例已经注册，请使用现有令牌上报。"); }
        return new(request.InstallationId, rawToken);
    }

    public async Task ReportAsync(string? authorization, ClientInstallationReportRequest request, CancellationToken token)
    {
        string rawToken = ReadBearerToken(authorization);
        string version = NormalizeVersion(request.Version);
        string machine = NormalizeMachineName(request.MachineName);
        if (!Results.Contains(request.Result, StringComparer.Ordinal))
            throw new AdminFailure("invalid_client_report", 400, "客户端更新结果无效。");
        var installation = await database.ClientInstallations.SingleOrDefaultAsync(x => x.TokenHash == HashToken(rawToken), token);
        if (installation == null || installation.RevokedUtcMs != null)
            throw new AdminFailure("client_token_invalid", 401, "客户端令牌无效或已撤销。");
        installation.ClientVersion = version;
        installation.MachineName = machine;
        installation.LastReportResult = request.Result;
        installation.LastErrorCode = request.Result == "failed" ? "update_start_failed" : null;
        installation.LastReportedUtcMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await database.SaveChangesAsync(token);
    }

    public async Task<PageResponse<ClientInstallationResponse>> ListAsync(ClientInstallationQuery query, CancellationToken token)
    {
        if (query.Page is < 1 or > 10000 || query.PageSize is < 1 or > 100)
            throw new AdminFailure("invalid_pagination", 400, "分页参数超出范围。");
        var source = database.ClientInstallations.AsNoTracking();
        if (query.Revoked is { } revoked) source = source.Where(x => (x.RevokedUtcMs != null) == revoked);
        else if (!query.IncludeRevoked) source = source.Where(x => x.RevokedUtcMs == null);
        if (query.Version != null)
        {
            string version = NormalizeVersion(query.Version);
            source = source.Where(x => x.ClientVersion == version);
        }
        if (query.Result != null)
        {
            if (!Results.Contains(query.Result, StringComparer.Ordinal)) throw new AdminFailure("invalid_client_report", 400, "客户端更新结果无效。");
            source = source.Where(x => x.LastReportResult == query.Result);
        }
        if (!string.IsNullOrWhiteSpace(query.MachineName))
        {
            string machine = NormalizeMachineName(query.MachineName).Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            source = source.Where(x => EF.Functions.Like(x.MachineName, "%" + machine + "%", "\\"));
        }
        if (query.ReportedBeforeUtcMs is { } before) source = source.Where(x => x.LastReportedUtcMs <= before);
        if (query.ReportedAfterUtcMs is { } after) source = source.Where(x => x.LastReportedUtcMs >= after);
        long total = await source.LongCountAsync(token);
        var rows = await source.OrderByDescending(x => x.LastReportedUtcMs).ThenBy(x => x.InstallationId)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(token);
        return new(rows.Select(ToResponse).ToArray(), query.Page, query.PageSize, total);
    }

    public async Task RevokeAsync(ClaimsPrincipal principal, string installationId, CancellationToken token)
    {
        ValidateInstallationId(installationId);
        var installation = await database.ClientInstallations.SingleOrDefaultAsync(x => x.InstallationId == installationId, token)
            ?? throw new AdminFailure("resource_not_found", 404, "客户端安装实例不存在。");
        if (installation.RevokedUtcMs == null)
        {
            installation.RevokedUtcMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            audit.Stage("client_installation.revoked", installationId,
                actorUserId: principal.FindFirstValue(ClaimTypes.NameIdentifier));
            await database.SaveChangesAsync(token);
        }
    }

    private static ClientInstallationResponse ToResponse(ClientInstallation item) => new(
        MaskId(item.InstallationId), item.InstallationId, item.MachineName, item.ClientVersion, item.LastReportResult,
        item.LastErrorCode, item.RegisteredUtcMs, item.LastReportedUtcMs, item.RevokedUtcMs,
        item.Revision, item.RevokedUtcMs != null);

    private static string NormalizeVersion(string value)
    {
        try
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value.Any(c => !char.IsAsciiDigit(c) && c != '.'))
                throw new FormatException();
            return UpdateManifest.ParseVersion(value).ToString(4);
        }
        catch { throw new AdminFailure("invalid_client_version", 400, "客户端版本号无效。"); }
    }

    private static string NormalizeMachineName(string value)
    {
        string machine = value?.Trim() ?? "";
        if (machine.Length is < 1 or > 128 || machine.Any(char.IsControl))
            throw new AdminFailure("invalid_machine_name", 400, "机器名无效。");
        return machine;
    }

    private static void ValidateInstallationId(string value)
    {
        if (value is not { Length: >= 16 and <= 64 } || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new AdminFailure("invalid_installation_id", 400, "安装实例标识无效。");
    }

    private static string ReadBearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new AdminFailure("client_token_invalid", 401, "客户端令牌无效或已撤销。");
        string token = authorization[prefix.Length..].Trim();
        if (token.Length is < 40 or > 100 || token.Any(char.IsWhiteSpace))
            throw new AdminFailure("client_token_invalid", 401, "客户端令牌无效或已撤销。");
        return token;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string MaskId(string value) => value.Length <= 8 ? value : value[..4] + "…" + value[^4..];
}
