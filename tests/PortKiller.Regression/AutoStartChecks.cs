using System.Text.Json;
using Microsoft.Win32;
using PortKiller.Services;

namespace PortKiller.Regression;

internal static class AutoStartChecks
{
    internal static void DefaultsAndPreferences()
    {
        Program.Assert(new AppSettings().IsAutoStart, "首次运行没有默认开启开机启动。");
        var missing = JsonSerializer.Deserialize("{}", SettingsJsonContext.Default.AppSettings)!;
        var disabled = JsonSerializer.Deserialize("{\"IsAutoStart\":false}", SettingsJsonContext.Default.AppSettings)!;
        Program.Assert(missing.IsAutoStart && !disabled.IsAutoStart, "缺少设置字段的默认值或显式关闭迁移不正确。");

        string directory = Path.Combine(Path.GetTempPath(), "PortKiller-AutoStart-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "config.json");
        bool systemEnabled = true, denyWrite = false;
        var preference = new AutoStartPreference(() => systemEnabled, value =>
        {
            if (denyWrite) return false;
            systemEnabled = value;
            return true;
        });
        try
        {
            Program.Assert(preference.Change(false, () => SettingsService.SaveToPath(
                new AppSettings { IsAutoStart = preference.Requested }, path)), "关闭开机启动未保存。");
            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings)!;
            Program.Assert(!systemEnabled && !preference.Requested && !saved.IsAutoStart,
                "关闭操作未同时更新系统、用户偏好和配置文件。");
            preference.Load(saved.IsAutoStart);
            Program.Assert(!preference.Requested, "重新加载后覆盖了用户的关闭选择。");
            Program.Assert(preference.Change(true, () => SettingsService.SaveToPath(
                new AppSettings { IsAutoStart = preference.Requested }, path)) && systemEnabled,
                "重新开启失败。");

            denyWrite = true;
            bool persisted = false;
            Program.Assert(!preference.Change(false, () => { persisted = true; return true; })
                && systemEnabled && preference.Requested && !persisted,
                "注册表拒绝写入时仍保存了虚假的关闭状态。");
            denyWrite = false;
            Program.Assert(!preference.Change(false, () => false) && systemEnabled && preference.Requested,
                "配置保存失败未回退系统启动项和用户偏好。");

            // 启动项缺失时保留用户意愿，其他设置保存不能将暂时失败变成永久关闭。
            systemEnabled = false;
            preference.Load(true);
            Program.Assert(preference.Requested && !preference.Enabled, "无法区分用户偏好与系统实际状态。");
            Program.Assert(!preference.Change(true, () => false) && !systemEnabled && preference.Requested,
                "配置保存失败时未恢复之前的系统状态。");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal static void IsolatedRegistry()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("开机启动集成验证要求 Windows。");
        string registryPath = @"Software\PortKiller.Regression\AutoStart\" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "PortKiller-AutoStart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "包含 空格"));
        string executable = Path.Combine(directory, "包含 空格", "PortKiller.exe");
        File.WriteAllText(executable, "owned test fixture; never executed");
        try
        {
            // 此路径位于隔离测试子项，绝非 Windows 的 Run 启动项。
            Program.Assert(AutoStartHelper.SetAutoStart(false, Registry.CurrentUser, registryPath, executable),
                "不存在的启动项无法幂等关闭。");
            using (var absent = Registry.CurrentUser.OpenSubKey(registryPath))
                Program.Assert(absent == null, "关闭操作创建了不必要的注册表子项。");
            Program.Assert(AutoStartHelper.SetAutoStart(true, Registry.CurrentUser, registryPath, executable),
                "首次启用未创建注册表子项。");
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)!)
            {
                Program.Assert(key.GetValue("PortKiller") as string == $"\"{executable}\""
                    && key.GetValueKind("PortKiller") == RegistryValueKind.String,
                    "中文/空格路径未正确引用或启动命令类型错误。");
                key.SetValue("OtherApplication", "preserve");
                key.SetValue("PortKiller", "\"C:\\old-location\\PortKiller.exe\"");
            }
            Program.Assert(!AutoStartHelper.IsAutoStartEnabled(Registry.CurrentUser, registryPath, executable),
                "过期路径被错误报告为当前程序已配置。");
            Program.Assert(AutoStartHelper.SetAutoStart(true, Registry.CurrentUser, registryPath, executable)
                && AutoStartHelper.IsAutoStartEnabled(Registry.CurrentUser, registryPath, executable),
                "移动目录后未修复旧启动路径。");

            foreach (string host in new[] { "dotnet.exe", "PortKiller.Regression.exe", "missing/PortKiller.exe" })
            {
                string candidate = Path.Combine(directory, host);
                if (!host.Contains('/')) File.WriteAllText(candidate, "fixture");
                Program.Assert(!AutoStartHelper.SetAutoStart(true, Registry.CurrentUser, registryPath, candidate),
                    "错误注册了测试/运行时宿主或不存在的文件。");
            }
            Program.Assert(AutoStartHelper.SetAutoStart(false, Registry.CurrentUser, registryPath, executable)
                && !AutoStartHelper.IsAutoStartEnabled(Registry.CurrentUser, registryPath, executable),
                "关闭开机启动后注册表值仍存在。");
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath)!)
                Program.Assert(key.GetValue("OtherApplication") as string == "preserve", "关闭操作删除了其他应用的设置。");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            Directory.Delete(directory, true);
        }
    }
}
