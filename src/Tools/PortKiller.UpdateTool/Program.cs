using System.Text.Json;
using System.Security.Cryptography;
using PortKiller.Updates;
using PortKiller.UpdateTool;

bool jsonErrors = args.LastOrDefault() == "--json-errors";
try
{
    string[] commandArgs = jsonErrors ? args[..^1] : args;
    if (commandArgs.Length < 3 || commandArgs.Length % 2 != 1) throw new ArgumentException("Invalid command arguments.");
    string command = commandArgs[0];
    var flags = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 1; index < commandArgs.Length; index += 2)
    {
        string name = commandArgs[index];
        if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= commandArgs.Length
            || !flags.TryAdd(name, commandArgs[index + 1]))
            throw new ArgumentException("Invalid command arguments.");
    }
    if (!flags.TryGetValue("--config", out string? configPath)) throw new ArgumentException("The verification configuration is required.");
    foreach (string name in flags.Keys)
        if (name is not ("--config" or "--root" or "--manifest" or "--package" or "--expected" or "--manifest-sha256" or "--package-sha256"
            or "--owner-pid" or "--owner-start-ticks" or "--max-runtime-seconds" or "--timeout-seconds"))
            throw new ArgumentException("An unsupported command option was provided.");
    if (command != "public-check" && flags.ContainsKey("--timeout-seconds"))
        throw new ArgumentException("The download timeout is only valid for public-check.");
    using var owner = ToolProcessGuard.FromArguments(flags, command == "public-check" ? 605 : 60);
    Action? ensureOwner = owner is null ? null : owner.Check;
    var options = JsonSerializer.Deserialize(File.ReadAllText(configPath), ProtocolJsonContext.Default.ManifestVerificationOptions)
        ?? throw new InvalidDataException("验证配置无效。");
    options.Validate();
    object? result;
    switch (command)
    {
        case "verify":
            var manifest = UpdateManifest.Verify(ReleaseStore.ReadManifest(flags["--manifest"]), options);
            ReleasePackage.Verify(flags["--package"], manifest);
            result = new { manifest.Version, manifest.Sequence, manifest.ExpiresUtc, manifest.Runtime,
                manifest.Size, manifest.Sha256, ManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(flags["--manifest"]))),
                manifest.ReleaseNotes }; break;
        case "activate":
            flags.TryGetValue("--manifest-sha256", out string? approvedManifest);
            flags.TryGetValue("--package-sha256", out string? approvedPackage);
            if ((approvedManifest == null) != (approvedPackage == null)) throw new ArgumentException("Both approved digests are required.");
            result = new ReleaseStore(flags["--root"], options).Activate(flags["--manifest"], flags["--package"], flags["--expected"],
                approvedManifestSha256: approvedManifest, approvedPackageSha256: approvedPackage, ensureOwner: ensureOwner); break;
        case "inspect": case "recover": result = new ReleaseStore(flags["--root"], options).Inspect(command == "recover", ensureOwner); break;
        case "public-check":
            int seconds = 300;
            if (flags.TryGetValue("--timeout-seconds", out string? timeoutText)
                && (!int.TryParse(timeoutText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 600))
                throw new ArgumentException("The public-check timeout is invalid.");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            {
                flags.TryGetValue("--expected", out string? expected);
                result = await PublicReleaseChecker.CheckAsync(options, expected, deadline.Token);
            }
            break;
        default: throw new ArgumentException("未知命令。");
    }
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex)
{
    if (!jsonErrors)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
        return 1;
    }

    string code = ex switch
    {
        ArgumentException or KeyNotFoundException => "invalid_arguments",
        FileNotFoundException or DirectoryNotFoundException => "file_unavailable",
        UnauthorizedAccessException => "access_denied",
        HttpRequestException => "remote_check_failed",
        OperationCanceledException => "remote_check_timeout",
        InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException or FormatException => "validation_failed",
        InvalidOperationException => "operation_conflict",
        IOException => "io_failure",
        _ => "internal_error"
    };
    string message = code switch
    {
        "invalid_arguments" => "工具参数无效。",
        "file_unavailable" => "验证所需文件不可用。",
        "access_denied" => "工具无权访问验证文件。",
        "remote_check_failed" => "远端更新源验证失败。",
        "remote_check_timeout" => "远端更新源验证超时。",
        "validation_failed" => "签名清单或更新包验证失败。",
        "operation_conflict" => "更新操作与当前状态冲突。",
        "io_failure" => "读取或写入更新文件失败。",
        _ => "更新工具内部错误。"
    };
    Console.Out.WriteLine(JsonSerializer.Serialize(new { schema = 1, code, message }));
    return 1;
}
