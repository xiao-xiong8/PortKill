using System.Collections.Specialized;
using PortKiller.Models;
using PortKiller.Native;
using PortKiller.Services;
using PortKiller.ViewModels;

namespace PortKiller.Regression;

internal static class RefreshChecks
{
    internal static void BulkSelection_PublishesOneSummaryAndStaysAccurate()
    {
        var vm = new MainViewModel();
        try
        {
            Publish(vm, Enumerable.Range(10000, 2000).Select(p => Row(p)).ToList());
            int countChanges = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedCount)) countChanges++; };
            vm.ToggleCheckAll();
            Program.Assert(countChanges == 1 && vm.SelectedCount == 2000 && vm.HeaderCheckState == true &&
                vm.FilteredItems.All(x => x.IsChecked), "全选重复发布统计或行勾选不准确。");
            countChanges = 0;
            vm.ClearSelection();
            Program.Assert(countChanges == 1 && vm.SelectedCount == 0 && vm.HeaderCheckState == false &&
                vm.FilteredItems.All(x => !x.IsChecked), "清空选择重复发布统计或行勾选不准确。");
            vm.ToggleCheck(vm.FilteredItems[0]);
            Program.Assert(vm.SelectedCount == 1 && vm.HeaderCheckState is null, "单选统计不准确。");
            vm.FilteredItems[1].IsChecked = true;
            Program.Assert(vm.SelectedCount == 2, "批量更新后未恢复行勾选监听。");
            Publish(vm, new());
            Program.Assert(vm.SelectedCount == 0 && !vm.HasCheckedRows, "空表仍保留旧选择统计。");
        }
        finally { vm.PrepareShutdown(); }
    }

    internal static void EmptySnapshot_ClearsHudDuringInteraction()
    {
        var vm = new MainViewModel();
        try
        {
            Publish(vm, new() { Row(10000, MibTcpState.Listen) });
            vm.IsHudVisible = true;
            vm.SetListInteractionHold(true);
            Publish(vm, new());
            Program.Assert(vm.FilteredItems.Count == 1 && vm.TotalCount == 0 && vm.HudRows.Count == 0,
                "空快照被旧列表回填为仍在监听的 HUD 记录。");
            Publish(vm, new() { Row(11000, MibTcpState.Listen) }, force: true);
            int changes = 0;
            vm.InspPorts.CollectionChanged += (_, _) => changes++;
            vm.SetListInteractionHold(false);
            Program.Assert(vm.FilteredItems.Single().LocalPort == 11000 && changes == 0,
                "强制刷新后交互结束再次合入旧快照。");
        }
        finally { vm.PrepareShutdown(); }
    }

    internal static void ListeningChips_PreferListenerRegardlessOfTableOrder()
    {
        var service = new ProcessDetailService();
        var listening = Row(10000, MibTcpState.Listen);
        var established = Row(10000);
        foreach (var rows in new[] { new[] { established, listening }, new[] { listening, established } })
        {
            var chip = service.GetListeningPorts(listening.Pid, rows).Single();
            Program.Assert(chip.IsListening && chip.StateText == "监听中", "端口同时监听和已连接时徽章依赖表行顺序。");
        }
    }

    internal static PortConnectionItem Row(int port, MibTcpState state = MibTcpState.Established,
        bool protect = false, string process = "refresh-regression.exe") => new()
    {
        LocalAddress = "127.0.0.1", LocalPort = port,
        RemoteAddress = "127.0.0.1", RemotePort = port + 10000,
        State = state == MibTcpState.TimeWait ? "TIME_WAIT" : state == MibTcpState.Listen ? "LISTENING" : "ESTABLISHED",
        RawState = state, Pid = 123456, ProcessName = process, IsSystemProtected = protect
    };

    internal static void Publish(MainViewModel vm, List<PortConnectionItem> rows, bool force = false) =>
        vm.ApplyScanResult(new ScanResult { Items = rows, HasChanged = true, Fingerprint = 1,
            ListeningCount = rows.Count(x => x.IsListening) }, force);

    internal static void StateAndFlags_KeepRowsAndNotify()
    {
        var vm = new MainViewModel();
        try
        {
            Publish(vm, Enumerable.Range(10000, 500).Select(p => Row(p)).ToList());
            var collection = vm.FilteredItems;
            var original = collection.ToArray();
            vm.ToggleCheck(original[0]);
            vm.FocusRow(original[0]);
            var notified = new HashSet<string>();
            original[0].PropertyChanged += (_, e) => notified.Add(e.PropertyName!);
            string previousKey = original[0].RowKey;
            _ = original[0].ServiceCategory;
            int changes = 0;
            collection.CollectionChanged += (_, _) => changes++;
            Publish(vm, Enumerable.Range(10000, 500).Select(p => Row(p, MibTcpState.TimeWait)).ToList());
            Program.Assert(ReferenceEquals(collection, vm.FilteredItems) && changes == 0,
                "状态变化触发了集合重置或替换。");
            Program.Assert(original.SequenceEqual(vm.FilteredItems) && vm.FilteredItems.All(x => x.IsTimeWait),
                "未复用连接对象或状态过期。");
            Program.Assert(ReferenceEquals(vm.FocusedItem, original[0]) && original[0].IsChecked,
                "状态更新丢失焦点或勾选。");
            Program.Assert(previousKey != original[0].RowKey &&
                new[] { "State", "StatusBadge", "ProcessDisplay", "ShowReleaseAction", "IsPillTimeWait" }.All(notified.Contains),
                "状态派生绑定未通知或行键缓存过期。");
            Publish(vm, Enumerable.Range(10000, 500).Select(p => Row(p, protect: true)).ToList());
            Program.Assert(original[0].IsKernelHeld && original[0].ShowDiagnoseAction &&
                original[0].ServiceCategory == "内核服务", "保护标志或分类缓存未更新。");
            vm.SelectedCategory = "内核服务";
            Publish(vm, Enumerable.Range(10000, 500).Select(p => Row(p)).ToList());
            Program.Assert(vm.FilteredCount == 0 && vm.FocusedItem is null,
                "保护标志改变后筛选仍保留旧行。");
        }
        finally { vm.PrepareShutdown(); }
    }

    internal static void ChurnDuplicatesAndOrdering_AreAccurate()
    {
        var vm = new MainViewModel();
        try
        {
            Publish(vm, Enumerable.Range(10000, 500).Select(p => Row(p)).ToList());
            var collection = vm.FilteredItems;
            int resets = 0;
            collection.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            // 同时覆盖范围替换、扩容、缩容和空表。
            foreach (int count in new[] { 750, 60, 1000, 0, 500 })
            {
                var rows = Enumerable.Range(12000, count).Select(p => Row(p)).Reverse().ToList();
                if (rows.Count > 0) rows.Add(Row(12000));
                Publish(vm, rows);
                Program.Assert(ReferenceEquals(collection, vm.FilteredItems) && resets == 0,
                    "大批变化替换了 ItemsSource 或发出 Reset。");
                Program.Assert(vm.FilteredItems.Select(x => x.RowKey).SequenceEqual(
                    rows.OrderBy(x => x.LocalPort).ThenBy(x => x.Pid).Select(x => x.RowKey)),
                    "新增、删除或重复连接丢失/排序错误。");
                Program.Assert(vm.FilteredItems.Distinct().Count() == rows.Count,
                    "重复身份被合并为同一对象。");
            }
            vm.ToggleSort(1);
            Publish(vm, new() { Row(16000, MibTcpState.TimeWait), Row(17000), Row(18000) });
            var retained = vm.FilteredItems.Single(x => x.LocalPort == 16000);
            Publish(vm, new() { Row(16000), Row(17000, MibTcpState.TimeWait), Row(18000) });
            Program.Assert(vm.FilteredItems.Select(x => x.State).SequenceEqual(
                vm.FilteredItems.Select(x => x.State).OrderBy(x => x)), "复用行状态变化后排序未更新。");
            Program.Assert(vm.FilteredItems.Contains(retained), "排序改变丢失复用行。");
            vm.SelectedState = "TIME_WAIT";
            Program.Assert(vm.FilteredCount == 1 && vm.FilteredItems[0].LocalPort == 17000, "状态筛选结果过期。");
            vm.SelectedState = "全部状态";
            vm.ToggleSort(0);
            vm.ToggleSort(0);
            Program.Assert(vm.FilteredItems.Select(x => x.LocalPort).SequenceEqual(new[] { 18000, 17000, 16000 }),
                "降序排序错误。");
        }
        finally { vm.PrepareShutdown(); }
    }

    internal static void InteractionAndHud_ConvergeToLatestSnapshot()
    {
        var vm = new MainViewModel();
        try
        {
            Publish(vm, new() { Row(10000, MibTcpState.Listen) });
            vm.IsHudVisible = true;
            int hudChanges = 0, chipChanges = 0;
            vm.HudRows.CollectionChanged += (_, _) => hudChanges++;
            vm.InspPorts.CollectionChanged += (_, _) => chipChanges++;
            var unrelated = Row(11000);
            unrelated.Pid++;
            Publish(vm, new() { Row(10000, MibTcpState.Listen), unrelated });
            Program.Assert(hudChanges == 0 && chipChanges == 0,
                "无关连接改变仍重建 HUD 或详情端口。");
            vm.IsHudVisible = false;
            vm.SetListInteractionHold(true);
            for (int port = 12000; port < 12020; port++)
                Publish(vm, new() { Row(port, MibTcpState.Listen) });
            Program.Assert(vm.FilteredItems.Count == 2 && vm.TotalCount == 1 && hudChanges == 0,
                "交互期间结构发生变化、后台计数停止或隐藏 HUD 仍更新。");
            vm.SetListInteractionHold(false);
            Program.Assert(vm.FilteredItems.Count == 1 && vm.FilteredItems[0].LocalPort == 12019,
                "交互结束未合入最新快照。");
            vm.IsHudVisible = true;
            Program.Assert(vm.HudRows.Count == 1 && vm.HudRows[0].Port == "12019", "HUD 恢复显示旧快照。");
        }
        finally { vm.PrepareShutdown(); }
    }
}
