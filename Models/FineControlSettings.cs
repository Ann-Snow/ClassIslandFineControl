namespace ClassIslandFineControl.Models;

/// <summary>
/// 插件引导配置（保存于插件配置目录下的 Config.json）。
/// v1.1.6 起插件只支持“集控清单驱动”一种配置来源：
///   客户端加入官方集控 → 插件从客户端集控配置读取清单 URL →
///   拉取服务器原始 manifest.json → 按版本号下载 permissions.json 并应用。
/// 不再支持本地文件 / 手动远程 URL 等传统模式（已移除）。
/// </summary>
public class FineControlSettings
{
    /// <summary>
    /// 集控清单刷新间隔（秒）。默认 300 秒（5 分钟）。
    /// 注意（v1.1.10.3 起）：自动周期拉取已移除，本字段不再被使用，
    /// 仅保留以兼容旧版 Config.json（避免反序列化报错）。
    /// 配置更新只通过启动时的一次自动拉取（版本号驱动）与状态页“更新”按钮
    /// （强制拉取）触发。
    /// </summary>
    public int RefreshIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// 拉取清单/权限配置时附加的请求头名称（可选）。留空则不附加。
    /// 示例：Authorization
    /// </summary>
    public string HttpAuthHeaderName { get; set; } = "";

    /// <summary>
    /// 拉取清单/权限配置时附加的请求头值（可选）。
    /// 示例：Bearer xxxxxx 或自定义 Token
    /// </summary>
    public string HttpAuthHeaderValue { get; set; } = "";

    /// <summary>
    /// 是否启用“设置值级锁定”：对配置中带 SettingsProperty 的不可操作项
    /// （Disabled 或 Hidden），通过 SettingsService.AddSettingsOverlay
    /// 把对应设置属性强制锁定为当前值，即使通过其它途径（自动化动作、快捷键等）
    /// 修改也会被弹回。找不到 SettingsService 时自动降级为仅 UI 拦截。
    /// </summary>
    public bool EnableValueLock { get; set; } = true;

    /// <summary>
    /// 是否启用“集控清单驱动”模式（v1.1.0 引入，v1.1.6 起为唯一来源，默认开启）。
    /// 开启后插件每次刷新：
    ///   1. 从客户端集控配置（%LocalAppData%\ClassIsland\Management\Settings.json，
    ///      兜底应用目录 ManagementPreset.json）读取 ManifestUrlTemplate；
    ///   2. 直接 GET 服务器上的原始 manifest.json（含自定义字段
    ///      <see cref="ManifestSourceName"/>，与官方 ReVersionString 同构
    ///      { "Version": int, "Value": "权限配置文件URL" }）；
    ///   3. 按版本号更新权限配置：清单 Version 大于上次应用的版本 → 拉取并应用；
    ///      版本号不变 → 跳过（与官方集控版本号机制一致）。
    /// 关闭此开关后插件不会加载任何权限配置（状态页会给出明确提示）。
    /// </summary>
    public bool EnableManifestSource { get; set; } = true;

    /// <summary>
    /// 集控清单中本插件权限源的字段名（默认 "FineControlPermissionSource"）。
    /// 管理员需要在 manifest.json 顶层新增同名字段，形如：
    /// "FineControlPermissionSource": {
    ///   "Version": 1,
    ///   "Value": "https://gitee.com/你的用户名/classisland-mgmt-cfg/raw/master/permissions.json"
    /// }
    /// </summary>
    public string ManifestSourceName { get; set; } = "FineControlPermissionSource";

    /// <summary>
    /// 设置窗口打开时默认导航到的设置页 Id（v1.5.1 起）。
    /// 默认 "management"（集控页）；留空则不强制跳转，保留官方默认/上次页面。
    /// 仅当目标页存在且未被权限禁用/隐藏时才跳转，避免违背管控策略。
    /// </summary>
    public string DefaultSettingsPageId { get; set; } = "management";
}
