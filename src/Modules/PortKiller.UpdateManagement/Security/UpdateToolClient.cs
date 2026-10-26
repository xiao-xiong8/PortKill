using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.Updates;

namespace PortKiller.UpdateManagement.Security;

public sealed record UpdateToolResult(bool Succeeded, string? ErrorCode, string? Version, long? Sequence,
    string? ExpiresUtc, string? Runtime, long? PackageSize, string? PackageSha256, string? ManifestSha256, string[] ReleaseNotes);

public sealed record UpdateToolActivationResult(bool Succeeded, string? ErrorCode, string? Version, long? Sequence,
    string? ExpiresUtc, string? ManifestSha256, string? PackageSha256, string? ReceiptSha256);

public sealed record UpdateToolInspectionResult(bool Succeeded, string? ErrorCode, bool HasCurrent,
    string? Version, long? Sequence, string? ManifestSha256, string? PackageSha256, string? ExpiresUtc,
    string? ReceiptSha256 = null);

public sealed record UpdateToolPublicCheckResult(bool Succeeded, string? ErrorCode, string? Version = null,
    long? Sequence = null, string? ManifestSha256 = null, string? PackageSha256 = null);

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
            string runtime = GetString(root, "Runtime", "runtime");
            long packageSize = GetInt64(root, "Size", "size");
            string packageSha256 = GetString(root, "Sha256", "sha256");
            string manifestSha256 = GetString(root, "ManifestSha256", "manifestSha256");
            string[] releaseNotes = GetStrings(root, "ReleaseNotes", "releaseNotes");
            if (version.Length is < 1 or > 32 || sequence <= 0 || expires.Length is < 1 or > 64
                || runtime != "win-x64" || packageSize <= 0 || packageSha256.Length != 64
                || !packageSha256.All(Uri.IsHexDigit) || manifestSha256.Length != 64 || !manifestSha256.All(Uri.IsHexDigit)
                || releaseNotes.Length > 20
                || releaseNotes.Any(note => string.IsNullOrWhiteSpace(note) || note.Length > 300))
                return Failure("tool_protocol_error");
            return new(true, null, version, sequence, expires, runtime, packageSize, packageSha256, manifestSha256, releaseNotes);
        }
        catch (JsonException) { return Failure("tool_protocol_error"); }
        catch (InvalidOperationException) { return Failure("tool_protocol_error"); }
    }

    public async Task<UpdateToolActivationResult> ActivateAsync(
        string manifestPath, string packagePath, string expectedManifestSha256, CancellationToken token,
        string approvedManifestSha256, string approvedPackageSha256)
    {
        try
        {
            options.ValidatePublishing();
            AdminRuntimeLease.RequireLocalPath(options.ReleaseRoot);
            AdminRuntimeLease.RequireLocalPath(manifestPath);
            AdminRuntimeLease.RequireLocalPath(packagePath);
            if (!File.Exists(manifestPath) || !File.Exists(packagePath)) return ActivationFailure("file_unavailable");
            ToolInvocation invocation = await InvokeAsync("activate", [
                ("--root", options.ReleaseRoot), ("--config", options.VerificationConfigPath),
                ("--manifest", Path.GetFullPath(manifestPath)), ("--package", Path.GetFullPath(packagePath)),
                ("--expected", expectedManifestSha256), ("--manifest-sha256", approvedManifestSha256),
                ("--package-sha256", approvedPackageSha256)], token);
            if (!invocation.Succeeded) return ActivationFailure(invocation.ErrorCode ?? "tool_execution_failed");
            using var document = JsonDocument.Parse(invocation.Output);
            JsonElement root = document.RootElement;
            string version = GetString(root, "Version", "version");
            long sequence = GetInt64(root, "Sequence", "sequence");
            string expires = GetString(root, "ExpiresUtc", "expiresUtc");
            string manifest = GetString(root, "ManifestSha256", "manifestSha256");
            string package = GetString(root, "PackageSha256", "packageSha256");
            if (version.Length is < 1 or > 32 || sequence <= 0 || expires.Length is < 1 or > 64
                || !IsDigest(manifest) || !IsDigest(package)) return ActivationFailure("tool_protocol_error");
            return new(true, null, version, sequence, expires, manifest.ToUpperInvariant(), package.ToUpperInvariant(),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.GetRawText()))));
        }
        catch (JsonException) { return ActivationFailure("tool_protocol_error"); }
        catch (InvalidOperationException) { return ActivationFailure("tool_unavailable"); }
        catch (AdminFailure failure) when (failure.Code == "invalid_storage_path") { return ActivationFailure("file_unavailable"); }
    }

    public Task<UpdateToolInspectionResult> InspectAsync(CancellationToken token)
        => InspectCoreAsync("inspect", token);

    public Task<UpdateToolInspectionResult> RecoverAsync(CancellationToken token)
        => InspectCoreAsync("recover", token);

    public async Task<UpdateToolPublicCheckResult> PublicCheckAsync(string expectedManifestSha256, CancellationToken token)
    {
        try
        {
            options.ValidatePublishing();
            if (!IsDigest(expectedManifestSha256)) return new(false, "tool_protocol_error");
            ToolInvocation invocation = await InvokeAsync("public-check", [
                ("--config", options.VerificationConfigPath), ("--expected", expectedManifestSha256),
                ("--timeout-seconds", options.PublicCheckTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                token, options.PublicCheckTimeoutSeconds + 2);
            if (!invocation.Succeeded) return new(false, invocation.ErrorCode ?? "remote_check_failed");
            using var document = JsonDocument.Parse(invocation.Output);
            JsonElement root = document.RootElement;
            string version = GetString(root, "Version", "version");
            long sequence = GetInt64(root, "Sequence", "sequence");
            string manifest = GetString(root, "ManifestSha256", "manifestSha256");
            string package = GetString(root, "PackageSha256", "packageSha256");
            if (version.Length is < 1 or > 32 || sequence <= 0 || !IsDigest(manifest) || !IsDigest(package)
                || !manifest.Equals(expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "tool_protocol_error");
            return new(true, null, version, sequence, manifest.ToUpperInvariant(), package.ToUpperInvariant());
        }
        catch (JsonException) { return new(false, "tool_protocol_error"); }
        catch (InvalidOperationException) { return new(false, "tool_unavailable"); }
        catch (AdminFailure failure) when (failure.Code == "invalid_storage_path") { return new(false, "file_unavailable"); }
    }

    private async Task<UpdateToolInspectionResult> InspectCoreAsync(string command, CancellationToken token)
    {
        try
        {
            options.ValidatePublishing();
            AdminRuntimeLease.RequireLocalPath(options.ReleaseRoot);
            ToolInvocation invocation = await InvokeAsync(command, [
                ("--root", options.ReleaseRoot), ("--config", options.VerificationConfigPath)], token);
            if (!invocation.Succeeded) return InspectionFailure(invocation.ErrorCode ?? "tool_execution_failed");
            using var document = JsonDocument.Parse(invocation.Output);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
                return new(true, null, false, null, null, null, null, null);
            JsonElement root = document.RootElement;
            string version = GetString(root, "Version", "version");
            long sequence = GetInt64(root, "Sequence", "sequence");
            string manifest = GetString(root, "ManifestSha256", "manifestSha256");
            string package = GetString(root, "PackageSha256", "packageSha256");
            string expires = GetString(root, "ExpiresUtc", "expiresUtc");
            // A trusted schema-1 release can be the baseline for the first schema-2 publication.
            if (version.Length is < 1 or > 32 || sequence < 0 || expires.Length is < 1 or > 64
                || !IsDigest(manifest) || !IsDigest(package)) return InspectionFailure("tool_protocol_error");
            return new(true, null, true, version, sequence, manifest.ToUpperInvariant(), package.ToUpperInvariant(), expires,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.GetRawText()))));
        }
        catch (JsonException) { return InspectionFailure("tool_protocol_error"); }
        catch (InvalidOperationException) { return InspectionFailure("tool_unavailable"); }
        catch (AdminFailure failure) when (failure.Code == "invalid_storage_path") { return InspectionFailure("file_unavailable"); }
    }

    private async Task<ToolInvocation> InvokeAsync(string command, (string Name, string Value)[] arguments, CancellationToken token,
        int? timeoutSeconds = null)
    {
        token.ThrowIfCancellationRequested();
        AdminRuntimeLease.RequireLocalPath(options.ExecutablePath);
        AdminRuntimeLease.RequireLocalPath(options.VerificationConfigPath);
        if (!File.Exists(options.ExecutablePath)) return new(false, "tool_unavailable", "", false);
        if (!File.Exists(options.VerificationConfigPath)) return new(false, "file_unavailable", "", false);
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!
        };
        start.Environment.Clear();
        if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = Path.GetTempPath();
        if (OperatingSystem.IsWindows())
            foreach (string name in new[] { "SYSTEMROOT", "WINDIR", "TEMP", "TMP" }) CopyEnvironment(name, start);
        start.ArgumentList.Add(command);
        foreach ((string name, string value) in arguments) AddArgument(start, name, value);
        using (var owner = Process.GetCurrentProcess())
        {
            AddArgument(start, "--owner-pid", owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddArgument(start, "--owner-start-ticks", PublisherProcessIdentity.ReadStartToken(owner).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddArgument(start, "--max-runtime-seconds", ((timeoutSeconds ?? options.TimeoutSeconds) + 3).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("--json-errors");
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new(false, "tool_unavailable", "", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { return new(false, "tool_unavailable", "", false); }

        // Keep draining bounded buffers until the killed process exits; request cancellation must not strand a pipe.
        using var outputCancellation = new CancellationTokenSource();
        Task<CapturedOutput> stdout = CaptureAsync(process.StandardOutput, options.MaximumOutputBytes, outputCancellation.Token);
        Task<CapturedOutput> stderr = CaptureAsync(process.StandardError, options.MaximumOutputBytes, outputCancellation.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? options.TimeoutSeconds));
        try
        {
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                bool stopped = await StopAsync(process);
                outputCancellation.Cancel();
                await IgnoreCaptureFailureAsync(stdout, stderr);
                token.ThrowIfCancellationRequested();
                return new(false, stopped ? "tool_timeout" : "tool_termination_pending", "", false);
            }
            CapturedOutput[] captured = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (captured.Any(item => item.Truncated)) return new(false, "tool_output_limit", captured[0].Text, true);
            if (process.ExitCode != 0)
            {
                UpdateToolResult error = ParseError(captured[0].Text);
                return new(false, error.ErrorCode ?? "tool_execution_failed", captured[0].Text, false);
            }
            return new(true, null, captured[0].Text, false);
        }
        finally
        {
            if (!process.HasExited) await StopAsync(process);
            outputCancellation.Cancel();
            await IgnoreCaptureFailureAsync(stdout, stderr);
        }
    }

    private static async Task<bool> StopAsync(Process process)
    {
        Kill(process);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); return true; }
        catch (TimeoutException) { return false; }
    }

    private static async Task IgnoreCaptureFailureAsync(params Task<CapturedOutput>[] tasks)
    {
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException) { }
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
            return code is "invalid_arguments" or "file_unavailable" or "access_denied" or "remote_check_failed" or "remote_check_timeout"
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

    private static string[] GetStrings(JsonElement root, string primary, string alternate)
    {
        if (!root.TryGetProperty(primary, out JsonElement value) && !root.TryGetProperty(alternate, out value)) return [];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "\0").ToArray();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static UpdateToolResult Failure(string code) => new(false, code, null, null, null, null, null, null, null, []);
    private static UpdateToolActivationResult ActivationFailure(string code)
        => new(false, code, null, null, null, null, null, null);
    private static UpdateToolInspectionResult InspectionFailure(string code)
        => new(false, code, false, null, null, null, null, null);
    private static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private sealed record ToolInvocation(bool Succeeded, string? ErrorCode, string Output, bool Truncated);
    private sealed record CapturedOutput(string Text, bool Truncated);
}
