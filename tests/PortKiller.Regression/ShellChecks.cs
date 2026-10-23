using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PortKiller.Shell;
using PortKiller.ViewModels;
using PortKiller.Views;

namespace PortKiller.Regression;

internal static class ShellChecks
{
    internal static void NavigationAndExtensionLifetime()
    {
        var lifetime = new PageLifetime();
        int creations = 0;
        using (var shell = new ShellViewModel(new[]
        {
            new ToolRegistration("ports", "端口", "M0 0h1", LegacyIndex: 0),
            new ToolRegistration("extra-tool", "扩展工具", "M0 0h1", ViewFactory: () =>
            { creations++; return new Border { DataContext = lifetime }; })
        }))
        {
            Program.Assert(creations == 0, "未选择的扩展工具提前构造页面。");
            Program.Assert(!shell.Select("unknown") && shell.Selected.Id == "ports", "未知工具改变了导航。");
            shell.Select("extra-tool");
            var view = shell.ExtensionView;
            Program.Assert(creations == 1 && lifetime.Activations == 1 && view != null,
                "工具注册没有创建并激活独立页面。");
            shell.Select("ports");
            Program.Assert(lifetime.Deactivations == 1 && shell.ExtensionView == null,
                "离开扩展工具没有停用页面。");
            shell.Select("extra-tool");
            Program.Assert(ReferenceEquals(view, shell.ExtensionView) && creations == 1 && lifetime.Activations == 2,
                "返回工具页面时重复构造或未恢复生命周期。");
        }
        Program.Assert(lifetime.Disposals == 1 && lifetime.Deactivations == 2, "退出未清理扩展页面。");

        bool rejected = false;
        try { _ = new ShellViewModel(new[] {
            new ToolRegistration("same", "A", "M0 0h1", LegacyIndex: 0),
            new ToolRegistration("same", "B", "M0 0h1", LegacyIndex: 1) }); }
        catch (ArgumentException) { rejected = true; }
        Program.Assert(rejected, "重复工具 ID 没有被拒绝。");

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Measure(new Size(1440, 900));
            window.Arrange(new Rect(0, 0, 1440, 900));
            vm.SwitchToolCommand.Execute("settings");
            Program.Assert(vm.CurrentView == 2 && vm.Shell.Selected.Id == "settings" &&
                window.GetLogicalDescendants().OfType<PortKiller.Features.Settings.SettingsView>().Single().IsVisible,
                "注册导航没有切换到独立设置页面。");
            vm.CurrentView = 1;
            Program.Assert(vm.Shell.Selected.Id == "kernel-diagnostics", "既有导航入口未同步注册表。");
        }
        finally { window.PrepareClose(); window.Close(); }
        Console.WriteLine("PASS 注册导航、独立页面及扩展工具生命周期");
    }

    private sealed class PageLifetime : IToolPageLifecycle, IDisposable
    {
        internal int Activations, Deactivations, Disposals;
        public void Activate() => Activations++;
        public void Deactivate() => Deactivations++;
        public void Dispose() => Disposals++;
    }
}
