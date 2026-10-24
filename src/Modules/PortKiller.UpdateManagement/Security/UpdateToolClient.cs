using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.Security;

public sealed record UpdateToolResult(bool Succeeded, string? ErrorCode, string? Version, long? Sequence, string? ExpiresUtc);

public sealed class UpdateToolClient(UpdateToolOptions options)
{
    public async Task<UpdateToolResult> VerifyAsync(string manifestPath, string packagePath, CancellationToken token)
    {
        options.Validate();
        AdminRuntimeLease.RequireLocalPath(options.ExecutablePath);
        AdminRuntimeLease.RequireLocalPath(options.VerificationConfigPath);
        AdminRuntimeLease.RequireLocalPath(manifestPath);
        AdminRuntimeLease.RequireLocalPath(packagePath);
        if (!File.Exists(options.ExecutablePath)) return Failure("tool_unavailable");
        if (!File.Exists(options.VerificationConfigPath) || !File.Exists(manifestPath) || !File.Exists(packagePath))
            return Failure("file_unavailable");

        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!
        };
        start.Environment.Clear();
        if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = Path.GetTempPath();
        if (OperatingSystem.IsWindows())
        {
            foreach (string name in new[] { "SYSTEMROOT", "WINDIR", "TEMP", "TMP" }) CopyEnvironment(name, start);
        }
        start.ArgumentList.Add("verify");
        AddArgument(start, "--config", options.VerificationConfigPath);
        AddArgument(start, "--manifest", Path.GetFullPath(manifestPath));
        AddArgument(start, "--package", Path.GetFullPath(packagePath));
        start.ArgumentList.Add("--json-errors");

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return Failure("tool_unavailable");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Failure("tool_unavailable");
        }

        Task<CapturedOutput> stdout = CaptureAsync(process.StandardOutput, options.MaximumOutputBytes, token);
        Task<CapturedOutput> stderr = CaptureAsync(process.StandardError, options.MaximumOutputBytes, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            return Failure("tool_timeout");
        }

        CapturedOutput output = await stdout;
        _ = await stderr;
        if (output.Truncated) return Failure("tool_output_limit");
        if (process.ExitCode != 0) return ParseError(output.Text);
        try
        {
            using var document = JsonDocument.Parse(output.Text);
            JsonElement root = document.RootElement;
            string version = GetString(root, "Version", "version");
            long sequence = GetInt64(root, "Sequence", "sequence");
            string expires = GetString(root, "ExpiresUtc", "expiresUtc");
            if (version.Length is < 1 or > 32 || sequence <= 0 || expires.Length is < 1 or > 64)
                return Failure("tool_protocol_error");
            return new(true, null, version, sequence, expires);
        }
        catch (JsonException) { return Failure("tool_protocol_error"); }
        catch (InvalidOperationException) { return Failure("tool_protocol_error"); }
    }

    private static void AddArgument(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static void CopyEnvironment(string name, ProcessStartInfo start)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value)) start.Environment[name] = value;
    }

    private static async Task<CapturedOutput> CaptureAsync(StreamReader reader, int maximumBytes, CancellationToken token)
    {
        var output = new StringBuilder(Math.Min(maximumBytes, 4096));
        char[] buffer = new char[4096];
        int byteCount = 0;
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (truncated) continue;
            int allowed = count;
            while (allowed > 0)
            {
                int bytes = Encoding.UTF8.GetByteCount(buffer.AsSpan(0, allowed));
                if (byteCount + bytes <= maximumBytes) { byteCount += bytes; break; }
                allowed--;
            }
            if (allowed != count) truncated = true;
            if (allowed > 0) output.Append(buffer, 0, allowed);
        }
        return new(output.ToString(), truncated);
    }

    private static UpdateToolResult ParseError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.GetInt32() != 1
                || !root.TryGetProperty("code", out JsonElement codeElement)) return Failure("tool_execution_failed");
            string code = codeElement.GetString() ?? "";
            return code is "invalid_arguments" or "file_unavailable" or "access_denied" or "remote_check_failed"
                or "validation_failed" or "operation_conflict" or "io_failure" or "internal_error"
                ? Failure(code) : Failure("tool_execution_failed");
        }
        catch (JsonException) { return Failure("tool_execution_failed"); }
        catch (InvalidOperationException) { return Failure("tool_execution_failed"); }
    }

    private static string GetString(JsonElement root, string primary, string alternate)
        => root.TryGetProperty(primary, out JsonElement value) || root.TryGetProperty(alternate, out value)
            ? value.GetString() ?? "" : "";

    private static long GetInt64(JsonElement root, string primary, string alternate)
        => root.TryGetProperty(primary, out JsonElement value) || root.TryGetProperty(alternate, out value)
            ? value.GetInt64() : 0;

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static UpdateToolResult Failure(string code) => new(false, code, null, null, null);
    private sealed record CapturedOutput(string Text, bool Truncated);
}
