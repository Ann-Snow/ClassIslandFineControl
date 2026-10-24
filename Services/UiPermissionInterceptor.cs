using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Shared;
using ClassIslandFineControl.Models;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;

namespace ClassIslandFineControl.Services;

/// <summary>
/// UI 权限拦截器（v1.4.5 精简版）。
///
/// 只保留两套机制：
///   1. 主流：唯一命中匹配 —— 精确模式（HeaderText / Name / AutomationId）在页内
///      只有唯一命中时才执行；多处同名默认跳过并记录一次警告，从源头避免误伤。
///      通配模式（Wildcard）保持“匹配全部”，因为那是管理员显式表达的批量意图。
///      命中容器时连同其后代一起处理（父锁子，保留既定行为）。
///   2. 保底：index —— 精确匹配确实多处同名时，用 index 指定可视树顺序中的第 N 个。
///
/// 触发全部为事件驱动，无任何轮询/定时器：
///   - Window.Opened / Frame.Navigated / Page.AttachedToVisualTree
///   - 插件列表 SelectionChanged（主流）
///   - 导航菜单或插件列表 CollectionChanged（唯一保底：结构变化后合并重应用一次）
///   - ConfigChanged（配置热更新）
/// 每个事件只对“当前页”应用一次，不再全窗口反复遍历。
/// </summary>
public class UiPermissionInterceptor
{
    private readonly ILogger<UiPermissionInterceptor> _logger;
    private readonly SettingsValueGuard _valueGuard;
    private PermissionManager? _manager;
    private Window? _settingsWindow;

    /// <summary>当前设置页（Frame.Navigated 或窗口打开时确定）。</summary>
    private SettingsPageBase? _currentPage;

    /// <summary>由本插件禁用的控件（用于权限恢复后重新启用）。</summary>
    private readonly HashSet<Control> _controlsDisabledByUs = new();

    /// <summary>由本插件隐藏的控件（Hidden 动作，用于权限恢复后重新显示）。</summary>
    private readonly HashSet<Control> _controlsHiddenByUs = new();

    /// <summary>由自保护写死隐藏的插件页“刷新”入口（关闭自保护时用于恢复）。</summary>
    private readonly HashSet<Control> _refreshControlsHiddenByUs = new();

    /// <summary>由本插件禁用的导航项（用于权限恢复后重新启用）。</summary>
    private readonly HashSet<NavigationViewItem> _navItemsDisabledByUs = new();

    /// <summary>由本插件隐藏的导航项（Hidden 动作，用于权限恢复后重新显示）。</summary>
    private readonly HashSet<NavigationViewItem> _navItemsHiddenByUs = new();

    /// <summary>
    /// 已从 NavigationView.MenuItems 物理移除的导航项及其原始下标。
    /// 官方 SelectNavigationItem 按 MenuItems.Contains 决定是否设置 SelectedItem，
    /// 且 NavigationView 对“隐藏项/空分组”的布局与选中存在缺陷；物理移除后
    /// 隐藏项不可被选中、不参与布局，恢复时按原始下标插回。
    /// </summary>
    private readonly Dictionary<NavigationViewItem, int> _removedNavItems = new();

    /// <summary>已挂接 AttachedToVisualTree 事件的设置页实例（避免重复挂接）。</summary>
    private readonly HashSet<SettingsPageBase> _hookedPageBases = new();

    /// <summary>已挂接 SelectionChanged 事件的插件列表 ListBox（避免重复挂接）。</summary>
    private readonly HashSet<ListBox> _hookedPluginLists = new();

    /// <summary>已订阅 CollectionChanged 的插件列表数据集合（结构变化保底，避免重复挂接）。</summary>
    private readonly HashSet<System.Collections.Specialized.INotifyCollectionChanged> _hookedPluginItems = new();

    /// <summary>已挂接 Content 变化事件的插件详情宿主（避免重复挂接）。</summary>
    private readonly HashSet<Control> _hookedDetailHosts = new();

    /// <summary>已挂接 CollectionChanged 的导航菜单集合（避免重复挂接）。</summary>
    private readonly HashSet<System.Collections.Specialized.INotifyCollectionChanged> _hookedNavCollections = new();

    /// <summary>已挂接 Navigated 事件的导航 Frame（避免重复挂接）。</summary>
    private readonly HashSet<Control> _hookedFrames = new();

    /// <summary>已记录过“多处同名已跳过”警告的规则键（配置变更时清空，避免每次应用刷日志）。</summary>
    private readonly HashSet<string> _warnedRules = new();

    /// <summary>页面应用防抖标志：同一布局周期内多次触发只调度一次。</summary>
    private bool _pageApplyScheduled;

    /// <summary>防抖期间最近一次请求应用的页面（后到的页面覆盖先到的）。</summary>
    private SettingsPageBase? _pendingPage;

    /// <summary>导航应用防抖标志：同一布局周期内多次触发只调度一次。</summary>
    private bool _navApplyScheduled;

    /// <summary>整窗应用防抖标志（配置变更/应用菜单打开时使用）。</summary>
    private bool _windowApplyScheduled;

    /// <summary>已挂接 Opening 事件的“三个点”应用菜单 Flyout（避免重复挂接）。</summary>
    private readonly HashSet<MenuFlyout> _hookedAppMenuFlyouts = new();

    /// <summary>“三个点”按钮缓存（窗口重建时重置，避免每次页面事件全窗扫描）。</summary>
    private Button? _appMenuButton;

    /// <summary>官方主窗口、托盘原生菜单与托盘图标。</summary>
    private Window? _mainWindow;
    private NativeMenu? _trayMenu;
    private TrayIcon? _trayIcon;

    /// <summary>由本插件禁用/隐藏的托盘菜单项（用于恢复）。</summary>
    private readonly HashSet<NativeMenuItem> _trayItemsDisabledByUs = new();
    private readonly HashSet<NativeMenuItem> _trayItemsHiddenByUs = new();

    /// <summary>托盘图标是否由本插件隐藏。</summary>
    private bool _trayIconHiddenByUs;

    /// <summary>
    /// 启动安全锁是否生效：权限配置尚未拉取完成时先锁死托盘菜单，
    /// 避免“重启后的一小段时间内托盘菜单仍可操作”的窗口期。
    /// </summary>
    private bool _trayGuardActive;

    /// <summary>“三个点”按钮是否由本插件隐藏/置灰（用于恢复）。</summary>
    private bool _appMenuButtonHiddenByUs;
    private bool _appMenuButtonDisabledByUs;

    /// <summary>本次窗口打开是否已执行“默认页导航”（每次 Opened 重置）。</summary>
    private bool _defaultPageApplied;

    /// <summary>是否已通过反射改写官方 LaunchSettingsPage，让官方 OnOpened 直接导航到默认页。</summary>
    private bool _launchPageOverrideApplied;

    /// <summary>
    /// 本次打开是否已完成“首次导航判定”。完成后再切页一律视为用户操作，
    /// 不再执行默认页纠正，避免把用户手动选择的页面拉回集控页。
    /// </summary>
    private bool _initialNavigationSettled;

    /// <summary>窗口是否已经历过一次 Opened（用于区分首次打开与后续显示）。</summary>
    private bool _opened;

    /// <summary>
    /// 首次打开时等待官方完成初始导航（官方 OnOpened 会先导航到“基本”页）。
    /// 在 Frame.Navigated 之前不执行默认页导航，避免被官方初始导航覆盖。
    /// </summary>
    private bool _awaitInitialNavigation;

    /// <summary>应用重入保护：防止事件链嵌套递归导致 UI 线程挂起（幂等，重入时丢弃）。</summary>
    private bool _applying;

    /// <summary>官方设置窗口类型全名（避免引用主程序程序集）。</summary>
    private const string SettingsWindowTypeName = "ClassIsland.Views.SettingsWindowNew";

    /// <summary>本插件自身的插件 id（写死置灰仅针对本插件）。</summary>
    private const string SelfPluginId = "cn.finecontrol.permission";

    /// <summary>匹配模式缺省值：按控件标题文本匹配。</summary>
    private const string DefaultMatchMode = "HeaderText";

    /// <summary>官方插件页设置页 Id。</summary>
    private const string PluginsPageId = "classisland.plugins";

    /// <summary>“三个点”应用菜单的伪设置页 Id（仅用于菜单项权限，不参与设置页导航）。</summary>
    private const string AppMenuPageId = "classisland.appmenu";

    /// <summary>托盘图标原生菜单的伪设置页 Id。</summary>
    private const string TrayMenuPageId = "classisland.traymenu";

    /// <summary>官方主窗口类型全名（托盘菜单宿主）。</summary>
    private const string MainWindowTypeName = "ClassIsland.MainWindow";

    /// <summary>
    /// 识别“三个点”应用菜单的标志性菜单项（Header 已归一化为半角省略号）。
    /// 命中任意一个即认定该 MenuFlyout 为应用菜单，而不是插件详情等其它菜单。
    /// </summary>
    private static readonly string[] AppMenuMarkers =
        { "实验性功能...", "调试窗口规则...", "导出诊断数据" };

    /// <summary>插件“已启用”开关的 Content 文本（详情面板形态）。</summary>
    private const string TextEnabled = "已启用";

    /// <summary>插件“移除”按钮的文本。</summary>
    private const string TextRemove = "移除";

    /// <summary>
    /// 写死置灰的本插件工具菜单项 Header（用半角省略号，
    /// 匹配时 Header 会先经 NormalizeEllipsis 归一化为半角）。
    /// </summary>
    private static readonly string[] SelfToolMenuHeaders =
        { "打包插件", "打开插件文件夹...", "打开配置文件夹..." };

    public UiPermissionInterceptor(SettingsValueGuard valueGuard, ILogger<UiPermissionInterceptor> logger)
    {
        _valueGuard = valueGuard;
        _logger = logger;
    }

    /// <summary>
    /// 启动拦截器（应用启动完成后调用）。
    /// </summary>
    public void Start(PermissionManager manager)
    {
        _manager = manager;
        _valueGuard.Start(manager); // 值级锁定（与 UI 拦截互补）

        // 配置变更：清空警告缓存，整窗重应用一次（导航 + 当前页 + 应用菜单）
        manager.ConfigChanged += () =>
        {
            _warnedRules.Clear();
            if (_settingsWindow != null)
            {
                ScheduleWindowApply(_settingsWindow);
            }

            if (_mainWindow != null)
            {
                Dispatcher.UIThread.Post(ApplyTrayMenu, DispatcherPriority.Background);
            }
        };

        // 刷新流程结束：配置仍未加载时解除托盘菜单的启动安全锁
        //（未加入集控 / 拉取失败 / 关闭清单驱动 → 与“全部可操作”的降级语义一致）
        manager.RefreshCompleted += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_manager?.CurrentConfig == null)
                {
                    UnlockTrayGuard();
                }
                else
                {
                    ApplyTrayMenu();
                }
            }, DispatcherPriority.Background);
        };

        // 设置窗口创建（Windows 集合通知）→ 挂接事件；启动时窗口可能已存在
        if (AppBase.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            if (lifetime.Windows is System.Collections.Specialized.INotifyCollectionChanged windows)
            {
                windows.CollectionChanged += (_, e) =>
                {
                    if (e.NewItems == null)
                    {
                        return;
                    }

                    foreach (var item in e.NewItems.OfType<Window>())
                    {
                        EnsureWindowHooked(item);
                    }
                };
            }

            foreach (var window in lifetime.Windows)
            {
                EnsureWindowHooked(window);
            }
        }
    }

    /// <summary>
    /// 发现并挂接设置窗口（幂等）：窗口创建时挂接 Opened 并立即应用一次
    /// （窗口未打开时遍历空树，无害）；打开后由 Opened 再应用一次。
    /// </summary>
    private void EnsureWindowHooked(Window window)
    {
        var typeName = window.GetType().FullName;
        if (typeName == MainWindowTypeName)
        {
            HookMainWindow(window);
            return;
        }

        if (typeName != SettingsWindowTypeName)
        {
            return;
        }

        if (ReferenceEquals(window, _settingsWindow))
        {
            return;
        }

        _settingsWindow = window;
        _currentPage = null;
        _appMenuButton = null;
        _opened = false;
        _initialNavigationSettled = false;
        _appMenuButtonHiddenByUs = false;
        _appMenuButtonDisabledByUs = false;
        window.Opened += (_, _) =>
        {
            _opened = true;
            _defaultPageApplied = false;
            _initialNavigationSettled = false;

            // 关键：官方 OnOpened 在 base.OnOpened（本事件）之后才读取 LaunchSettingsPage
            // 并 CoreNavigate。这里先行改写为默认页，让官方自己完成首次导航。
            // 集控页标记了 HideDefault=true，根本不在导航栏里，因此不能靠选导航项。
            var targetPageId = _manager?.Settings.DefaultSettingsPageId;
            _launchPageOverrideApplied = !string.IsNullOrWhiteSpace(targetPageId) &&
                                         TryOverrideLaunchSettingsPage(window, targetPageId!);

            // 改写成功时由官方负责导航；失败时才等待 Frame.Navigated 后自行导航
            var currentPage = GetCurrentPage(window);
            _awaitInitialNavigation = !_launchPageOverrideApplied && currentPage == null;

            // 窗口重新打开时如果已经停在默认页，则首次导航判定直接完成，
            // 避免用户第一次切页被误判为“改写未生效”而拉回。
            _initialNavigationSettled = _launchPageOverrideApplied &&
                                        currentPage != null &&
                                        !string.IsNullOrWhiteSpace(targetPageId) &&
                                        string.Equals(GetPageInfo(currentPage)?.Id, targetPageId,
                                            StringComparison.OrdinalIgnoreCase);
            ApplyAll(window);

            if (!_awaitInitialNavigation && !_launchPageOverrideApplied)
            {
                // 窗口复用/重开时帧内已有页面，布局后直接应用默认页
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_settingsWindow, window) || !window.IsVisible)
                    {
                        return;
                    }

                    _defaultPageApplied = false;
                    ApplyDefaultPage(window);
                }, DispatcherPriority.Background);
            }
        };

        // 窗口被隐藏后再次显示（单例窗口场景）：同样执行一次默认页导航
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty || !window.IsVisible || !_opened)
            {
                return;
            }

            _launchPageOverrideApplied = false;
            _awaitInitialNavigation = false;
            _defaultPageApplied = false;
            // 窗口重新显示：不参与“首次导航判定”，避免把后续用户切页当成覆盖失败
            _initialNavigationSettled = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_settingsWindow, window) && window.IsVisible)
                {
                    ApplyDefaultPage(window);
                }
            }, DispatcherPriority.Background);
        };
        ApplyAll(window);
    }

    /// <summary>
    /// 挂接官方主窗口（托盘菜单宿主）：加载后解析原生菜单 AppMenu 与托盘图标。
    /// </summary>
    private void HookMainWindow(Window window)
    {
        if (ReferenceEquals(window, _mainWindow))
        {
            return;
        }

        _mainWindow = window;
        ResolveTrayMenu(window);
        ResolveTrayIcon();

        window.Loaded += (_, _) =>
        {
            ResolveTrayMenu(window);
            ResolveTrayIcon();
            ApplyTrayMenu();
        };
        window.Opened += (_, _) =>
        {
            ResolveTrayMenu(window);
            ApplyTrayMenu();
        };

        ApplyTrayMenu();
    }

    /// <summary>解析主窗口资源里的原生托盘菜单（AppMenu）。</summary>
    private void ResolveTrayMenu(Window window)
    {
        if (_trayMenu != null)
        {
            return;
        }

        try
        {
            _trayMenu = window.FindResource("AppMenu") as NativeMenu;
            if (_trayMenu != null)
            {
                _logger.LogInformation("已挂接托盘图标菜单（AppMenu）。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析托盘图标菜单失败。");
        }
    }

    /// <summary>解析官方 TaskBarIconService 的托盘图标（用于页面级隐藏）。</summary>
    private void ResolveTrayIcon()
    {
        if (_trayIcon != null)
        {
            return;
        }

        try
        {
            var serviceType = Type.GetType(
                "ClassIsland.Core.Abstractions.Services.ITaskBarIconService, ClassIsland.Core");
            var service = serviceType != null
                ? IAppHost.Host?.Services.GetService(serviceType)
                : null;
            _trayIcon = service?.GetType().GetProperty("MainTaskBarIcon")?.GetValue(service) as TrayIcon;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析托盘图标失败。");
        }
    }

    /// <summary>
    /// 托盘图标原生菜单管控（伪页面 classisland.traymenu）：
    ///   - 页面级：Hidden 隐藏托盘图标；Disabled 禁用全部菜单项；
    ///   - 控件级：按 Header 匹配 NativeMenuItem，支持 Disabled/Hidden/index。
    /// 原生菜单不在可视树中，直接遍历 NativeMenu.Items（含子菜单）。
    /// </summary>
    private void ApplyTrayMenu()
    {
        if (_manager == null)
        {
            return;
        }

        if (_trayMenu == null && _mainWindow != null)
        {
            ResolveTrayMenu(_mainWindow);
        }

        if (_trayMenu == null)
        {
            return;
        }

        // 配置尚未拉取完成（仍在刷新中）：先锁死托盘菜单（安全默认），
        // 避免启动后网络拉取完成前的“可操作窗口期”。
        // 刷新已结束且仍无配置时保持既有降级语义（全部可操作），不再锁。
        if (_manager.CurrentConfig == null)
        {
            if (_manager.IsRefreshing)
            {
                LockTrayGuard();
            }

            return;
        }

        UnlockTrayGuard();

        var pageState = _manager.IsPageExplicitlyConfigured(TrayMenuPageId)
            ? _manager.GetPageState(TrayMenuPageId)
            : PermissionState.Operable;
        ApplyTrayPageState(pageState);

        var items = CollectNativeMenuItems(_trayMenu.Items).ToList();
        var rules = _manager.GetItemsForPage(TrayMenuPageId)
            .Where(x => x.IsDisabled)
            .ToList();

        var matched = new Dictionary<NativeMenuItem, PermissionState>();
        foreach (var rule in rules)
        {
            var candidates = items.Where(i => MatchesNative(i, rule)).ToList();
            var selected = SelectTrayItems(rule, TrayMenuPageId, candidates);
            if (selected.Count == 0)
            {
                continue;
            }

            var state = rule.IsHidden ? PermissionState.Hidden : PermissionState.Disabled;
            foreach (var item in selected)
            {
                PromoteNative(matched, item, state);
            }
        }

        // 页面级 Disabled：全部菜单项置灰
        if (pageState == PermissionState.Disabled)
        {
            foreach (var item in items)
            {
                PromoteNative(matched, item, PermissionState.Disabled);
            }
        }

        ApplyNativeStates(matched);
        RestoreNativeItems(items, matched);
    }

    private void ApplyTrayPageState(PermissionState state)
    {
        if (_trayIcon == null)
        {
            return;
        }

        if (state == PermissionState.Hidden)
        {
            if (_trayIcon.IsVisible)
            {
                _trayIcon.IsVisible = false;
                _trayIconHiddenByUs = true;
            }
        }
        else if (_trayIconHiddenByUs)
        {
            _trayIcon.IsVisible = true;
            _trayIconHiddenByUs = false;
        }
    }

    /// <summary>
    /// 启动安全锁：权限配置尚未就绪时把托盘菜单项全部置灰，
    /// 防止重启后的一小段时间内学生仍可通过托盘菜单操作。
    /// 配置加载完成（或刷新结束仍无配置）后由 ApplyTrayMenu / UnlockTrayGuard 解除。
    /// </summary>
    private void LockTrayGuard()
    {
        if (_trayMenu == null)
        {
            return;
        }

        _trayGuardActive = true;
        foreach (var item in CollectNativeMenuItems(_trayMenu.Items))
        {
            if (!item.IsEnabled)
            {
                continue;
            }

            item.IsEnabled = false;
            _trayItemsDisabledByUs.Add(item);
        }
    }

    /// <summary>解除启动安全锁（只恢复由本锁置灰的项）。</summary>
    private void UnlockTrayGuard()
    {
        if (!_trayGuardActive)
        {
            return;
        }

        foreach (var item in _trayItemsDisabledByUs.ToList())
        {
            item.IsEnabled = true;
            _trayItemsDisabledByUs.Remove(item);
        }

        _trayGuardActive = false;
    }

    private static IEnumerable<NativeMenuItem> CollectNativeMenuItems(
        System.Collections.IEnumerable items)
    {
        foreach (var itemObject in items)
        {
            if (itemObject is not NativeMenuItem item)
            {
                continue;
            }

            yield return item;

            if (item.Menu is { } submenu)
            {
                foreach (var child in CollectNativeMenuItems(submenu.Items))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool MatchesNative(NativeMenuItem item, PermissionItem rule)
    {
        var key = rule.ControlKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var mode = string.IsNullOrWhiteSpace(rule.MatchMode) ? DefaultMatchMode : rule.MatchMode;
        switch (mode.ToLowerInvariant())
        {
            case "headertext":
                return string.Equals(
                    NormalizeEllipsis(item.Header),
                    NormalizeEllipsis(key),
                    StringComparison.OrdinalIgnoreCase);

            case "wildcard":
                var pattern = "^" + Regex.Escape(NormalizeEllipsis(key))
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(
                    NormalizeEllipsis(item.Header ?? ""), pattern, RegexOptions.IgnoreCase);

            default:
                // 原生菜单没有 x:Name / AutomationId
                return false;
        }
    }

    private List<NativeMenuItem> SelectTrayItems(
        PermissionItem rule,
        string pageId,
        List<NativeMenuItem> candidates)
    {
        if (candidates.Count == 0)
        {
            return new List<NativeMenuItem>();
        }

        if (rule.Index > 0)
        {
            if (rule.Index <= candidates.Count)
            {
                return new List<NativeMenuItem> { candidates[rule.Index - 1] };
            }

            WarnOnce(pageId, rule,
                $"index={rule.Index} 超出命中数 {candidates.Count}，已跳过。");
            return new List<NativeMenuItem>();
        }

        var mode = string.IsNullOrWhiteSpace(rule.MatchMode) ? DefaultMatchMode : rule.MatchMode;
        if (string.Equals(mode, "Wildcard", StringComparison.OrdinalIgnoreCase))
        {
            return candidates;
        }

        if (candidates.Count == 1)
        {
            return candidates;
        }

        WarnOnce(pageId, rule,
            $"菜单内命中 {candidates.Count} 处（" +
            string.Join("；", candidates.Take(5).Select((i, n) =>
                $"#{n + 1} NativeMenuItem「{NormalizeEllipsis(i.Header ?? "")}」")) +
            "），已跳过以避免误伤。请改用唯一 Header，或配置 index 指定第 N 处。");
        return new List<NativeMenuItem>();
    }

    private static void PromoteNative(
        Dictionary<NativeMenuItem, PermissionState> matched,
        NativeMenuItem item,
        PermissionState state)
    {
        if (matched.TryGetValue(item, out var existing))
        {
            if (state == PermissionState.Hidden)
            {
                matched[item] = PermissionState.Hidden;
            }

            return;
        }

        matched[item] = state;
    }

    private void ApplyNativeStates(Dictionary<NativeMenuItem, PermissionState> matched)
    {
        foreach (var pair in matched)
        {
            var item = pair.Key;
            if (pair.Value == PermissionState.Hidden)
            {
                if (item.IsVisible)
                {
                    item.IsVisible = false;
                    _trayItemsHiddenByUs.Add(item);
                }
            }
            else
            {
                if (_trayItemsHiddenByUs.Remove(item))
                {
                    item.IsVisible = true;
                }

                if (item.IsEnabled)
                {
                    item.IsEnabled = false;
                    _trayItemsDisabledByUs.Add(item);
                }
            }
        }
    }

    private void RestoreNativeItems(
        List<NativeMenuItem> items,
        Dictionary<NativeMenuItem, PermissionState> matched)
    {
        var scope = new HashSet<NativeMenuItem>(items);
        foreach (var item in _trayItemsDisabledByUs.ToList())
        {
            if (scope.Contains(item) && !matched.ContainsKey(item))
            {
                item.IsEnabled = true;
                _trayItemsDisabledByUs.Remove(item);
            }
        }

        foreach (var item in _trayItemsHiddenByUs.ToList())
        {
            if (scope.Contains(item) && !matched.ContainsKey(item))
            {
                item.IsVisible = true;
                _trayItemsHiddenByUs.Remove(item);
            }
        }
    }

    private void ApplyAll(Window window)
    {
        if (!BeginApply())
        {
            return;
        }

        try
        {
            HookFrameNavigated(window);

            var page = GetCurrentPage(window);
            if (page != null)
            {
                _currentPage = page;
            }

            ApplyNavigation(window);
            if (_currentPage != null)
            {
                // 已在 ApplyAll 的 _applying 保护内，直接执行，避免二次 BeginApply 被吞掉
                ApplyPage(_currentPage);
                ApplyPluginLockControls(_currentPage);
            }

            // “三个点”应用菜单（窗口标题栏，位于设置页之外）
            ApplyAppMenu(window);

            PruneStale(window);
        }
        catch (Exception ex)
        {
            // 单次应用失败不中断，等待下一次事件触发重试
            _logger.LogDebug(ex, "UI 权限应用失败。");
        }
        finally
        {
            EndApply();
        }
    }

    /// <summary>对指定页面应用控件权限与插件锁定（事件驱动的统一入口）。</summary>
    private void ApplyPageAndLocks(SettingsPageBase page)
    {
        if (!BeginApply())
        {
            return;
        }

        try
        {
            ApplyPage(page);
            ApplyPluginLockControls(page);

            // 应用菜单在设置页之外，页面事件时顺带应用（按钮缓存，无需每次全窗扫描）
            if (_settingsWindow != null)
            {
                ApplyAppMenu(_settingsWindow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "页面权限应用失败。");
        }
        finally
        {
            EndApply();
        }
    }

    private bool BeginApply()
    {
        if (_applying)
        {
            return false;
        }

        _applying = true;
        return true;
    }

    private void EndApply() => _applying = false;

    /// <summary>
    /// 延迟到布局完成后对页面应用一次（Background 优先级在布局之后，
    /// 此时设置项控件已构建完成）。防抖：同一周期只调度一次。
    /// </summary>
    private void SchedulePageApply(SettingsPageBase? page)
    {
        if (page != null)
        {
            // 后到的页面覆盖先到的：连续切页时只应用最终页面
            _pendingPage = page;
        }

        if (_pageApplyScheduled || _pendingPage == null)
        {
            return;
        }

        _pageApplyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pageApplyScheduled = false;
            var target = _pendingPage;
            _pendingPage = null;
            if (target != null)
            {
                ApplyPageAndLocks(target);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 延迟到布局完成后重新应用导航项一次（结构变化保底，防抖）。
    /// </summary>
    private void ScheduleNavApply(Window window)
    {
        if (_navApplyScheduled)
        {
            return;
        }

        _navApplyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _navApplyScheduled = false;
            if (!BeginApply())
            {
                return;
            }

            try
            {
                ApplyNavigation(window);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "导航权限应用失败。");
            }
            finally
            {
                EndApply();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 延迟到布局完成后整窗重应用一次（配置变更时使用，防抖）。
    /// </summary>
    private void ScheduleWindowApply(Window window)
    {
        if (_windowApplyScheduled)
        {
            return;
        }

        _windowApplyScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _windowApplyScheduled = false;
            ApplyAll(window);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 挂接导航 Frame 的 Navigated 事件：切页时只对新的当前页应用一次。
    /// </summary>
    private void HookFrameNavigated(Window window)
    {
        var frame = FindDescendant(window, c =>
            c.GetType().FullName == "FluentAvalonia.UI.Controls.Frame");
        if (frame is not Frame fluentFrame || !_hookedFrames.Add(frame))
        {
            return;
        }

        fluentFrame.Navigated += (_, _) =>
        {
            if (fluentFrame.Content is SettingsPageBase page)
            {
                _currentPage = page;

                var targetPageId = _manager?.Settings.DefaultSettingsPageId;
                if (!_initialNavigationSettled)
                {
                    // 只在本次打开后的第一次导航做判定：若改写未生效（先到了别的页），
                    // 才走备用导航；一旦首次导航就到达目标页，后续用户切页不再纠正。
                    _initialNavigationSettled = true;
                    if (_launchPageOverrideApplied &&
                        !string.IsNullOrWhiteSpace(targetPageId) &&
                        !string.Equals(GetPageInfo(page)?.Id, targetPageId, StringComparison.OrdinalIgnoreCase))
                    {
                        _launchPageOverrideApplied = false;
                        _defaultPageApplied = false;
                    }
                }

                _awaitInitialNavigation = false;
                ApplyDefaultPage(window);
                SchedulePageApply(page);
            }
        };
    }

    /// <summary>读取导航 Frame 当前承载的设置页；未就绪时返回 null。</summary>
    private static SettingsPageBase? GetCurrentPage(Window window)
    {
        var frame = FindDescendant(window, c =>
            c.GetType().FullName == "FluentAvalonia.UI.Controls.Frame");
        return (frame as Frame)?.Content as SettingsPageBase;
    }

    /// <summary>
    /// 页面级拦截：导航项三态（Disabled 置灰 / Hidden 物理隐藏 / Operable 恢复）。
    /// 导航是两层结构（分组容器 + 子项），必须递归遍历；
    /// 分组容器只在“大页规则生效”或“组内子项全部被隐藏”时处理，避免误伤整组。
    /// </summary>
    private void ApplyNavigation(Window window)
    {
        if (_manager == null)
        {
            return;
        }

        var navigationView = FindDescendant(window, c => c is NavigationView);
        if (navigationView is not NavigationView nav)
        {
            return;
        }

        // 唯一保底：导航菜单集合变化（第三方插件动态注册设置页）→ 合并重应用一次。
        // 本插件自己移除/插回导航项时 _applying 为 true，直接忽略，避免自触发循环。
        if (nav.MenuItems is System.Collections.Specialized.INotifyCollectionChanged navItems &&
            _hookedNavCollections.Add(navItems))
        {
            navItems.CollectionChanged += (_, _) =>
            {
                if (_applying)
                {
                    return;
                }

                ScheduleNavApply(window);
            };
        }

        // 快照遍历——隐藏项会从 MenuItems 物理移除，直接 foreach 会抛异常。
        foreach (var itemObject in nav.MenuItems.ToList())
        {
            if (itemObject is NavigationViewItem item)
            {
                ApplyNavigationItemRecursive(nav, item);
            }
        }

        // 恢复已物理移除的导航项（按原始下标插回）
        foreach (var item in _removedNavItems.Keys.ToList())
        {
            if (ShouldRestoreRemovedItem(item))
            {
                RestoreNavigationItem(nav, item);
            }
        }

        // 修正选中：当前选中项被置灰/隐藏时置 null 保持无选中。
        // 不自动改选可见项——改选会触发官方导航并加载页面内容，
        // 导致被隐藏的启动目标被“顶替”为其他可用页。
        if (nav.SelectedItem is NavigationViewItem selected &&
            (!selected.IsVisible || !selected.IsEnabled))
        {
            nav.SelectedItem = null;
        }

        // 清理孤立的导航分隔线（全部导航项被隐藏后不留“两条线”）
        for (var i = nav.MenuItems.Count - 1; i >= 0; i--)
        {
            if (nav.MenuItems[i] is NavigationViewItemSeparator)
            {
                var visibleBefore = nav.MenuItems.Take(i).OfType<NavigationViewItem>()
                    .Any(x => x.IsVisible && x.IsEnabled);
                var visibleAfter = nav.MenuItems.Skip(i + 1).OfType<NavigationViewItem>()
                    .Any(x => x.IsVisible && x.IsEnabled);
                if (!visibleBefore || !visibleAfter)
                {
                    nav.MenuItems.RemoveAt(i);
                }
            }
        }

        // 首次打开时按 Config.json 的 DefaultSettingsPageId 导航（默认集控页）
        ApplyDefaultPage(window);
    }

    /// <summary>
    /// 设置窗口打开时默认导航到指定设置页
    /// （Config.json 的 DefaultSettingsPageId，默认 "management" 即集控页）。
    /// 仅在页面存在且未被权限禁用/隐藏时执行；每次 Opened 只尝试一次，
    /// 不干扰用户后续手动切页。
    /// </summary>
    private void ApplyDefaultPage(Window window)
    {
        if (_defaultPageApplied || _manager == null || !window.IsVisible ||
            _awaitInitialNavigation || _launchPageOverrideApplied)
        {
            return;
        }

        var targetId = _manager.Settings.DefaultSettingsPageId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            _defaultPageApplied = true;
            return;
        }

        // 已经在目标页则无需导航
        var currentPage = GetCurrentPage(window);
        if (currentPage != null &&
            string.Equals(GetPageInfo(currentPage)?.Id, targetId, StringComparison.OrdinalIgnoreCase))
        {
            _defaultPageApplied = true;
            return;
        }

        // 权限层面不可操作时跳过（显式配置才检查，避免 defaultPermission 误伤隐藏页）
        if (_manager.IsPageExplicitlyConfigured(targetId) &&
            _manager.GetPageState(targetId) != PermissionState.Operable)
        {
            _logger.LogInformation("默认设置页 {PageId} 被权限禁用/隐藏，跳过。", targetId);
            _defaultPageApplied = true;
            return;
        }

        // 优先从导航项取 SettingsPageInfo；集控页 HideDefault=true 不在导航栏，
        // 此时改用官方页面注册表（SettingsWindowRegistryService.Registered）。
        var nav = FindDescendant(window, c => c is NavigationView) as NavigationView;
        NavigationViewItem? item = null;
        if (nav != null && nav.MenuItems.Count > 0)
        {
            item = FindNavigationItem(nav.MenuItems, targetId);
            if (item == null && string.Equals(targetId, "management", StringComparison.OrdinalIgnoreCase))
            {
                item = FindNavigationItemByHeader(nav.MenuItems, "集控");
            }
        }

        var info = item?.Tag as SettingsPageInfo ?? FindRegisteredPageInfo(targetId);
        if (info == null)
        {
            _logger.LogWarning("默认设置页未找到：{PageId}（导航项 {Count} 个）。",
                targetId, nav?.MenuItems.Count ?? 0);
            _defaultPageApplied = true;
            return;
        }

        if (item != null && (!item.IsVisible || !item.IsEnabled))
        {
            _logger.LogInformation(
                "默认设置页 {PageId} 当前不可导航（可见={Visible}，启用={Enabled}），跳过。",
                targetId, item.IsVisible, item.IsEnabled);
            _defaultPageApplied = true;
            return;
        }

        _defaultPageApplied = true;
        _logger.LogInformation("默认设置页：导航到 {PageId}。", targetId);
        ScheduleNavigateToPage(window, info);
    }

    /// <summary>
    /// 反射改写官方 SettingsWindowNew 的私有属性 LaunchSettingsPage，并重置
    /// _isFirstNavigated，让官方 OnOpened 自己把启动页导航到指定页。
    /// 这是“集控页 HideDefault=true、不在导航栏”情况下最稳妥的路径：
    /// 官方 CoreNavigate 不检查 HideDefault，可以正常加载该页。
    /// </summary>
    private bool TryOverrideLaunchSettingsPage(Window window, string pageId)
    {
        try
        {
            var type = window.GetType();
            var launchProperty = type.GetProperty(
                "LaunchSettingsPage", BindingFlags.Instance | BindingFlags.NonPublic);
            if (launchProperty?.CanWrite != true)
            {
                return false;
            }

            launchProperty.SetValue(window, pageId);
            type.GetField("_isFirstNavigated", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(window, false);

            _logger.LogInformation("默认设置页：已改写官方启动页为 {PageId}。", pageId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "改写官方启动页失败，改用 CoreNavigate 兜底。");
            return false;
        }
    }

    /// <summary>
    /// 延迟到布局后，按官方导航路径切到目标页：
    ///   1. 反射调用 SettingsWindowNew.CoreNavigate(info, null)（与官方点击导航完全一致）；
    ///   2. 失败时设置 ViewModel.SelectedPageInfo（官方变更监听会触发 CoreNavigate）；
    ///   3. 再失败时兜底选中对应导航项（若存在）。
    /// </summary>
    private void ScheduleNavigateToPage(Window window, SettingsPageInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_settingsWindow, window) || !window.IsVisible)
            {
                return;
            }

            if (TryCoreNavigate(window, info))
            {
                return;
            }

            // 兜底 1：设置官方 ViewModel.SelectedPageInfo
            var viewModel = window.GetType().GetProperty("ViewModel")?.GetValue(window);
            var selectedProperty = viewModel?.GetType().GetProperty("SelectedPageInfo");
            if (viewModel != null && selectedProperty != null)
            {
                try
                {
                    selectedProperty.SetValue(viewModel, info);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "设置 ViewModel.SelectedPageInfo 失败。");
                }
            }

            // 兜底 2：直接选中导航项（仅导航栏中存在的页面）
            var nav = FindDescendant(window, c => c is NavigationView) as NavigationView;
            if (nav != null)
            {
                var item = FindNavigationItem(nav.MenuItems, info.Id);
                if (item != null)
                {
                    nav.SelectedItem = item;
                }
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>从官方页面注册表中查找指定 Id 的 SettingsPageInfo（含 HideDefault 页面）。</summary>
    private static SettingsPageInfo? FindRegisteredPageInfo(string pageId)
    {
        try
        {
            var registryType = typeof(SettingsPageInfo).Assembly.GetType(
                "ClassIsland.Core.Services.Registry.SettingsWindowRegistryService");
            if (registryType?.GetProperty("Registered", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) is System.Collections.IEnumerable registered)
            {
                foreach (var entry in registered)
                {
                    if (entry is SettingsPageInfo info &&
                        string.Equals(info.Id, pageId, StringComparison.OrdinalIgnoreCase))
                    {
                        return info;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 注册表不可用：返回 null，由调用方记录警告
        }

        return null;
    }

    /// <summary>反射调用官方 SettingsWindowNew.CoreNavigate(SettingsPageInfo, Uri) 完成导航。</summary>
    private bool TryCoreNavigate(Window window, SettingsPageInfo info)
    {
        try
        {
            var method = window.GetType().GetMethod(
                "CoreNavigate",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(SettingsPageInfo), typeof(Uri) },
                null);
            if (method == null)
            {
                return false;
            }

            method.Invoke(window, new object?[] { info, null });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "调用 CoreNavigate 失败，尝试备用导航方式。");
            return false;
        }
    }

    /// <summary>在导航项（含分组子项）中递归查找指定标题的页面（兜底用）。</summary>
    private static NavigationViewItem? FindNavigationItemByHeader(
        System.Collections.IEnumerable items,
        string header)
    {
        foreach (var itemObject in items)
        {
            if (itemObject is not NavigationViewItem item)
            {
                continue;
            }

            if (string.Equals(
                    NormalizeEllipsis(HeaderTextOf(item)),
                    NormalizeEllipsis(header),
                    StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var child = FindNavigationItemByHeader(item.MenuItems, header);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>在导航项（含分组子项）中递归查找指定设置页。</summary>
    private static NavigationViewItem? FindNavigationItem(
        System.Collections.IEnumerable items,
        string pageId)
    {
        foreach (var itemObject in items)
        {
            if (itemObject is not NavigationViewItem item)
            {
                continue;
            }

            if (item.Tag is SettingsPageInfo info &&
                string.Equals(info.Id, pageId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            var child = FindNavigationItem(item.MenuItems, pageId);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// 递归应用单个导航项（或分组容器的全部子项）的页面权限。
    /// </summary>
    private void ApplyNavigationItemRecursive(NavigationView nav, NavigationViewItem item)
    {
        if (_manager == null)
        {
            return;
        }

        // 分组容器：先递归处理全部子项
        if (item.MenuItems.Count > 0)
        {
            foreach (var childObject in item.MenuItems.ToList())
            {
                if (childObject is NavigationViewItem child)
                {
                    ApplyNavigationItemRecursive(nav, child);
                }
            }

            // 大页规则：Hidden → 整组隐藏；Disabled → 整组置灰；Operable → 恢复
            var groupId = (item.Tag as SettingsPageInfo)?.GroupId;
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                var groupState = _manager.GetGroupState(groupId);
                if (groupState == PermissionState.Hidden)
                {
                    HideNavigationItem(nav, item);
                    return;
                }

                if (groupState == PermissionState.Disabled)
                {
                    DisableNavigationItem(nav, item);
                    return;
                }

                RestoreNavigationItem(nav, item);
            }

            // 兜底：无大页规则时，组内子项全部被隐藏 → 隐藏空壳分组
            var children = item.MenuItems.OfType<NavigationViewItem>().ToList();
            if (children.Count > 0 && children.All(c => _navItemsHiddenByUs.Contains(c)))
            {
                HideNavigationItem(nav, item);
            }
            else
            {
                RestoreNavigationItem(nav, item);
            }

            return;
        }

        if (item.Tag is not SettingsPageInfo info)
        {
            return;
        }

        var state = _manager.GetPageState(info.Id);

        // 级联：插件页被禁用/隐藏时，所有“未在配置中显式列出”的页面整体跟随。
        if (state == PermissionState.Operable &&
            !_manager.IsPageExplicitlyConfigured(info.Id) &&
            !string.Equals(info.Id, PluginsPageId, StringComparison.OrdinalIgnoreCase))
        {
            var pluginsPageState = _manager.GetPageState(PluginsPageId);
            if (pluginsPageState != PermissionState.Operable)
            {
                state = pluginsPageState;
            }
        }

        switch (state)
        {
            case PermissionState.Hidden:
                HideNavigationItem(nav, item);
                break;

            case PermissionState.Disabled:
                DisableNavigationItem(nav, item);
                break;

            default:
                RestoreNavigationItem(nav, item);
                break;
        }
    }

    /// <summary>
    /// 将导航项置为“隐藏”：确保不处于置灰状态，隐藏并登记，同时从 MenuItems 物理移除。
    /// </summary>
    private void HideNavigationItem(NavigationView nav, NavigationViewItem item)
    {
        if (_navItemsDisabledByUs.Remove(item) && !item.IsEnabled)
        {
            item.IsEnabled = true;
        }

        if (item.IsVisible)
        {
            item.IsVisible = false;
        }

        _navItemsHiddenByUs.Add(item);

        if (!_removedNavItems.ContainsKey(item))
        {
            var index = nav.MenuItems.IndexOf(item);
            if (index >= 0)
            {
                _removedNavItems[item] = index;
                nav.MenuItems.Remove(item);
            }
        }
    }

    /// <summary>将导航项置为“置灰”：确保可见、不可点击，并登记。</summary>
    private void DisableNavigationItem(NavigationView nav, NavigationViewItem item)
    {
        if (_navItemsHiddenByUs.Remove(item))
        {
            RestoreNavigationItem(nav, item);
        }
        else if (!item.IsVisible)
        {
            item.IsVisible = true;
        }

        if (item.IsEnabled)
        {
            item.IsEnabled = false;
        }

        _navItemsDisabledByUs.Add(item);
    }

    /// <summary>恢复导航项（Operable）：撤销置灰/隐藏，并把曾物理移除的项按原下标插回。</summary>
    private void RestoreNavigationItem(NavigationView nav, NavigationViewItem item)
    {
        if (_navItemsDisabledByUs.Remove(item) && !item.IsEnabled)
        {
            item.IsEnabled = true;
        }

        if (_navItemsHiddenByUs.Remove(item) && !item.IsVisible)
        {
            item.IsVisible = true;
        }

        if (_removedNavItems.Remove(item, out var index) && !nav.MenuItems.Contains(item))
        {
            nav.MenuItems.Insert(Math.Min(index, nav.MenuItems.Count), item);
        }
    }

    /// <summary>
    /// 判断被物理移除的导航项当前是否应恢复（Operable）。
    /// </summary>
    private bool ShouldRestoreRemovedItem(NavigationViewItem item)
    {
        if (_manager == null || item.Tag is not SettingsPageInfo info)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(info.GroupId))
        {
            if (_manager.GetGroupState(info.GroupId) != PermissionState.Operable)
            {
                return false;
            }

            return item.MenuItems.OfType<NavigationViewItem>()
                .All(c => !_navItemsHiddenByUs.Contains(c));
        }

        var state = _manager.GetPageState(info.Id);
        if (state == PermissionState.Operable &&
            !_manager.IsPageExplicitlyConfigured(info.Id) &&
            !string.Equals(info.Id, PluginsPageId, StringComparison.OrdinalIgnoreCase))
        {
            var pluginsPageState = _manager.GetPageState(PluginsPageId);
            if (pluginsPageState != PermissionState.Operable)
            {
                state = pluginsPageState;
            }
        }

        return state == PermissionState.Operable;
    }

    /// <summary>
    /// 控件级拦截（单页范围）：
    ///   1. 收集本页全部候选控件（可视树控件 + Flyout 菜单项，详情面板菜单不参与配置匹配）；
    ///   2. 逐条规则做“唯一命中 / index 保底”筛选；
    ///   3. 命中容器时连同后代一起处理（父锁子）；
    ///   4. 统一应用状态并恢复已解除规则的本插件控件。
    /// </summary>
    private void ApplyPage(SettingsPageBase page)
    {
        if (_manager == null)
        {
            return;
        }

        var pageInfo = GetPageInfo(page);
        if (pageInfo == null)
        {
            return;
        }

        // 页面重新挂载（Frame 缓存复用）→ 布局后重应用一次（事件驱动，非轮询）
        if (_hookedPageBases.Add(page))
        {
            page.AttachedToVisualTree += (_, _) => SchedulePageApply(page);
        }

        // 插件页：挂接列表事件（主流 SelectionChanged + 结构变化保底各一次）
        if (string.Equals(pageInfo.Id, PluginsPageId, StringComparison.OrdinalIgnoreCase))
        {
            HookPluginPage(page);
        }

        var rules = _manager.GetItemsForPage(pageInfo.Id)
            .Where(x => x.IsDisabled)
            .ToList();

        var visualControls = Descendants(page).OfType<Control>()
            .Where(c => c is not MenuItem && c is not ContentPresenter && IsMatchCandidate(c))
            .ToList();
        var menuItems = CollectPageMenuItems(page);
        var allCandidates = visualControls.Concat(menuItems.Cast<Control>()).ToList();

        var matched = new Dictionary<Control, PermissionState>();
        foreach (var rule in rules)
        {
            // 先折叠嵌套命中：父容器与其内部同名子控件视为同一个逻辑目标，
            // 既符合“父锁子”的设计，也避免因模板内部重复文本造成假歧义。
            var candidates = CollapseNested(
                allCandidates.Where(c => Matches(c, rule)).ToList());
            var selected = SelectByUniqueness(rule, pageInfo.Id, candidates);
            if (selected.Count == 0)
            {
                continue;
            }

            var state = rule.IsHidden ? PermissionState.Hidden : PermissionState.Disabled;
            foreach (var control in selected)
            {
                AddWithDescendants(matched, control, state);
            }
        }

        ApplyMatched(page, matched, menuItems);
    }

    /// <summary>
    /// 折叠嵌套命中：若某候选是另一个候选的后代，则只保留上层容器
    /// （父容器命中后本就会级联处理后代）。
    /// </summary>
    private static List<Control> CollapseNested(List<Control> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var lookup = new HashSet<Control>(candidates);
        return candidates.Where(c =>
        {
            for (Visual? current = c.GetVisualParent(); current != null; current = current.GetVisualParent())
            {
                if (current is Control ancestor && lookup.Contains(ancestor))
                {
                    return false;
                }
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// 收集本页所有 Flyout 菜单项（按钮的 MenuFlyout.Items）。
    /// 详情面板菜单（DataContext 链含 PluginInfo）不参与配置匹配，只由写死置灰控制。
    /// </summary>
    private List<MenuItem> CollectPageMenuItems(SettingsPageBase page)
    {
        var result = new List<MenuItem>();
        foreach (var button in Descendants(page).OfType<Button>())
        {
            if (button.Flyout is not MenuFlyout flyout || ResolvePluginInfo(button) != null)
            {
                continue;
            }

            result.AddRange(flyout.Items.OfType<MenuItem>());
        }

        return result;
    }

    /// <summary>
    /// 唯一命中筛选：
    ///   - 精确模式：唯一命中才执行；多处同名默认跳过并记录一次警告；
    ///   - 通配模式：保持“匹配全部”；
    ///   - 配置了 index（1 起）：只取可视树顺序中的第 N 个（对通配同样生效）。
    /// </summary>
    private List<Control> SelectByUniqueness(PermissionItem rule, string pageId, List<Control> candidates)
    {
        if (candidates.Count == 0)
        {
            return new List<Control>();
        }

        if (rule.Index > 0)
        {
            if (rule.Index <= candidates.Count)
            {
                return new List<Control> { candidates[rule.Index - 1] };
            }

            WarnOnce(pageId, rule,
                $"index={rule.Index} 超出命中数 {candidates.Count}，已跳过。");
            return new List<Control>();
        }

        var mode = string.IsNullOrWhiteSpace(rule.MatchMode) ? DefaultMatchMode : rule.MatchMode;
        if (string.Equals(mode, "Wildcard", StringComparison.OrdinalIgnoreCase))
        {
            return candidates;
        }

        if (candidates.Count == 1)
        {
            return candidates;
        }

        WarnOnce(pageId, rule,
            $"页内命中 {candidates.Count} 处（{DescribeCandidates(candidates)}），已跳过以避免误伤。" +
            "请改用唯一的 Name/AutomationId，或配置 index 指定第 N 处。");
        return new List<Control>();
    }

    /// <summary>用于警告日志的候选描述（最多列 5 个）。</summary>
    private static string DescribeCandidates(List<Control> candidates)
    {
        return string.Join("；", candidates.Take(5).Select((c, i) =>
        {
            var header = NormalizeEllipsis(HeaderTextOf(c) ?? "");
            var name = string.IsNullOrWhiteSpace(c.Name) ? "" : $" name={c.Name}";
            return $"#{i + 1} {c.GetType().Name}「{header}」{name}({ScopeOf(c)})";
        }));
    }

    /// <summary>粗略判断控件所在区域，仅用于日志定位。</summary>
    private static string ScopeOf(Control control)
    {
        if (control is MenuItem)
        {
            return "menu";
        }

        for (Visual? current = control; current != null; current = current.GetVisualParent())
        {
            if (current is not Control c)
            {
                continue;
            }

            switch (c.DataContext)
            {
                case PluginInfo:
                    return "detail";
                case { } obj when
                    obj.GetType().GetProperty("Value")?.GetValue(obj) is PluginInfo:
                    return "list";
            }
        }

        return "page";
    }

    /// <summary>
    /// 同一规则的多处命中警告只记录一次（配置变更时清空缓存），
    /// 避免每次页面应用都刷日志。
    /// </summary>
    private void WarnOnce(string pageId, PermissionItem rule, string message)
    {
        var key = pageId + "|" + rule.ControlKey + "|" + rule.MatchMode + "|" + rule.Index;
        if (!_warnedRules.Add(key))
        {
            return;
        }

        _logger.LogWarning("权限规则 {Page}/{Key}（{Mode}）：{Message}",
            pageId, rule.ControlKey, rule.MatchMode, message);
    }

    /// <summary>把命中控件及其全部后代加入目标状态（Hidden 优先于 Disabled）。</summary>
    private static void AddWithDescendants(
        Dictionary<Control, PermissionState> matched,
        Control control,
        PermissionState state)
    {
        Promote(matched, control, state);

        // 菜单项不在页面可视树中，也没有需要级联的设置控件
        if (control is MenuItem)
        {
            return;
        }

        foreach (var descendant in Descendants(control).OfType<Control>())
        {
            Promote(matched, descendant, state);
        }
    }

    private static void Promote(
        Dictionary<Control, PermissionState> matched,
        Control control,
        PermissionState state)
    {
        if (matched.TryGetValue(control, out var existing))
        {
            // Hidden 优先于 Disabled；同为 Disabled 保持
            if (state == PermissionState.Hidden)
            {
                matched[control] = PermissionState.Hidden;
            }

            return;
        }

        matched[control] = state;
    }

    /// <summary>
    /// 应用页面匹配结果并恢复已解除规则的控件。
    /// </summary>
    private void ApplyMatched(
        SettingsPageBase page,
        Dictionary<Control, PermissionState> matched,
        List<MenuItem> menuItems)
    {
        ApplyStates(matched);

        var scope = new HashSet<Control>(Descendants(page).OfType<Control>());
        scope.UnionWith(menuItems.Cast<Control>());
        RestoreTracked(scope, matched);
    }

    /// <summary>
    /// 应用目标状态：菜单项会解除官方 OneWay 绑定，防止加载态/可见性回推。
    /// </summary>
    private void ApplyStates(Dictionary<Control, PermissionState> matched)
    {
        foreach (var pair in matched)
        {
            var control = pair.Key;
            if (pair.Value == PermissionState.Hidden)
            {
                if (control is MenuItem)
                {
                    Unbind(control, Control.IsVisibleProperty);
                }

                if (control.IsVisible)
                {
                    control.IsVisible = false;
                    _controlsHiddenByUs.Add(control);
                }
            }
            else
            {
                // 从隐藏恢复可见（若之前被本插件隐藏）
                if (_controlsHiddenByUs.Remove(control))
                {
                    control.ClearValue(Control.IsVisibleProperty);
                }

                if (control is MenuItem)
                {
                    Unbind(control, Control.IsEnabledProperty);
                }

                if (control.IsEnabled)
                {
                    control.IsEnabled = false;
                    _controlsDisabledByUs.Add(control);
                }
            }
        }
    }

    /// <summary>
    /// 恢复指定范围内曾被本插件处理、但当前不再命中的控件。
    /// </summary>
    private void RestoreTracked(HashSet<Control> scope, Dictionary<Control, PermissionState> matched)
    {
        foreach (var control in _controlsDisabledByUs.Where(scope.Contains).ToList())
        {
            if (matched.ContainsKey(control))
            {
                continue;
            }

            control.ClearValue(Control.IsEnabledProperty);
            _controlsDisabledByUs.Remove(control);
        }

        foreach (var control in _controlsHiddenByUs.Where(scope.Contains).ToList())
        {
            if (matched.ContainsKey(control))
            {
                continue;
            }

            control.ClearValue(Control.IsVisibleProperty);
            _controlsHiddenByUs.Remove(control);
        }
    }

    /// <summary>
    /// “三个点”应用菜单（窗口标题栏，位于设置页之外）：
    ///   - 页面级：显式配置 classisland.appmenu 时控制按钮本身（Disabled 置灰 / Hidden 隐藏）；
    ///   - 控件级：菜单项按 Header 匹配，支持 Disabled / Hidden / index。
    /// 菜单项不在设置页可视树中，直接遍历 Flyout.Items（含子菜单）。
    /// </summary>
    private void ApplyAppMenu(Window window)
    {
        if (_manager == null)
        {
            return;
        }

        var menuButton = _appMenuButton;
        if (menuButton == null || menuButton.GetVisualRoot() == null)
        {
            menuButton = FindAppMenuButton(window);
            _appMenuButton = menuButton;
        }

        if (menuButton?.Flyout is not MenuFlyout flyout)
        {
            return;
        }

        // 只有显式配置了 classisland.appmenu 才控制按钮本身，
        // 避免 defaultPermission=Hidden 时把整个应用菜单误隐藏。
        var buttonState = _manager.IsPageExplicitlyConfigured(AppMenuPageId)
            ? _manager.GetPageState(AppMenuPageId)
            : PermissionState.Operable;
        ApplyAppMenuButtonState(menuButton, buttonState);

        var items = CollectMenuItems(flyout.Items).ToList();
        var rules = _manager.GetItemsForPage(AppMenuPageId)
            .Where(x => x.IsDisabled)
            .ToList();

        var matched = new Dictionary<Control, PermissionState>();
        foreach (var rule in rules)
        {
            var candidates = CollapseNested(
                items.Where(c => Matches(c, rule)).Cast<Control>().ToList());
            var selected = SelectByUniqueness(rule, AppMenuPageId, candidates);
            if (selected.Count == 0)
            {
                continue;
            }

            var state = rule.IsHidden ? PermissionState.Hidden : PermissionState.Disabled;
            foreach (var control in selected)
            {
                Promote(matched, control, state);
            }
        }

        ApplyStates(matched);
        RestoreTracked(new HashSet<Control>(items.Cast<Control>()), matched);

        // 菜单打开前按当前配置再应用一次（事件驱动，防止菜单项重建后状态丢失）
        if (_hookedAppMenuFlyouts.Add(flyout))
        {
            flyout.Opening += (_, _) =>
            {
                if (!BeginApply())
                {
                    return;
                }

                try
                {
                    ApplyAppMenu(window);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "应用菜单权限应用失败。");
                }
                finally
                {
                    EndApply();
                }
            };
        }
    }

    /// <summary>查找“三个点”应用菜单所属的 Button（按标志性菜单项识别）。</summary>
    private static Button? FindAppMenuButton(Window window)
    {
        foreach (var button in Descendants(window).OfType<Button>())
        {
            if (button.Flyout is not MenuFlyout flyout)
            {
                continue;
            }

            foreach (var item in CollectMenuItems(flyout.Items))
            {
                var header = NormalizeEllipsis(HeaderTextOf(item));
                if (AppMenuMarkers.Contains(header, StringComparer.Ordinal))
                {
                    return button;
                }
            }
        }

        return null;
    }

    /// <summary>递归收集菜单项（含子菜单），忽略分隔线等非菜单项。</summary>
    private static IEnumerable<MenuItem> CollectMenuItems(System.Collections.IEnumerable items)
    {
        foreach (var item in items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            yield return menuItem;
            foreach (var child in CollectMenuItems(menuItem.Items))
            {
                yield return child;
            }
        }
    }

    /// <summary>应用“三个点”按钮本身的页面级权限（Operable 时恢复）。</summary>
    private void ApplyAppMenuButtonState(Button button, PermissionState state)
    {
        switch (state)
        {
            case PermissionState.Hidden:
                if (_appMenuButtonDisabledByUs)
                {
                    button.ClearValue(Control.IsEnabledProperty);
                    _appMenuButtonDisabledByUs = false;
                }

                if (button.IsVisible)
                {
                    button.IsVisible = false;
                    _appMenuButtonHiddenByUs = true;
                }

                break;

            case PermissionState.Disabled:
                if (_appMenuButtonHiddenByUs)
                {
                    button.ClearValue(Control.IsVisibleProperty);
                    _appMenuButtonHiddenByUs = false;
                }

                if (button.IsEnabled)
                {
                    button.IsEnabled = false;
                    _appMenuButtonDisabledByUs = true;
                }

                break;

            default:
                if (_appMenuButtonHiddenByUs)
                {
                    button.ClearValue(Control.IsVisibleProperty);
                    _appMenuButtonHiddenByUs = false;
                }

                if (_appMenuButtonDisabledByUs)
                {
                    button.ClearValue(Control.IsEnabledProperty);
                    _appMenuButtonDisabledByUs = false;
                }

                break;
        }
    }

    /// <summary>
    /// 解除官方 OneWay 绑定的本地压制：以“无值绑定”替换原绑定，
    /// 原绑定被替换后不再回推；恢复时 ClearValue 清除本地值。
    /// </summary>
    private static void Unbind(Control control, AvaloniaProperty property)
    {
        control.Bind(property, new Binding { Source = BindingNotification.UnsetValue });
    }

    /// <summary>
    /// 插件页事件挂接（幂等）：
    ///   1. 列表选中变化（主流）→ 对插件页重应用一次；
    ///   2. 列表数据集合变化（唯一保底：插件源重载/筛选重建）→ 合并重应用一次。
    /// </summary>
    private void HookPluginPage(SettingsPageBase page)
    {
        foreach (var list in Descendants(page).OfType<ListBox>())
        {
            if (_hookedPluginLists.Add(list))
            {
                list.SelectionChanged += (_, _) => SchedulePageApply(page);
            }
        }

        // 详情面板宿主（Content 变化 = 切换插件/内容重建）→ 布局后重应用一次。
        // 这是“写死置灰泄漏到其它插件”的关键时序：SelectionChanged 触发时详情
        // 控件可能仍挂着上一个插件的 DataContext，必须等内容/数据上下文更新后再锁。
        foreach (var host in Descendants(page).OfType<Control>()
                     .Where(c => c.GetType().FullName == "ClassIsland.Core.Controls.Emptiable"))
        {
            if (!_hookedDetailHosts.Add(host))
            {
                continue;
            }

            host.PropertyChanged += (_, e) =>
            {
                if (string.Equals(e.Property?.Name, "Content", StringComparison.Ordinal))
                {
                    SchedulePageApply(page);
                }
            };

            // 首次发现时详情内容可能已就绪（订阅晚了）→ 补一次事件驱动应用
            if (host.GetType().GetProperty("Content")?.GetValue(host) != null)
            {
                SchedulePageApply(page);
            }
        }

        object? vm = page.DataContext;
        if (vm != null && vm.GetType().GetProperty("ViewModel") != null)
        {
            vm = vm.GetType().GetProperty("ViewModel")?.GetValue(vm);
        }

        if (vm != null &&
            vm.GetType().GetProperty("MergedPluginsFiltered")?.GetValue(vm) is
                System.Collections.Specialized.INotifyCollectionChanged pluginItems &&
            _hookedPluginItems.Add(pluginItems))
        {
            pluginItems.CollectionChanged += (_, _) => SchedulePageApply(page);
        }
    }

    /// <summary>
    /// 插件页锁定与写死置灰（仅在插件页应用时执行）：
    ///   1. 写死置灰（受自保护总开关 SelfProtection 控制）：本插件详情“更多选项”菜单的
    ///      打包插件 / 打开插件文件夹… / 打开配置文件夹… 三个菜单项强制置灰；
    ///   2. 锁定插件：LockedPlugins 中的插件“已启用”开关与“移除”按钮强制置灰。
    /// </summary>
    private void ApplyPluginLockControls(SettingsPageBase page)
    {
        var pageInfo = GetPageInfo(page);
        if (pageInfo == null ||
            !string.Equals(pageInfo.Id, PluginsPageId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 自保护总开关（v1.5.7）：关闭后不再写死置灰本插件工具菜单、不再隐藏“刷新”
        var selfProtection = IsSelfProtectionEnabled;
        var locked = _manager?.CurrentConfig?.LockedPlugins;
        var hasLocked = locked != null && locked.Count > 0;

        foreach (var ctrl in Descendants(page).OfType<Control>())
        {
            var info = ResolvePluginInfo(ctrl);
            var isSelfPlugin = info?.Manifest != null && string.Equals(
                info.Manifest.Id, SelfPluginId, StringComparison.OrdinalIgnoreCase);

            // 1) 插件详情“更多选项”菜单（以“打包插件”项识别；页头“更多选项”不含该项）
            if (ctrl is Button { Flyout: MenuFlyout flyout })
            {
                var menuItems = flyout.Items.OfType<MenuItem>().ToList();
                var isDetailMenu = menuItems.Any(mi =>
                    string.Equals(NormalizeEllipsis(HeaderTextOf(mi)), "打包插件", StringComparison.Ordinal));
                if (isDetailMenu && info?.Manifest != null)
                {
                    foreach (var menuItem in menuItems.Where(IsSelfToolMenuItem))
                    {
                        if (isSelfPlugin && selfProtection)
                        {
                            menuItem.IsEnabled = false;
                        }
                        else
                        {
                            // 详情控件会被不同插件的模板复用：切到其它插件时必须恢复，
                            // 否则写死置灰会“泄漏”到其它插件的详情菜单。
                            menuItem.ClearValue(Control.IsEnabledProperty);
                        }
                    }
                }
            }

            // 2) 锁定插件：启用开关与移除按钮置灰
            if (info?.Manifest == null)
            {
                continue;
            }

            var isEnabledCheck = ctrl is CheckBox cb && IsPluginEnabledCheck(cb);
            var isRemoveButton = ctrl is Button btn && ContainsButtonText(btn, TextRemove);
            if (!isEnabledCheck && !isRemoveButton)
            {
                continue;
            }

            // 自保护开启时始终锁定本插件自身；关闭时连同 lockedPlugins 对本插件的锁定一并解除。
            // 其它插件只按 lockedPlugins 列表锁定，不受自保护开关影响。
            var shouldLock = isSelfPlugin
                ? selfProtection
                : hasLocked && locked!.Contains(info.Manifest.Id, StringComparer.OrdinalIgnoreCase);
            if (shouldLock)
            {
                ctrl.IsEnabled = false;
            }
            else
            {
                ctrl.ClearValue(Control.IsEnabledProperty);
            }
        }

        // 3) 自保护：隐藏插件页头部的“刷新”入口（总开关关闭时恢复）
        if (selfProtection)
        {
            HidePluginPageRefreshControl(page);
        }
        else
        {
            RestorePluginPageRefreshControl();
        }
    }

    /// <summary>自保护总开关：未加载配置时按默认关闭处理。</summary>
    private bool IsSelfProtectionEnabled => _manager?.CurrentConfig?.SelfProtection ?? false;

    /// <summary>关闭自保护时恢复被写死隐藏的“刷新”入口。</summary>
    private void RestorePluginPageRefreshControl()
    {
        foreach (var target in _refreshControlsHiddenByUs)
        {
            // 若该项同时被权限规则显式隐藏，则保持隐藏，避免恢复动作覆盖管控策略
            if (!_controlsHiddenByUs.Contains(target))
            {
                target.IsVisible = true;
            }
        }

        _refreshControlsHiddenByUs.Clear();
    }

    /// <summary>
    /// 写死隐藏插件页头部的“刷新”入口（受自保护总开关 SelfProtection 控制）。
    /// 识别：页面头部区域（排除插件列表行与详情面板）中文本为“刷新”的控件，
    /// 向上找到可点击的 Button 宿主后整体隐藏（图标 + 文字一起消失）。
    /// </summary>
    private void HidePluginPageRefreshControl(SettingsPageBase page)
    {
        var targets = new HashSet<Control>();
        foreach (var control in Descendants(page).OfType<Control>())
        {
            // 只看页头：列表行与插件详情里的“刷新”字样（插件自述内容等）不处理
            if (ResolvePluginInfo(control) != null || IsInsideListItem(control))
            {
                continue;
            }

            var text = HeaderTextOf(control);
            if (!string.Equals(text, "刷新", StringComparison.Ordinal))
            {
                text = control.GetType().GetProperty("Text")?.GetValue(control) as string;
            }

            if (!string.Equals(text, "刷新", StringComparison.Ordinal))
            {
                continue;
            }

            targets.Add(FindButtonAncestor(control) ?? control);
        }

        foreach (var target in targets)
        {
            // 解除可能存在的 IsVisible 绑定，防止官方重新显示
            Unbind(target, Control.IsVisibleProperty);
            if (target.IsVisible)
            {
                target.IsVisible = false;
            }

            _refreshControlsHiddenByUs.Add(target);
        }
    }

    /// <summary>向上查找控件所属的可点击 Button（含 HyperlinkButton）。</summary>
    private static Button? FindButtonAncestor(Control control)
    {
        for (Visual? current = control; current != null; current = current.GetVisualParent())
        {
            if (current is Button button)
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>控件是否位于列表行内（插件列表项）。</summary>
    private static bool IsInsideListItem(Control control)
    {
        for (Visual? current = control; current != null; current = current.GetVisualParent())
        {
            if (current is ListBoxItem)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是否为需要写死置灰的本插件工具菜单项（按 Header 文本识别）。</summary>
    private static bool IsSelfToolMenuItem(MenuItem menuItem)
    {
        var header = NormalizeEllipsis(HeaderTextOf(menuItem));
        return SelfToolMenuHeaders.Contains(header, StringComparer.Ordinal);
    }

    /// <summary>
    /// 判断 CheckBox 是否为插件条目的“已启用”开关：
    ///   - Content 为“已启用”（详情面板带文字形态）→ 是；
    ///   - Content 为空/空白（列表行内仅勾选框形态）→ 是。
    /// 调用前已确认其 DataContext 链为被锁定插件，不会误伤其它开关。
    /// </summary>
    private static bool IsPluginEnabledCheck(CheckBox cb)
    {
        var content = cb.Content as string;
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        return string.Equals(content, TextEnabled, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断按钮内容（或其内部视觉子树）是否包含指定文本。
    /// </summary>
    private static bool ContainsButtonText(Button btn, string text)
    {
        if (btn.Content is string s && string.Equals(s, text, StringComparison.Ordinal))
        {
            return true;
        }

        return Descendants(btn).OfType<TextBlock>()
            .Any(t => string.Equals(t.Text, text, StringComparison.Ordinal));
    }

    /// <summary>把 Unicode 省略号"…"归一化为"..."，使两种写法在匹配时等价。</summary>
    private static string NormalizeEllipsis(string? s)
    {
        return s?.Replace("…", "...") ?? "";
    }

    /// <summary>
    /// 沿可视树向上解析控件所属的 PluginInfo：
    ///   - DataContext 本身是 PluginInfo（详情面板模板）；
    ///   - DataContext 是包装对象且其公开属性 Value 为 PluginInfo（列表行模板）。
    /// </summary>
    private static PluginInfo? ResolvePluginInfo(Control start)
    {
        for (Visual? current = start; current != null; current = current.GetVisualParent())
        {
            if (current is not Control c)
            {
                continue;
            }

            switch (c.DataContext)
            {
                case PluginInfo info:
                    return info;
                case { } obj when
                    obj.GetType().GetProperty("Value")?.GetValue(obj) is PluginInfo inner:
                    return inner;
            }
        }

        return null;
    }

    /// <summary>读取页面的 [SettingsPageInfo] 特性。</summary>
    private static SettingsPageInfo? GetPageInfo(SettingsPageBase page)
    {
        return page.GetType()
            .GetCustomAttributes(typeof(SettingsPageInfo), false)
            .OfType<SettingsPageInfo>()
            .FirstOrDefault();
    }

    /// <summary>
    /// 清理已不在设置窗口可视树中的控件引用（窗口关闭/页面切换后旧控件会卸载）。
    /// MenuItem 不在清理范围——Flyout 关闭时菜单项不在可视树中是常态，
    /// 清出跟踪集合后热更新恢复会失效。
    /// </summary>
    private void PruneStale(Window window)
    {
        var currentControls = new HashSet<Control>(Descendants(window).OfType<Control>());
        _controlsDisabledByUs.RemoveWhere(c => !currentControls.Contains(c) && c is not MenuItem);
        _controlsHiddenByUs.RemoveWhere(c => !currentControls.Contains(c) && c is not MenuItem);
        _navItemsDisabledByUs.RemoveWhere(c => !currentControls.Contains(c));
        _navItemsHiddenByUs.RemoveWhere(c => !currentControls.Contains(c) && !_removedNavItems.ContainsKey(c));
    }

    /// <summary>
    /// 读取控件的 Header 文本（适用于 SettingsExpander、Expander、MenuItem 等带 Header 的控件）。
    /// 无 Header 时兼容 TabStripItem / CheckBox 等以 Content 承载文本的控件。
    /// </summary>
    private static string? HeaderTextOf(Control control)
    {
        var property = control.GetType().GetProperty("Header");
        if (property == null)
        {
            var content = control.GetType().GetProperty("Content");
            if (content == null)
            {
                return null;
            }

            try
            {
                return content.GetValue(control)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        try
        {
            return property.GetValue(control)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按权限项的匹配模式判断控件自身是否命中。
    /// </summary>
    private static bool Matches(Control control, PermissionItem item)
    {
        var key = item.ControlKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var mode = string.IsNullOrWhiteSpace(item.MatchMode) ? DefaultMatchMode : item.MatchMode;
        switch (mode.ToLowerInvariant())
        {
            case "name":
                return string.Equals(control.Name, key, StringComparison.OrdinalIgnoreCase);

            case "automationid":
                var automationId = control.GetValue(AutomationProperties.AutomationIdProperty) as string;
                return string.Equals(automationId, key, StringComparison.OrdinalIgnoreCase);

            case "headertext":
                return string.Equals(
                    NormalizeEllipsis(HeaderTextOf(control)),
                    NormalizeEllipsis(key),
                    StringComparison.OrdinalIgnoreCase);

            case "wildcard":
                var pattern = "^" + Regex.Escape(NormalizeEllipsis(key)).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var name = control.Name ?? "";
                var autoId = control.GetValue(AutomationProperties.AutomationIdProperty) as string ?? "";
                var header = NormalizeEllipsis(HeaderTextOf(control) ?? "");
                return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)
                    || Regex.IsMatch(autoId, pattern, RegexOptions.IgnoreCase)
                    || Regex.IsMatch(header, pattern, RegexOptions.IgnoreCase);

            default:
                return false;
        }
    }

    /// <summary>
    /// 判断控件是否值得作为规则候选：
    ///   - 带 Header 的容器（SettingsExpander / Expander 等）——按标题整块锁定；
    ///   - 可交互控件（TemplatedControl：按钮/开关/输入框/下拉框/TabStripItem 等）。
    /// 排除模板内部的 ContentPresenter 与纯展示控件（TextBlock 等），
    /// 避免同一段文字被模板内的多个视觉节点重复命中。
    /// </summary>
    private static bool IsMatchCandidate(Control control)
    {
        if (control.GetType().GetProperty("Header") != null || control is TemplatedControl)
        {
            return true;
        }

        // 兼容以纯文本 Content 承载标题的非交互容器（模板内部的 ContentPresenter 已在上层排除）
        var contentProperty = control.GetType().GetProperty("Content");
        return contentProperty?.GetValue(control) is string;
    }

    /// <summary>
    /// 深度优先遍历可视树的后代（不含根自身），严格按可视顺序（父 → 子，兄弟从左到右）。
    /// </summary>
    private static IEnumerable<Visual> Descendants(Visual root)
    {
        var stack = new Stack<Visual>();
        stack.Push(root);
        var isRoot = true;
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!isRoot)
            {
                yield return current;
            }
            else
            {
                isRoot = false;
            }

            var children = current.GetVisualChildren().ToList();
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    /// <summary>
    /// 在可视树中查找第一个满足条件的后代。
    /// </summary>
    private static Control? FindDescendant(Visual root, Func<Visual, bool> predicate)
    {
        foreach (var visual in Descendants(root))
        {
            if (predicate(visual))
            {
                return visual as Control;
            }
        }

        return null;
    }
}
