using Microsoft.Extensions.Hosting;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Commands;

public static class AdminCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 2 || (args.Length - 2) % 2 != 0) return Usage();
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 2; index < args.Length; index += 2)
                if (args[index] is not ("--user" or "--data-root") || !flags.TryAdd(args[index], args[index + 1])) return Usage();
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            var options = AdminHostConfiguration.Load(builder.Configuration, []);
            if (flags.TryGetValue("--data-root", out string? root)) options.DataRoot = root;
            options.Validate();
            builder.Services.AddSingleton(options).AddAdminIdentity(options);
            using var host = builder.Build();
            using var scope = host.Services.CreateScope();
            if (args[0] == "db" && args[1] == "migrate" && !flags.ContainsKey("--user"))
            {
                await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
                Console.WriteLine("Database migration completed."); return 0;
            }
            if (args[0] != "admin" || args[1] != "init" || !flags.TryGetValue("--user", out string? name)) return Usage();
            Console.Error.Write("Password: "); string password = await ReadPasswordAsync();
            Console.Error.Write("Confirm password: "); string confirmation = await ReadPasswordAsync();
            if (password != confirmation) throw new AdminFailure("password_mismatch", 400, "两次输入的密码不一致。");
            string id = await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync(name, password);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { created = true, userId = id }));
            return 0;
        }
        catch (AdminFailure error) { Console.Error.WriteLine(error.Code + ": " + error.Message); return 1; }
        catch (Exception) { Console.Error.WriteLine("admin_operation_failed: 操作失败，请检查数据库迁移和本机配置。"); return 1; }
    }

    private static int Usage()
    { Console.Error.WriteLine("Usage: db migrate [--data-root directory] | admin init --user name [--data-root directory]. Password is read from terminal/stdin."); return 2; }

    private static async Task<string> ReadPasswordAsync()
    {
        if (Console.IsInputRedirected) return await Console.In.ReadLineAsync() ?? "";
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && buffer.Length < 256) buffer.Append(key.KeyChar);
        }
    }
}
