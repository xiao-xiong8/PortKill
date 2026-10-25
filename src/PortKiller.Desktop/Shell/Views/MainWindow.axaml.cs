using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PortKiller.Models;
using PortKiller.Services;
using PortKiller.ViewModels;

namespace PortKiller.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ThemeTransitionDuration = TimeSpan.FromMilliseconds(190);
    private DispatcherTimer? _themeTransitionTimer;
    private RenderTargetBitmap? _themeTransitionFrame;
    private int _themeTransitionGeneration;

    public MainWindow()
    {
        InitializeComponent();
        InitializeUpdateSignals();
        InitializeConfirmationDialogs();
        Closing += OnWindowClosing;

        // 交互静默闸口的 View 侧接线：指针移动 / 滚动 / 右键菜单期间，
        // 实时扫描的「列表归并」推迟到交互结束（详见 MainViewModel.SetListInteractionHold）
        PortListScroller.PointerMoved += OnPortListPointerMoved;
        PortListScroller.ScrollChanged += OnPortListScrollChanged;
        PortListScroller.AddHandler(PointerWheelChangedEvent,
            (_, _) => PokeListInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);

        // 拖拽移动 / 缩放窗口期间同样挂起归并：Win32 模态移动循环仍在泵消息，
        // 若归并照常跑，整表布局会挤占拖拽的消息泵，表现为窗口跟随卡顿
        PositionChanged += (_, _) => PokeListInteraction();
        Resized += (_, _) => PokeListInteraction();

        UpdateMaxButtonVisual();
    }

    /// <summary>相邻主题切换时淡出旧画面；跨过一档及减少动态效果时直接切换。</summary>
    private void OnThemeChangeRequested(string previous, string next)
    {
        if (Vm is not { ReduceMotion: false } ||
            !MainViewModel.ShouldAnimateThemeTransition(previous, next, reduceMotion: false))
        {
            CancelThemeTransition();
            return;
        }

        var overlay = this.FindControl<Image>("ThemeTransitionOverlay");
        var frameBorder = this.FindControl<Border>("WindowFrameBorder");
        if (overlay is null || frameBorder is null) return;

        // 如果用户在动画中再次切换，抓取当前主题底图而不是前一张覆盖帧。
        StopThemeTransitionTimer();
        overlay.Transitions = null;
        overlay.Opacity = 1;
        overlay.IsVisible = false;
        RenderTargetBitmap? frame = null;
        try
        {
            double scale = Math.Max(1, RenderScaling);
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(frameBorder.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(frameBorder.Bounds.Height * scale)));
            frame = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            frame.Render(frameBorder);
        }
        catch
        {
            frame?.Dispose();
            CancelThemeTransition();
            return;
        }

        int generation = ++_themeTransitionGeneration;
        ReleaseThemeTransitionFrame();
        _themeTransitionFrame = frame;
        overlay.Source = frame;
        overlay.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = ThemeTransitionDuration,
                Easing = new CubicEaseOut()
            }
        };
        overlay.Opacity = 1;
        overlay.IsVisible = true;

        // 让主题资源先完成一次布局与绘制，旧画面再在其上淡出。
        Dispatcher.UIThread.Post(() => Dispatcher.UIThread.Post(() =>
        {
            if (generation != _themeTransitionGeneration || !overlay.IsVisible) return;
            overlay.Opacity = 0;
            _themeTransitionTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = ThemeTransitionDuration + TimeSpan.FromMilliseconds(35) };
            _themeTransitionTimer.Tick += (_, _) =>
            {
                StopThemeTransitionTimer();
                if (generation == _themeTransitionGeneration) CancelThemeTransition();
            };
            _themeTransitionTimer.Start();
        }, DispatcherPriority.Render), DispatcherPriority.Background);
    }

    private void StopThemeTransitionTimer()
    {
        if (_themeTransitionTimer is null) return;
        _themeTransitionTimer.Stop();
        _themeTransitionTimer = null;
    }

    private void ReleaseThemeTransitionFrame()
    {
        var frame = _themeTransitionFrame;
        _themeTransitionFrame = null;
        if (this.FindControl<Image>("ThemeTransitionOverlay") is { } overlay) overlay.Source = null;
        frame?.Dispose();
    }

    private void CancelThemeTransition()
    {
        _themeTransitionGeneration++;
        StopThemeTransitionTimer();
        var overlay = this.FindControl<Image>("ThemeTransitionOverlay");
        if (overlay is not null)
        {
            overlay.Transitions = null;
            overlay.Opacity = 0;
            overlay.IsVisible = false;
        }
        ReleaseThemeTransitionFrame();
    }

    /// <summary>最大化 / 还原时同步标题栏按钮图标与提示（双击标题栏、拖向屏幕边缘也会触发）。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateMaxButtonVisual();
    }

    /// <summary>最大化显示「还原」双框图标，其余状态显示「最大化」单框。</summary>
    private void UpdateMaxButtonVisual()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaxButtonIcon.Data = StreamGeometry.Parse(maximized
            ? "M7 3h14v14h-3 M3 7h14v14H3z"
            : "M3 3h18v18H3z");
        ToolTip.SetTip(MaxButton, maximized ? "还原" : "最大化");
    }

    // ── 防抖闸口：移动即开闸，静止 700ms 后释放并合入挂起快照 ─────────
    // 旧版是「指针在表格内就永久冻结归并」，静止观察端口变化时界面假死；
    // 现按「移动瞄准时不跳行、静止观察时实时刷新」折中：PointerMoved/滚动
    // 只负责重置计时器，闸口本身仅在开/释边界各调一次，配对不会失衡。
    // 右键菜单打开期间走独立的强锁定（OpenRowMenu），不受本计时器影响。

    private const int ListIdleReleaseMs = 700;
    private DispatcherTimer? _listIdleTimer;
    private bool _listPokeHoldActive;
    private MainViewModel? _interactionVm;
    private long _lastListInteraction;

    private void PokeListInteraction()
    {
        if (_exitConfirmed || Vm is not { } vm) return;
        _lastListInteraction = Environment.TickCount64;
        if (_listPokeHoldActive && !ReferenceEquals(_interactionVm, vm))
        {
            _interactionVm?.SetListInteractionHold(false);
            _listPokeHoldActive = false;
        }
        if (!_listPokeHoldActive)
        {
            _listPokeHoldActive = true;
            _interactionVm = vm;
            vm.SetListInteractionHold(true);
        }

        if (_listIdleTimer == null)
        {
            _listIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(100) };
            _listIdleTimer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - _lastListInteraction < ListIdleReleaseMs) return;
                _listIdleTimer.Stop();
                _listPokeHoldActive = false;
                var owner = _interactionVm;
                _interactionVm = null;
                owner?.SetListInteractionHold(false);
            };
        }
        if (!_listIdleTimer.IsEnabled) _listIdleTimer.Start();
    }

    private void OnPortListPointerMoved(object? sender, PointerEventArgs e) => PokeListInteraction();

    private void OnPortListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 只响应位移（用户滚轮/拖滑块）；归并引起的 Extent 变化不开闸
        if (Math.Abs(e.OffsetDelta.X) < 0.1 && Math.Abs(e.OffsetDelta.Y) < 0.1) return;
        PokeListInteraction();
    }

    private TextBox SearchBox => PortsPage.FindControl<TextBox>("SearchBox")!;
    private Grid HeaderGrid => PortsPage.FindControl<Grid>("HeaderGrid")!;
    private Canvas HeaderOverlay => PortsPage.FindControl<Canvas>("HeaderOverlay")!;
    private Border WidthTip => PortsPage.FindControl<Border>("WidthTip")!;
    private TextBlock WidthTipText => PortsPage.FindControl<TextBlock>("WidthTipText")!;
    private Border InsertLine => PortsPage.FindControl<Border>("InsertLine")!;
    private Border DragChip => PortsPage.FindControl<Border>("DragChip")!;
    private TextBlock DragChipText => PortsPage.FindControl<TextBlock>("DragChipText")!;
    private ScrollViewer PortListScroller => PortsPage.FindControl<ScrollViewer>("PortListScroller")!;

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ══════════════════════════════════════════════════════════════
    //  自绘标题栏：拖拽移动 / 双击最大化 / 最小化 / 最大化 / 关闭
    // ══════════════════════════════════════════════════════════════

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>8 向缩放热区：优先交由系统非客户区处理，未接管时回退到手动缩放。</summary>
    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.Tag is not string tag) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var edge = tag switch
        {
            "N" => WindowEdge.North,
            "S" => WindowEdge.South,
            "W" => WindowEdge.West,
            "E" => WindowEdge.East,
            "NW" => WindowEdge.NorthWest,
            "NE" => WindowEdge.NorthEast,
            "SW" => WindowEdge.SouthWest,
            "SE" => WindowEdge.SouthEast,
            _ => (WindowEdge?)null
        };

        if (edge is null) return;
        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    /// <summary>统计卡片点击：Tag 指定筛选维度，交给 VM 切换（再点一次回到全部）。</summary>
    internal void OnStatCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { Tag: string kind })
            Vm?.FilterByStatCommand.Execute(kind);
    }

    /// <summary>详情面板端口胶囊点击：按「:端口」筛选；再点同一胶囊清除。</summary>
    private void OnPortChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: PortChipItem chip })
            Vm?.FilterByPortChipCommand.Execute(chip);
    }

    /// <summary>详情面板「可执行文件」路径点击复制。</summary>
    private void OnExePathPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Vm?.CopyExePathCommand.Execute(null);
    }

    // ══════════════════════════════════════════════════════════════
    //  全局快捷键：F5 强制刷新 · Ctrl+F 聚焦搜索
    // ══════════════════════════════════════════════════════════════

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // 确认层自行处理 Esc / Tab，其他按键留给层内原生控件，不能落到底层列表。
        if (_activeConfirmationCard != null) return;
        if (e.Key == Key.F5)
        {
            if (Vm != null) _ = Vm.Refresh();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (Vm != null) Vm.CurrentView = 0; // 搜索框只在「端口监控」视图存在
            // 视图切换后搜索框才进入可视树，延后到下一轮布局再聚焦
            Dispatcher.UIThread.Post(() =>
            {
                SearchBox?.Focus();
                SearchBox?.SelectAll();
            }, DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            // 右键菜单里标注的 Delete 终止快捷键（此前只是装饰文案）；
            // 焦点在搜索框等输入控件时不拦截，避免误杀
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
            {
                if (Vm != null) _ = Vm.KillFocusedAsync();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
            {
                if (Vm != null) _ = Vm.CopyFocusedRowAsync();
                e.Handled = true;
            }
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            // 输入控件内不拦截（ComboBox 上下键切换选项、TextBox 移动光标）
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or ComboBox) return;
            if (Vm == null || Vm.CurrentView != 0) return; // 仅端口监控视图
            var index = Vm.MoveFocusIndex(e.Key == Key.Down ? 1 : -1);
            if (index >= 0) ScrollRowIntoView(index);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
            if (Vm?.CurrentView == 0 && Vm.FocusedItem != null)
            {
                Vm.ToggleCheck(Vm.FocusedItem);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            // 逐层关闭：模态对话框 → 列设置面板 → 搜索词 → 详情面板 → 勾选
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            // 搜索框放行：Esc 清空搜索词是该层最高频入口；其余输入框内不抢 Esc（5.2 方案要求）
            if (focused is TextBox && !ReferenceEquals(focused, SearchBox)) return;
            var vm = Vm;
            if (vm == null) return;
            if (vm.IsUpdateConfirmVisible)
            {
                if (!vm.IsInstallingUpdate) vm.CancelInstallUpdateCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.IsExitConfirmVisible) { vm.CancelExitCommand.Execute(null); e.Handled = true; }
            else if (vm.IsInterceptVisible) { vm.CancelInterceptCommand.Execute(null); e.Handled = true; }
            else if (vm.IsFamilyConfirmVisible) { vm.CancelFamilyKillCommand.Execute(null); e.Handled = true; }
            else if (vm.IsColumnMenuOpen) { vm.IsColumnMenuOpen = false; e.Handled = true; }
            else if (!string.IsNullOrEmpty(vm.SearchText)) { vm.SearchText = string.Empty; e.Handled = true; }
            else if (vm.FocusedItem != null) { vm.ClearFocus(); e.Handled = true; }
            else if (vm.HasCheckedRows) { vm.ClearSelectionCommand.Execute(null); e.Handled = true; }
        }
    }

    /// <summary>Border 的 48px 高度已包含分隔线，键盘定位与实际虚拟行高一致。</summary>
    private void ScrollRowIntoView(int index)
    {
        const double rowHeight = ConnectionViewportPanel.RowHeight;
        double top = index * rowHeight;
        double bottom = top + rowHeight;
        double viewTop = PortListScroller.Offset.Y;
        double viewHeight = PortListScroller.Viewport.Height;
        if (top < viewTop)
            PortListScroller.Offset = new Vector(PortListScroller.Offset.X, top);
        else if (bottom > viewTop + viewHeight)
            PortListScroller.Offset = new Vector(PortListScroller.Offset.X, bottom - viewHeight);
    }

    // ══════════════════════════════════════════════════════════════
    //  表格行交互
    // ══════════════════════════════════════════════════════════════

    private static PortConnectionItem? ResolveItem(object? sender)
        => (sender as Control)?.DataContext as PortConnectionItem;

    /// <summary>勾选列宽度（与表头列定义保持一致）。</summary>
    private const double CheckColumnWidth = 44;

    /// <summary>
    /// 行内单击：命中左侧勾选列则切换批量选择，其余位置则把该行设为详情面板焦点。
    /// 统一在行上取坐标判定，避免依赖 15px 小方块自身的命中测试。
    /// </summary>
    internal void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        var item = ResolveItem(sender);
        if (item == null || sender is not Control row) return;

        var point = e.GetCurrentPoint(row);

        // 右键：打开全表共享的单实例上下文菜单（菜单已从行模板移出，
        // 虚拟化滚动不再为每行实例化整套 MenuItem 控件树）
        if (point.Properties.IsRightButtonPressed)
        {
            OpenRowMenu(row, item);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        double x = e.GetPosition(row).X;
        if (x <= CheckColumnWidth)
        {
            Vm?.ToggleCheck(item);
            e.Handled = true;
            return;
        }

        Vm?.FocusRow(item);
    }

    private ContextMenu? _rowMenu;
    private bool _rowMenuHooked;

    private void OpenRowMenu(Control anchor, PortConnectionItem item)
    {
        _rowMenu ??= this.FindResource("RowCtxMenu") as ContextMenu;
        if (_rowMenu == null) return;

        // 菜单存续期间挂归并闸口：防止后台刷新把 anchor 行容器回收
        // （菜单莫名消失）或把焦点行清掉（菜单动作静默无效）。
        // 菜单是共享单例，Closed 只挂一次。
        if (!_rowMenuHooked)
        {
            _rowMenu.Closed += (_, _) => Vm?.SetListInteractionHold(false);
            _rowMenuHooked = true;
        }

        Vm?.FocusRow(item);          // 菜单动作全部作用于焦点行
        _rowMenu.DataContext = item;     // MenuItem 的 DataContext 由此继承（CtxItem 解析依赖它）
        Vm?.SetListInteractionHold(true);
        _rowMenu.Open(anchor);
    }

    /// <summary>表头左侧勾选列：全选 / 取消全选。</summary>
    internal void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control header) return;
        if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        if (e.GetPosition(header).X > CheckColumnWidth) return;

        Vm?.ToggleCheckAllCommand.Execute(null);
        e.Handled = true;
    }

    internal void OnKillRowClick(object? sender, RoutedEventArgs e) => FocusAndRun(sender, static vm => vm.KillFocusedCommand);

    internal void OnReleaseRowClick(object? sender, RoutedEventArgs e) => FocusAndRun(sender, static vm => vm.ReleaseFocusedTcpCommand);

    internal void OnDiagnoseRowClick(object? sender, RoutedEventArgs e) => FocusAndRun(sender, static vm => vm.DiagnoseFocusedCommand);

    private void FocusAndRun(object? sender, Func<MainViewModel, System.Windows.Input.ICommand> pick)
    {
        var item = ResolveItem(sender);
        if (item == null || Vm == null) return;

        Vm.FocusRow(item);
        var command = pick(Vm);
        if (command.CanExecute(null)) command.Execute(null);
    }

    // ══════════════════════════════════════════════════════════════
    //  详情面板动作
    // ══════════════════════════════════════════════════════════════

    private void OnKillTreeClick(object? sender, RoutedEventArgs e)
        => Vm?.KillFocusedTreeCommand.Execute(null);

    private void OnDiagnoseInspectorClick(object? sender, RoutedEventArgs e)
        => Vm?.DiagnoseFocusedCommand.Execute(null);

    private void OnCloseInspector(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.InspectorVisible = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  工具栏
    // ══════════════════════════════════════════════════════════════

    internal void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.SearchText = string.Empty;
    }

    internal void OnClearAllFilters(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.SearchText = string.Empty;
        Vm.SelectedCategory = "全部分类";
        Vm.SelectedState = "全部状态";
        Vm.ShowToast("已清除全部筛选条件", "列表已恢复为全量端口视图");
    }

    internal void OnColumnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.IsColumnMenuOpen = !Vm.IsColumnMenuOpen;
        if (Vm.IsColumnMenuOpen) PositionColumnMenu(sender as Control);
    }

    /// <summary>
    /// 把列设置浮层的右边缘对齐触发按钮的右边缘、顶边贴在按钮下方 8px。
    /// 用运行时坐标而不是固定 margin —— 顶部「权限受限」横幅会随管理员身份出现/消失，
    /// 工具栏纵向位置并不固定。
    /// </summary>
    private void PositionColumnMenu(Control? anchor)
    {
        if (anchor == null || ColumnMenuPanel == null || ColumnMenuOverlay == null) return;
        var origin = anchor.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } p) return;

        double right = Bounds.Width - (p.X + anchor.Bounds.Width);
        ColumnMenuPanel.Margin = new Thickness(0, p.Y + anchor.Bounds.Height + 8, Math.Max(0, right), 0);
    }

    /// <summary>点击浮层外的遮罩：收起列设置面板。</summary>
    private void OnDismissColumnMenu(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsColumnMenuOpen = false;
        e.Handled = true;
    }

    internal void OnDismissToast(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.IsToastVisible = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  内核诊断视图
    // ══════════════════════════════════════════════════════════════

    internal void OnOpenServices(object? sender, RoutedEventArgs e)
        => Vm?.OpenServicesManagerCommand.Execute(null);

    internal async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        await Vm.CopyDiagnosticReportAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  设置页交互已全部下沉到 MainViewModel（原生 MVVM）
    // ══════════════════════════════════════════════════════════════
    //  · 开关          → ToggleButton + AppSwitch 控件主题，IsChecked 双向绑定
    //  · 分段 / 标签    → Button.segChip / Button.tab + Command + CommandParameter
    //  · 恢复默认 / 字体 → RestoreDefaultsCommand / ResetTypographyCommand
    //  原先的 OnToggleXxx / OnPickXxx / OnRestoreDefaults 一并移除，
    //  避免同一份设置存在「绑定」与「手工翻转」两条写入路径。

    /// <summary>错误态卡片上的「重试」。</summary>
    internal async void OnRetryScan(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        await Vm.RetryScanAsync();
    }

    // ══════════════════════════════════════════════════════════════
    //  系统保护拦截弹窗 / 退出二次确认
    // ══════════════════════════════════════════════════════════════

    private void OnConfirmIntercept(object? sender, RoutedEventArgs e)
    {
        if (Vm is { InterceptRiskChecked: true, InterceptHasForceable: true }) _ = Vm.ConfirmInterceptAsync();
        e.Handled = true;
    }

    private void OnCancelIntercept(object? sender, RoutedEventArgs e)
        => Vm?.CancelInterceptCommand.Execute(null);

    // ══════════════════════════════════════════════════════════════
    //  家族终止确认弹窗（nginx 类守护模型多目标）
    // ══════════════════════════════════════════════════════════════

    private void OnCancelFamilyKill(object? sender, RoutedEventArgs e)
        => Vm?.CancelFamilyKillCommand.Execute(null);

    private void OnConfirmFamilyKill(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) _ = Vm.ConfirmFamilyKillAsync();
    }

    private void OnCancelExit(object? sender, RoutedEventArgs e)
        => Vm?.CancelExitCommand.Execute(null);

    private void OnConfirmExit(object? sender, RoutedEventArgs e)
        => Vm?.ConfirmExitCommand.Execute(null);

    // ══════════════════════════════════════════════════════════════
    //  行右键上下文菜单（设计稿「表格交互增强」）
    // ══════════════════════════════════════════════════════════════

    private static PortConnectionItem? CtxItem(object? sender)
        => (sender as Control)?.DataContext as PortConnectionItem;

    private void OnCtxCopyRow(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = CtxItem(sender);
        if (item != null) Vm.FocusRow(item);
        Vm.CopyFocusedRowCommand.Execute(null);
    }

    private void OnCtxCopyPort(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = CtxItem(sender);
        if (item != null) Vm.FocusRow(item);
        Vm.CopyEndpointCommand.Execute(null);
    }

    private void OnCtxKill(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = CtxItem(sender);
        if (item != null) Vm.FocusRow(item);
        Vm.KillFocusedCommand.Execute(null);
    }

    private void OnCtxKillTree(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = CtxItem(sender);
        if (item != null) Vm.FocusRow(item);
        Vm.KillFocusedTreeCommand.Execute(null);
    }

    private void OnCtxReveal(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = CtxItem(sender);
        if (item != null) Vm.FocusRow(item);
        Vm.OpenProcessLocationCommand.Execute(null);
    }

    // ══════════════════════════════════════════════════════════════
    //  列设置面板
    // ══════════════════════════════════════════════════════════════

    private void OnToggleColMenuItem(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null || sender is not Border b || b.Tag is not string tag) return;
        Vm.ToggleColVisibleCommand.Execute(tag);
        e.Handled = true;
    }

    private void OnResetColumns(object? sender, PointerPressedEventArgs e)
    {
        if (Vm != null) Vm.IsColumnMenuOpen = false;
        Vm?.ResetColumnsCommand.Execute(null);
        e.Handled = true;
    }

    // ══════════════════════════════════════════════════════════════
    //  表头：排序点击 / 列宽拖拽 / 列顺序拖拽
    // ══════════════════════════════════════════════════════════════

    private int _sortLogical = -1;
    private bool _reorderStarted;
    private Point _sortPressPoint;

    private int _resizeLogical = -1;
    private double _resizeStartWidth;
    private double _resizeStartX;
    private Border? _resizeHandle;
    private bool _resizeFrameQueued;
    private int _pendingResizeLogical = -1;
    private double _pendingResizeWidth;

    private static int ParseTag(object? tag)
        => int.TryParse(tag as string, out int i) ? i : -1;

    /// <summary>
    /// Resize math is deliberately independent of the moving header grid. The grid is
    /// re-laid out on every pointer move, so its local X coordinate is not a stable drag
    /// origin and can make a rightward drag look like a leftward resize.
    /// </summary>
    internal static double ComputeColumnWidth(double startWidth, double startPointerX,
        double currentPointerX, double minWidth, double maxWidth)
        => Math.Clamp(Math.Round(startWidth + currentPointerX - startPointerX), minWidth, maxWidth);

    /// <summary>表头按下：左键记录起点，水平拖动 > 4px 进入列顺序拖拽，否则松手时触发排序。</summary>
    internal void OnHeaderSortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border cell || Vm == null) return;
        if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;

        _sortLogical = ParseTag(cell.Tag);
        _reorderStarted = false;
        _sortPressPoint = e.GetPosition(cell);

        cell.PointerMoved += OnHeaderCellMoved;
        cell.PointerReleased += OnHeaderCellReleased;
        e.Pointer.Capture(cell);
        e.Handled = true;
    }

    private void OnHeaderCellMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border cell || Vm == null || _sortLogical < 0) return;

        var pos = e.GetPosition(cell);
        if (!_reorderStarted)
        {
            if (Math.Abs(pos.X - _sortPressPoint.X) <= 4) return;
            if (_sortLogical == 6) return; // 操作列不参与顺序调整
            _reorderStarted = true;

            if (PortsPage.FindControl<TextBlock>("DragChipText") is { } chipText)
                chipText.Text = MainViewModel.ColTitles[_sortLogical];
            if (PortsPage.FindControl<Border>("DragChip") is { } chip) chip.IsVisible = true;
            if (PortsPage.FindControl<Border>("InsertLine") is { } line) line.IsVisible = true;
        }

        // chip 跟随光标
        var overlayPos = e.GetPosition(HeaderOverlay);
        if (PortsPage.FindControl<Border>("DragChip") is { } dragChip)
        {
            Canvas.SetLeft(dragChip, overlayPos.X + 14);
            Canvas.SetTop(dragChip, overlayPos.Y - 17);
        }

        // 落位线：吸附到最近的数据列边界
        if (HeaderGrid is { } grid && PortsPage.FindControl<Border>("InsertLine") is { } line2)
        {
            double gx = e.GetPosition(grid).X;
            double bestX = -1;
            foreach (var child in grid.Children.OfType<Border>()
                .Where(c => c.IsEffectivelyVisible && Grid.GetColumn(c) >= 1)
                .OrderBy(c => c.Bounds.X))
            {
                double right = child.Bounds.Right;
                if (Math.Abs(right - bestX) > 0.5 && (bestX < 0 || Math.Abs(gx - right) < Math.Abs(gx - bestX)))
                    bestX = right;
            }
            if (bestX >= 0)
            {
                Canvas.SetLeft(line2, bestX - 1);
                Canvas.SetTop(line2, 3);
            }
        }
    }

    private void OnHeaderCellReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border cell) return;
        cell.PointerMoved -= OnHeaderCellMoved;
        cell.PointerReleased -= OnHeaderCellReleased;

        if (Vm != null)
        {
            if (_reorderStarted && _sortLogical >= 0)
            {
                // 计算落位：以数据列边界判定插入位（先摘除被拖列，再回填位置）
                int insertAt = 0;
                if (HeaderGrid is { } grid)
                {
                    double gx = e.GetPosition(grid).X;
                    var cells = grid.Children.OfType<Border>()
                        .Where(c => c.IsEffectivelyVisible && Grid.GetColumn(c) >= 1)
                        .OrderBy(c => c.Bounds.X).ToList();
                    var otherCells = cells.Where(c => !ReferenceEquals(c, cell)).ToList();
                    int boundaries = 0;
                    foreach (var c in otherCells)
                    {
                        if (gx > c.Bounds.X + c.Bounds.Width / 2) boundaries++;
                    }
                    // insertAt 以“拖动列移除后”的可见列序号计算，隐藏列不参与落位。
                    insertAt = boundaries;
                }
                Vm.ReorderColumn(_sortLogical, insertAt);
            }
            else if (_sortLogical >= 0)
            {
                Vm.ToggleSort(_sortLogical);
            }
        }

        if (PortsPage.FindControl<Border>("DragChip") is { } chip) chip.IsVisible = false;
        if (PortsPage.FindControl<Border>("InsertLine") is { } line) line.IsVisible = false;
        _reorderStarted = false;
        _sortLogical = -1;
        e.Handled = true;
    }

    /// <summary>列宽手柄按下：进入拖拽（设计稿三态：默认静默 → Hover 加粗 → 拖拽品牌色 + 宽度提示 → 到底琥珀锁定）。</summary>
    internal void OnColHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || Vm == null) return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        _resizeLogical = ParseTag(handle.Tag);
        if (_resizeLogical < 0 || _resizeLogical >= MainViewModel.ColTitles.Length) return;

        _resizeHandle = handle;
        handle.Classes.Add("dragging");
        // MainWindow 的原点不会因为列宽重排或水平滚动而移动。
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = Vm.GetColWidth(_resizeLogical);

        handle.PointerMoved += OnHandleMoved;
        handle.PointerReleased += OnHandleReleased;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (Vm == null || _resizeLogical < 0) return;

        double currentX = e.GetPosition(this).X;
        double min = Vm.GetColMin(_resizeLogical);
        double newW = ComputeColumnWidth(_resizeStartWidth, _resizeStartX, currentX, min,
            PortKiller.Services.ColumnSettingsMigrator.MaximumWidth);
        bool locked = newW <= min + 0.5;

        if (_resizeHandle is { } h)
        {
            if (locked) h.Classes.Add("locked");
            else h.Classes.Remove("locked");
        }

        // PointerMoved 可以远快于屏幕刷新。把列宽提交合并到下一帧，避免每个
        // 原始鼠标事件都触发表头、内容网格和 ScrollViewer 的完整布局。
        _pendingResizeLogical = _resizeLogical;
        _pendingResizeWidth = newW;
        if (!_resizeFrameQueued)
        {
            _resizeFrameQueued = true;
            Dispatcher.UIThread.Post(ApplyPendingResize, DispatcherPriority.Render);
        }

        // 实时宽度提示
        if (HeaderOverlay is { } overlay)
        {
            var pos = e.GetPosition(overlay);
            if (PortsPage.FindControl<Border>("WidthTip") is { } tip)
            {
                if (PortsPage.FindControl<TextBlock>("WidthTipText") is { } txt)
                    txt.Text = $"{(int)Math.Max(min, newW)} px";
                tip.IsVisible = true;
                Canvas.SetLeft(tip, pos.X + 10);
                Canvas.SetTop(tip, 7);
            }
        }
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border handle) return;
        ApplyPendingResize();
        handle.PointerMoved -= OnHandleMoved;
        handle.PointerReleased -= OnHandleReleased;
        handle.Classes.Remove("dragging");
        handle.Classes.Remove("locked");

        if (PortsPage.FindControl<Border>("WidthTip") is { } tip) tip.IsVisible = false;

        Vm?.SaveColumnState();
        _resizeLogical = -1;
        _pendingResizeLogical = -1;
        _resizeHandle = null;
        e.Handled = true;
    }

    private void ApplyPendingResize()
    {
        _resizeFrameQueued = false;
        if (_resizeLogical < 0 || _pendingResizeLogical != _resizeLogical || Vm == null) return;
        Vm.SetColumnWidth(_resizeLogical, _pendingResizeWidth);
    }

    // ══════════════════════════════════════════════════════════════
    //  托盘态 · 关闭进托盘 · 退出二次确认 · 桌面悬浮窗 HUD
    // ══════════════════════════════════════════════════════════════

    private TrayIcon? _trayIcon;
    private HudWindow? _hud;
    private bool _exitConfirmed;
    private bool _vmHooked;
    private bool _maximizedBeforeHide;
    private MemoryDiagnosticSession? _memoryDiagnostics;
    private ConnectionViewportPanel? _diagnosticPanel;

    internal MemoryDiagnosticSession StartMemoryDiagnostics(string? path = null, TimeSpan? duration = null)
        => _memoryDiagnostics ??= MemoryDiagnosticSession.Start(() =>
        {
            _diagnosticPanel ??= PortListScroller.GetVisualDescendants().OfType<ConnectionViewportPanel>().FirstOrDefault();
            return (Vm?.CaptureMemoryDiagnosticState() ?? default) with
            {
                WindowVisible = IsVisible,
                Minimized = WindowState == WindowState.Minimized,
                RetainedContainers = _diagnosticPanel?.Children.Count ?? 0
            };
        }, path, duration);

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (MemoryDiagnosticSession.IsRequested) StartMemoryDiagnostics();

        if (Vm == null || _vmHooked) return;
        _vmHooked = true;

        Vm.RequestShutdown += OnRequestShutdown;
        Vm.RequestShowMainWindow += ShowFromTray;
        Vm.RequestShowUpdates += ShowUpdateWindow;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.ThemeChangeRequested += OnThemeChangeRequested;

        InitTrayIcon();
        if (Vm.IsHudVisible) ShowHud();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Vm.StartAsync();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ReduceMotion) && Vm is { ReduceMotion: true })
        {
            CancelThemeTransition();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsHudVisible))
        {
            if (Vm is { IsHudVisible: true }) ShowHud();
            else CloseHud();
        }
        else if (e.PropertyName == nameof(MainViewModel.TotalCount) ||
                 e.PropertyName == nameof(MainViewModel.IsAutoRefresh))
        {
            UpdateTrayTooltip();
        }
    }

    private void ShowFromTray()
    {
        Show();
        // 隐藏进托盘前若是最大化则原样恢复（Hide 前已记录）；仅任务栏最小化态需要打回 Normal，
        // 否则唤出会丢失最大化态，窗口退回最大化前尺寸，视觉上像「最大化但有缝」
        WindowState = _maximizedBeforeHide ? WindowState.Maximized : WindowState.Normal;
        _maximizedBeforeHide = false;
        Activate();
    }

    /// <summary>正常退出和提权重启共用此入口，先删除托盘再结束消息循环。</summary>
    private void OnRequestShutdown()
    {
        PrepareClose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown();
    }

    /// <summary>幂等清理；也由系统结束会话时调用，避免遗留图标和事件订阅。</summary>
    public void PrepareClose()
    {
        if (_exitConfirmed) return;
        _exitConfirmed = true;
        CancelThemeTransition();
        _memoryDiagnostics?.Stop();
        _listIdleTimer?.Stop();
        _listPokeHoldActive = false;
        _interactionVm = null;
        Vm?.PrepareShutdown();
        if (Vm != null)
        {
            Vm.RequestShutdown -= OnRequestShutdown;
            Vm.RequestShowMainWindow -= ShowFromTray;
            Vm.RequestShowUpdates -= ShowUpdateWindow;
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.ThemeChangeRequested -= OnThemeChangeRequested;
        }
        if (_trayIcon is { } tray)
        {
            tray.IsVisible = false;
            tray.Dispose();
            _trayIcon = null;
            if (Application.Current is { } app) TrayIcon.GetIcons(app)?.Clear();
        }
        if (_hud != null)
        {
            // 先保存 HUD 坐标再断开 DataContext —— 置空后 HudWindow 的 OnClosing
            // 保存会因拿不到 ViewModel 而静默失效，导致本次拖拽的位置丢失。
            Vm?.SaveHudPosition(_hud.Position.X, _hud.Position.Y);
            _hud.DataContext = null;
            _hud.Close();
            _hud = null;
        }
        _updateWindow?.CloseForShutdown();
        _updateWindow = null;
    }

    private PortKiller.Features.Updates.UpdateWindow? _updateWindow;
    private void ShowUpdateWindow()
    {
        if (Vm == null || _exitConfirmed) return;
        _updateWindow ??= new PortKiller.Features.Updates.UpdateWindow(Vm.UpdateUi);
        _updateWindow.ShowFor(this);
    }

    /// <summary>窗口规则：关闭主窗口 = 收进托盘，后台监听继续运行；退出必须走托盘菜单的二次确认。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitConfirmed) return;

        // 托盘图标创建失败时收进托盘会让程序彻底无法访问 —— 此时放行关闭直接退出
        if (_trayIcon == null) { PrepareClose(); return; }

        e.Cancel = true;
        _maximizedBeforeHide = WindowState == WindowState.Maximized;
        Hide();
        UpdateTrayTooltip();
    }

    private void InitTrayIcon()
    {
        if (_trayIcon != null || Application.Current is null) return;

        WindowIcon? icon = null;
        try
        {
            // avares 资源路径区分大小写：必须与 csproj 的 Include（Assets 大写 A）一致，
            // 写成 assets 会抛 FileNotFoundException → Icon 为 null → 托盘图标完全不显示
            using var stream = AssetLoader.Open(new Uri("avares://PortKiller/Assets/avalonia-logo.ico"));
            icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            // 不再静默：图标加载失败写入 error.log（Icon=null 时托盘不可见，必须可追溯）
            App.WriteLog("TrayIcon", ex);
        }

        var menu = new NativeMenu();
        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => Vm?.ShowMainFromTrayCommand.Execute(null);
        var release = new NativeMenuItem("快速释放全部 TIME_WAIT");
        release.Click += (_, _) => _ = Vm?.ReleaseAllFromTrayAsync();
        var pause = new NativeMenuItem("暂停后台实时监听");
        pause.Click += (_, _) => { Vm?.ToggleWatchFromTrayCommand.Execute(null); UpdateTrayTooltip(); };
        var settingsItem = new NativeMenuItem("打开系统设置");
        settingsItem.Click += (_, _) => Vm?.OpenSettingsFromTrayCommand.Execute(null);
        var exit = new NativeMenuItem("退出 PortKiller");
        exit.Click += (_, _) =>
        {
            ShowFromTray();
            if (Vm != null) Vm.IsExitConfirmVisible = true;
        };
        menu.Items.Add(show);
        menu.Items.Add(release);
        menu.Items.Add(pause);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "PortKiller · 端口监控",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => ShowFromTray();
        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current, icons);
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon == null || Vm == null) return;
        _trayIcon.ToolTipText = Vm.IsAutoRefresh
            ? $"PortKiller · 共 {Vm.TotalCount} 个端口 · 监听中"
            : $"PortKiller · 共 {Vm.TotalCount} 个端口 · 监听已暂停";
    }

    // ── HUD ──

    private void ShowHud()
    {
        if (Vm == null) return;
        if (_hud is { } hud && hud.IsVisible) return;

        _hud ??= new HudWindow { DataContext = Vm };
        _hud.PositionHud(Vm.HudX, Vm.HudY);
        _hud.Show();
        _hud.PositionHud(Vm.HudX, Vm.HudY);
    }

    private void CloseHud()
    {
        _hud?.Hide();
    }
}
