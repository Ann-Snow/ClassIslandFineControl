using System;
using System.Text.Json.Serialization;

namespace ClassIslandFineControl.Models;

/// <summary>
/// 权限动作状态。
///   Operable —— 可操作（不拦截）；
///   Disabled —— 禁用（置灰，不可点击/修改，但可见）；
///   Hidden   —— 隐藏（彻底隐藏，设置项/设置页不出现在界面中）。
/// </summary>
public enum PermissionState
{
    Operable,
    Disabled,
    Hidden
}

/// <summary>
/// 集控权限配置文件模型（对应 JSON 配置文件本身）。
/// 由管理员维护，托管在集控清单（manifest.json）指定的 URL 上，
/// 客户端通过清单版本号自动拉取更新。
/// </summary>
public class PermissionConfig
{
    /// <summary>配置格式版本号。当前为 1。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 默认权限：Operable（可操作）/ Disabled（禁用）/ Hidden（隐藏）。
    /// 未在 SettingsPages 中单独列出的设置页，按此默认值处理。
    /// 注意：控件级权限必须显式列出，不受此默认值影响。
    /// </summary>
    public string DefaultPermission { get; set; } = "Operable";

    /// <summary>页面级权限规则列表（禁用/隐藏整个设置页）。</summary>
    public List<PermissionPageItem> SettingsPages { get; set; } = new();

    /// <summary>分组级（大页）权限规则列表（禁用/隐藏整个设置分组）。</summary>
    public List<PermissionGroupItem> SettingsGroups { get; set; } = new();

    /// <summary>控件级权限规则列表（禁用/隐藏设置页内某个具体设置项）。</summary>
    public List<PermissionItem> SettingsItems { get; set; } = new();

    // ===================== 树状结构（v1.1.8 起，推荐格式） =====================
    // 按设置窗口导航结构组织的三级权限树：
    //   Groups —— 大页（导航分组，如“通用”“主界面”），组内挂小页（pages），
    //             小页内挂细节（items，页内设置项控件）；
    //   Pages  —— 无分组的独立小页（如“提醒”“天气”“更新”“插件”“主题”“关于”等）。
    // 每个节点自带权限；大页权限继承到组内所有小页（Disabled/Hidden → 整组失效）。
    // 旧平铺格式（SettingsGroups/SettingsPages/SettingsItems）仍兼容解析。

    /// <summary>树状：大页（分组）节点列表。每项含分组权限与组内小页。</summary>
    public List<PermissionGroupNode> Groups { get; set; } = new();

    /// <summary>树状：无分组独立小页节点列表（提醒/天气/更新/插件/主题/关于等）。</summary>
    public List<PermissionPageNode> Pages { get; set; } = new();

    /// <summary>默认权限是否为“可操作”。</summary>
    [JsonIgnore]
    public bool IsOperableDefault =>
        string.Equals(DefaultPermission, "Operable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 锁定插件 Id 列表（v1.1.10）：列出的插件在“插件”页中的启用开关会被禁用，
    /// 学生无法取消启用这些插件（防止关闭管控插件本身导致管理失效）。
    /// 值为插件 manifest 中的 id（如本插件 "cn.finecontrol.permission"）。
    /// </summary>
    public List<string> LockedPlugins { get; set; } = new();

    /// <summary>
    /// 自保护总开关（默认 false，v1.5.14 起）。
    /// 开启时插件执行自保护动作：
    ///   - 置灰本插件详情“更多选项”中的 打包插件 / 打开插件文件夹… / 打开配置文件夹…；
    ///   - 隐藏插件页头部的“刷新”入口；
    ///   - 锁定本插件自身的“已启用/移除”；
    ///   - 拦截并删除 .disabled / .uninstall 标记文件。
    /// 默认关闭，避免在非受控环境下限制用户正常禁用/卸载；集控场景由管理员显式开启。
    /// <see cref="LockedPlugins"/> 属于显式配置，不受该开关影响。
    /// </summary>
    public bool SelfProtection { get; set; } = false;
}

/// <summary>
/// 权限字符串解析器：把配置中的 "Operable"/"Disabled"/"Hidden" 解析为
/// <see cref="PermissionState"/>；无法识别时按 Operable 处理。
/// 被权限配置各节点类（分组/页面/控件，树状与平铺）共用。
/// </summary>
internal static class PermissionStateParser
{
    public static PermissionState Parse(string permission)
    {
        if (string.Equals(permission, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return PermissionState.Disabled;
        }

        if (string.Equals(permission, "Hidden", StringComparison.OrdinalIgnoreCase))
        {
            return PermissionState.Hidden;
        }

        return PermissionState.Operable;
    }
}

/// <summary>
/// 分组级（大页）权限：针对设置窗口侧边导航中的整个分组（如“通用”“主界面”）。
/// 大页被禁用/隐藏时，组内所有设置页（小页）整体失效：
///   Disabled —— 整个分组置灰（可见但打不开，组内所有小页不可进入）；
///   Hidden   —— 整个分组从导航中消失（组内所有小页不可见）；
///   Operable —— 组内小页按各自 SettingsPages 规则独立控制。
/// 注意：官方内置分组取自 ClassIsland 2.1.0.1 源码
/// （ClassIsland/App.Services.xaml.cs 的 AddSettingsPageGroup 与
/// Views/SettingPages/*.axaml.cs 的 [Group(...)]）：
///   classisland.general   = “通用”分组（通用/时钟/存储/隐私/翻新与迎新/高级）
///   classisland.mainwindow= “主界面”分组（主界面/外观/组件）
/// 未在 SettingsGroups 中列出的分组按 Operable 处理。
/// </summary>
public class PermissionGroupItem
{
    /// <summary>分组 Id（必填）。如 classisland.general（通用）、classisland.mainwindow（主界面）。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>权限：Operable（可操作）/ Disabled（禁用，置灰）/ Hidden（隐藏，不显示）。</summary>
    public string Permission { get; set; } = "Disabled";

    /// <summary>备注（可选，仅用于说明，不影响逻辑）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态（解析 Permission 字段）。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);

    /// <summary>是否处于不可操作状态（Disabled 或 Hidden 均视为不可操作）。</summary>
    [JsonIgnore]
    public bool IsDisabled => State != PermissionState.Operable;

    /// <summary>是否隐藏（Hidden）。</summary>
    [JsonIgnore]
    public bool IsHidden => State == PermissionState.Hidden;
}

/// <summary>
/// 页面级权限：针对某个设置页整体（如“窗口”“天气”“更新”）。
/// 被禁用的页面其侧边导航项被置灰，用户无法打开该页面；
/// 被隐藏的页面其侧边导航项直接不显示。
/// </summary>
public class PermissionPageItem
{
    /// <summary>
    /// 设置页 Id（必填）。ClassIsland 内置设置页 Id（取自 ClassIsland 源码
    /// Views/SettingPages/*.axaml.cs 的 [SettingsPageInfo]）：
    ///   general（通用）、window（窗口）、clock（时钟）、appearance（外观）、
    ///   notification（提醒）、weather（天气）、update（更新）、automation（自动化）、
    ///   storage（存储）、privacy（隐私）、classisland.plugins（插件）、
    ///   classisland.themes（主题）、management（集控）、management.credentials、
    ///   management.policy、advanced（高级）、about（关于）、debug（调试）等。
    /// 插件注册的自定义页面 Id 也可使用（如本插件的 cn.finecontrol.status）。
    /// </summary>
    public string PageId { get; set; } = "";

    /// <summary>权限：Operable（可操作）/ Disabled（禁用，置灰）/ Hidden（隐藏，不显示）。</summary>
    public string Permission { get; set; } = "Disabled";

    /// <summary>备注（可选，仅用于说明，不影响逻辑）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态（解析 Permission 字段）。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);

    /// <summary>是否处于不可操作状态（Disabled 或 Hidden 均视为不可操作）。</summary>
    [JsonIgnore]
    public bool IsDisabled => State != PermissionState.Operable;

    /// <summary>是否隐藏（Hidden）。</summary>
    [JsonIgnore]
    public bool IsHidden => State == PermissionState.Hidden;
}

/// <summary>
/// 控件级权限：针对设置页内某个具体设置项。
/// </summary>
public class PermissionItem
{
    /// <summary>
    /// 所属设置页 Id（必填）。控件级匹配只在“当前打开的页”内进行。
    /// </summary>
    public string PageId { get; set; } = "";

    /// <summary>
    /// 控件匹配键（必填）。与 MatchMode 配合定位目标控件：
    ///   HeaderText  —— 控件的 Header 文本（如设置项标题“开机自启”）
    ///   Name        —— 控件的 x:Name
    ///   AutomationId—— 控件的自动化 Id（AutomationProperties.AutomationId）
    ///   Wildcard    —— 通配匹配（支持 * 与 ?，作用于上述三类值）
    /// </summary>
    public string ControlKey { get; set; } = "";

    /// <summary>
    /// 匹配模式：HeaderText / Name / AutomationId / Wildcard。
    /// 留空时默认 HeaderText。
    /// 精确模式（HeaderText / Name / AutomationId）在页内只有唯一命中时才执行，
    /// 多处同名默认跳过；Wildcard 保持匹配全部。
    /// </summary>
    public string MatchMode { get; set; } = "HeaderText";

    /// <summary>
    /// 保底：同名多实例时按可视树顺序取第 N 个（1 起）。
    /// 0/未配置 = 不指定（精确模式要求唯一命中，Wildcard 匹配全部）。
    /// 仅在精确匹配确实多处同名时使用，避免误伤。
    /// </summary>
    public int Index { get; set; }

    /// <summary>权限：Operable（可操作）/ Disabled（禁用，置灰）/ Hidden（隐藏，不显示）。</summary>
    public string Permission { get; set; } = "Disabled";

    /// <summary>
    /// 可选：对应 ClassIsland Settings 模型中的属性名（如 IsSplashEnabled、
    /// IsReportingEnabled、IsSentryEnabled、ShowDate、HideOnClass、
    /// IsCountdownEnabled、IsClassPrepareNotificationEnabled 等）。
    /// 填写后，插件会对该属性启用“值级锁定”：禁止通过任何途径修改
    /// （Disabled 与 Hidden 均生效）。
    /// 注意：只有 Settings 模型中的属性才能值级锁定；不在模型中的设置项
    /// （如“开机自启”，实际由系统服务托管）仅做 UI 拦截。
    /// </summary>
    public string? SettingsProperty { get; set; }

    /// <summary>备注（可选）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态（解析 Permission 字段）。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);

    /// <summary>是否处于不可操作状态（Disabled 或 Hidden 均视为不可操作）。</summary>
    [JsonIgnore]
    public bool IsDisabled => State != PermissionState.Operable;

    /// <summary>是否隐藏（Hidden）。</summary>
    [JsonIgnore]
    public bool IsHidden => State == PermissionState.Hidden;
}

// ===================== 树状结构模型（v1.1.8） =====================

/// <summary>
/// 树状：大页（导航分组）节点。对应设置窗口左侧导航中的分组
/// （如“通用”“主界面”）。大页权限继承到组内所有小页：
///   Disabled —— 整组置灰，组内小页全部不可进入；
///   Hidden   —— 整组隐藏，组内小页全部不可见；
///   Operable —— 组内小页按各自权限独立控制。
/// </summary>
public class PermissionGroupNode
{
    /// <summary>分组 Id（必填）：classisland.general（通用）/ classisland.mainwindow（主界面）。</summary>
    public string Id { get; set; } = "";

    /// <summary>分组显示名称（可选，仅便于阅读，不影响逻辑）。如“通用”“主界面”。</summary>
    public string? Name { get; set; }

    /// <summary>分组权限：Operable（可操作）/ Disabled（禁用，置灰）/ Hidden（隐藏）。</summary>
    public string Permission { get; set; } = "Operable";

    /// <summary>组内小页节点列表（每项一个设置页，含该页权限与页内细节控件权限）。</summary>
    public List<PermissionPageNode> Pages { get; set; } = new();

    /// <summary>备注（可选）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);
}

/// <summary>
/// 树状：小页（设置页）节点。对应左侧导航中的一个设置页
/// （分组内的小页，或无分组的独立页）。
/// </summary>
public class PermissionPageNode
{
    /// <summary>设置页 Id（必填）。如 general（基本）、clock（时钟）、update（更新）等。</summary>
    public string Id { get; set; } = "";

    /// <summary>设置页显示名称（可选，仅便于阅读）。如“基本”“时钟”“更新”。</summary>
    public string? Name { get; set; }

    /// <summary>小页权限：Operable（可操作）/ Disabled（禁用，置灰）/ Hidden（隐藏）。</summary>
    public string Permission { get; set; } = "Operable";

    /// <summary>页内细节（控件）权限列表：本页内具体设置项的权限。</summary>
    public List<PermissionItemNode> Items { get; set; } = new();

    /// <summary>备注（可选）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);
}

/// <summary>
/// 树状：细节（控件）权限节点。对应设置页内某个具体设置项
/// （开关、按钮、下拉框、输入框等）。
/// </summary>
public class PermissionItemNode
{
    /// <summary>控件匹配键（必填）。与 MatchMode 配合定位目标控件。</summary>
    public string Key { get; set; } = "";

    /// <summary>匹配模式：HeaderText（默认）/ Name / AutomationId / Wildcard。</summary>
    public string MatchMode { get; set; } = "HeaderText";

    /// <summary>
    /// 保底：同名多实例时按可视树顺序取第 N 个（1 起）。
    /// 0/未配置 = 不指定（精确模式要求唯一命中，Wildcard 匹配全部）。
    /// </summary>
    public int Index { get; set; }

    /// <summary>控件权限：Operable（可操作）/ Disabled（置灰）/ Hidden（隐藏）。</summary>
    public string Permission { get; set; } = "Disabled";

    /// <summary>可选：Settings 模型属性名（值级锁定，Disabled/Hidden 均生效）。</summary>
    public string? SettingsProperty { get; set; }

    /// <summary>备注（可选）。</summary>
    public string? Remark { get; set; }

    /// <summary>权限状态。</summary>
    [JsonIgnore]
    public PermissionState State => PermissionStateParser.Parse(Permission);
}
