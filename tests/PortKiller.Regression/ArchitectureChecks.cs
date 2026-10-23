using System.ComponentModel;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.ViewModels;

namespace PortKiller.Regression;

internal static class ArchitectureChecks
{
    internal static void Snapshot_IsIndependentAndRowsAreReused()
    {
        var references = typeof(PortConnection).Assembly.GetReferencedAssemblies();
        Program.Assert(references.All(x => x.Name is not "PortKiller" &&
            !x.Name!.StartsWith("Avalonia") && !x.Name.StartsWith("CommunityToolkit")),
            "端口模块反向依赖桌面或界面框架。");
        Program.Assert(!typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(PortConnection)),
            "系统快照包含界面通知状态。");
        var vm = new MainViewModel();
        try
        {
            PortConnection Make(MibTcpState state, uint rawPort) => new()
            {
                LocalAddress = "127.0.0.1", LocalPort = 18080, RemoteAddress = "127.0.0.1",
                RemotePort = 28080, Pid = 123456, ProcessName = "architecture-check.exe",
                State = NetworkScannerServiceState(state), RawState = state, RawLocalPort = rawPort
            };
            var first = Make(MibTcpState.Established, 100);
            vm.ApplyScanResult(new ScanResult { Items = new[] { first } }, true);
            var row = vm.FilteredItems.Single();
            Program.Assert(!ReferenceEquals(first, row) && row.RawLocalPort == 100,
                "纯快照没有转换为独立显示行或丢失原生操作参数。");
            row.IsChecked = true;
            int changes = 0;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.IsTimeWait)) changes++; };
            var second = Make(MibTcpState.TimeWait, 200);
            vm.ApplyScanResult(new ScanResult { Items = new[] { second } }, false);
            Program.Assert(ReferenceEquals(row, vm.FilteredItems.Single()) && row.IsChecked &&
                row.IsTimeWait && changes == 1 && row.RawLocalPort == 200,
                "纯快照更新未复用显示行、保留选择或同步原生参数及属性通知。");
            Program.Assert(first.RawState == MibTcpState.Established && !ReferenceEquals(second, row),
                "显示行更新污染了采集快照。");
        }
        finally { vm.PrepareShutdown(); }
    }

    private static string NetworkScannerServiceState(MibTcpState state)
        => state == MibTcpState.TimeWait ? "TIME_WAIT" : "ESTABLISHED";
}
