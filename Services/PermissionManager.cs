using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Shared.Helpers;
using ClassIslandFineControl.Models;
using Microsoft.Extensions.Logging;

namespace ClassIslandFineControl.Services;

/// <summary>
/// 权限管理器。
/// 职责（v1.1.6 起仅清单驱动）：
///   1. 读取插件引导配置（插件配置目录/Config.json）；
///   2. 从客户端集控配置取清单 URL，拉取服务器原始 manifest.json，
///      按版本号下载 permissions.json 并应用（不再支持本地/手动远程传统模式）；
///   3. 按配置的刷新间隔定时重载，支持配置热更新；
///   4. 对外提供页面级 / 控件级权限查询。
/// </summary>
public class PermissionManager
{
    /// <summary>
    /// 本插件在 SettingsService 设置叠层（SettingsOverlays）中使用的固定 Guid，
    /// 用于“值级锁定”（见 SettingsValueGuard）。
    /// </summary>
    public static readonly Guid PluginGuid = new("8f2d5c9a-3b6e-4c1f-9a7d-2e4b8f6c0a31");

    /// <summary>本插件 Id（与 manifest.yml 一致），用于拼装 cipx 安装包文件名。</summary>
    public const string PluginId = "cn.finecontrol.permission";

    /// <summary>
    /// 权限配置文件固定文件名（v1.3.21 起集控源 Value 指向文件夹时拼接）。
    /// 客户端启动时无条件拉取并应用，配置更新不再靠版本号。
    /// </summary>
    private const string PermissionConfigFileName = "permissions.json";

    /// <summary>
    /// 插件安装包文件名模板：{0} = 集控清单 Version（插件版本号）。
    /// 上传到集控目录的文件须按此命名（v1.4.1 适配用户命名规则），
    /// 如 ClassIslandFineControl-v1.4.1.cipx。
    /// </summary>
    private const string PluginPackageFileNameTemplate = "ClassIslandFineControl-v{0}.cipx";

    /// <summary>本插件 manifest.yml 文件名（读取自身版本用）。</summary>
    private const string PluginManifestFileName = "manifest.yml";

    /// <summary>
    /// 权限配置 JSON 反序列化选项：大小写不敏感、允许注释与尾随逗号，
    /// 方便管理员手写配置文件（可在 JSON 里写 // 或 /* */ 注释）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>本地缓存文件（ManifestVersions.json）写入选项：缩进美化。</summary>
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<PermissionManager> _logger;
    private readonly HttpClient _httpClient = new();
    private string _configFolder = "";
    private volatile bool _refreshing;

    public PermissionManager(ILogger<PermissionManager> logger)
    {
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        // 关键：.NET HttpClient 默认不携带 User-Agent，部分静态托管（Gitee/GitHub raw、CDN）
        // 会拒绝无 UA 的请求并返回 403/429。这里固定携带插件 UA，避免拉取失败。
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("ClassIslandFineControl/1.1.0");
    }

    /// <summary>插件引导配置（Config.json）。</summary>
    public FineControlSettings Settings { get; private set; } = new();

    /// <summary>当前生效的权限配置；尚未成功加载时为 null（此时全部设置项保持可操作）。</summary>
    public PermissionConfig? CurrentConfig { get; private set; }

    /// <summary>是否正在执行一次配置刷新（启动安全锁据此判断是否仍处于“配置未就绪”阶段）。</summary>
    public bool IsRefreshing => _refreshing;

    /// <summary>最近一次成功加载配置的时间。</summary>
    public DateTime LastLoadTime { get; private set; }

    /// <summary>最近一次成功加载配置的来源（Manifest:URL）。</summary>
    public string LastSource { get; private set; } = "";

    /// <summary>最近一次加载/拉取的错误信息；无错误时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 客户端集控配置路径（客户端加入集控后由官方保存）：
    /// %LocalAppData%\ClassIsland\Management\Settings.json
    /// 其中包含 ManifestUrlTemplate（集控清单 URL 模板）与 IsManagementEnabled。
    /// 插件从这里拿到清单 URL，再直接拉取服务器上的原始 manifest.json。
    /// </summary>
    public static string ManagementSettingsPath =>
        Path.Combine(CommonDirectories.AppDataFolderPath, "Management", "Settings.json");

    /// <summary>
    /// 集控预设文件路径（应用目录）：&lt;ClassIsland根目录&gt;\ManagementPreset.json
    /// 未加入集控时，用户手动放置该文件即可让客户端自动预选集控配置；
    /// 插件将其作为 Settings.json 不存在时的兜底来源。
    /// </summary>
    public static string ManagementPresetPath =>
        Path.Combine(CommonDirectories.AppRootFolderPath, "ManagementPreset.json");

    /// <summary>
    /// 清单源应用版本缓存文件（插件配置目录下），记录上次成功应用的自定义源版本号，
    /// 用于“版本号驱动”更新：只有清单中的 Version 大于该值时才会重新拉取。
    /// </summary>
    public string ManifestVersionsPath => Path.Combine(_configFolder, "ManifestVersions.json");

    /// <summary>集控清单中是否已配置本插件的自定义源（供状态页展示）。</summary>
    public bool ManifestSourceConfigured { get; private set; }

    /// <summary>最近一次从集控清单读到的源版本号（数值解析失败为 0，保留旧字段兼容）。</summary>
    public int LastManifestVersion { get; private set; }

    /// <summary>
    /// 最近一次从集控清单读到的源版本号原文（v1.3.21 起为插件版本号字符串，
    /// 如 "1.3.21"；旧配置为数字时也返回其字符串形式）。
    /// </summary>
    public string LastManifestSourceVersion { get; private set; } = "";

    /// <summary>
    /// 是否有已下载待生效的插件更新包（下载成功后置 true，重启后官方自动安装）。
    /// 供状态页展示“重启后生效”提示；静默更新不弹窗不打扰。
    /// </summary>
    public bool PluginUpdatePending { get; private set; }

    /// <summary>本地插件版本（Plugins/{PluginId}/manifest.yml 的 version；读取失败为空）。</summary>
    public string LocalPluginVersion { get; private set; } = "";

    /// <summary>最近一次成功拉取集控清单的时间（清单最后更新时间）。</summary>
    public DateTime LastManifestFetchedAt { get; private set; }

    /// <summary>集控清单 URL（清单最后更新来源）。</summary>
    public string LastManifestUrl { get; private set; } = "";

    /// <summary>插件更新包下载完成时间（非生效时间）；无待安装更新时为 null。</summary>
    public DateTime? PluginUpdateDownloadedAt => _updateState?.DownloadedAt;

    /// <summary>插件更新包来源 URL；无待安装更新时为 null。</summary>
    public string? PluginUpdateSourceUrl => _updateState?.SourceUrl;

    /// <summary>插件更新最终生效时间（重启后检测到新版本已安装）；未生效为 null。</summary>
    public DateTime? PluginUpdateAppliedAt => _updateState?.AppliedAt;

    /// <summary>插件最后更新检查时间（最近一次执行插件更新检查，无论是否更新都记录）。</summary>
    public DateTime? PluginUpdateCheckedAt => _updateState?.LastCheckedAt;

    /// <summary>最近一次执行集控清单检查的时间（无论成功失败都记录）。</summary>
    public DateTime LastManifestCheckedAt { get; private set; }

    /// <summary>插件更新状态（持久化到插件配置目录 UpdateState.json，跨重启记录下载/生效时间）。</summary>
    private PluginUpdateState? _updateState;

    /// <summary>插件更新状态持久化模型。</summary>
    private class PluginUpdateState
    {
        public string? PendingVersion { get; set; }
        public DateTime? DownloadedAt { get; set; }
        public string? SourceUrl { get; set; }
        public DateTime? AppliedAt { get; set; }
        public DateTime? LastCheckedAt { get; set; }
    }

    /// <summary>配置发生变化时触发（供状态页、UI 拦截器、值级守卫刷新）。</summary>
    public event Action? ConfigChanged;

    /// <summary>
    /// 一次刷新流程结束（无论成功/失败/未配置）时触发。
    /// 供 UI 拦截器判断“启动安全锁”是否还需要保持：
    /// 刷新结束仍无配置（未加入集控 / 拉取失败 / 关闭了清单驱动）时解除安全锁，
    /// 与“无配置则全部可操作”的既有降级语义保持一致。
    /// </summary>
    public event Action? RefreshCompleted;

    /// <summary>插件配置目录。</summary>
    public string ConfigFolder => _configFolder;

    /// <summary>引导配置文件路径（Config.json）。</summary>
    public string BootstrapConfigPath => Path.Combine(_configFolder, "Config.json");

    /// <summary>
    /// 初始化权限管理器（应用启动完成后调用）。
    /// </summary>
    /// <param name="pluginConfigFolder">插件配置目录（来自 PluginBase.PluginConfigFolder）。</param>
    public void Initialize(string pluginConfigFolder)
    {
        _configFolder = pluginConfigFolder;
        if (!Directory.Exists(_configFolder))
        {
            Directory.CreateDirectory(_configFolder);
        }

        // 读取（不存在时自动生成）引导配置
        Settings = ConfigureFileHelper.LoadConfig<FineControlSettings>(BootstrapConfigPath);
        _logger.LogInformation("精细化管理插件已启动，集控清单驱动：{Enabled}", Settings.EnableManifestSource);

        // 读取本地插件版本与插件更新状态（v1.4.1）
        // 启动时若检测到本地插件版本 == 上次待安装版本，说明更新已随本次启动生效，
        // 记录“最终生效时间”（仅首次记录，之后保持）。
        LocalPluginVersion = ReadLocalPluginVersion() ?? "";
        LoadPluginUpdateState();
        if (_updateState is { PendingVersion: not null, AppliedAt: null } &&
            string.Equals(LocalPluginVersion, _updateState.PendingVersion, StringComparison.OrdinalIgnoreCase))
        {
            _updateState.AppliedAt = DateTime.Now;
            SavePluginUpdateState();
            _logger.LogInformation("插件更新已生效：v{Version}（生效时间 {AppliedAt:yyyy-MM-dd HH:mm:ss}）",
                LocalPluginVersion, _updateState.AppliedAt.Value);
        }

        // 启动时先应用本地缓存的最近一次成功配置（离线兜底），再拉取网络最新配置。
        // 这样即使某次网络/清单异常，也不会出现“整场管控失效”的空窗。
        TryLoadCachedConfig();

        // 启动时立即执行一次拉取（v1.3.15：不再校验版本号，无条件拉取并应用——
        // 管理员修改了 permissions.json 但未递增 Version 时，客户端重启也能立即生效）。
        // 失败时按退避策略重试（有限次，非轮询），避免一次瞬时故障导致全程无管控。
        _ = RefreshStartupAsync();
    }

    /// <summary>
    /// 启动刷新（v1.5.12）：最多 3 次尝试（0s / 20s / 60s 退避），成功即停止。
    /// 每次失败都会保留当前已应用配置（内存或本地缓存），不会清空管控。
    /// </summary>
    private async Task RefreshStartupAsync()
    {
        var delays = new[] { 0, 20_000, 60_000 };
        for (var i = 0; i < delays.Length; i++)
        {
            if (delays[i] > 0)
            {
                await Task.Delay(delays[i]);
            }

            if (await RefreshAsync(force: true))
            {
                return;
            }
        }

        _logger.LogWarning("启动刷新多次失败，本次会话继续使用已有配置（本地缓存或内存），" +
                           "可稍后在“精细化管理”状态页点击“更新”手动重试。");
    }

    /// <summary>最近一次成功应用的权限配置本地缓存路径（离线兜底）。</summary>
    public string PermissionConfigCachePath => Path.Combine(_configFolder, "PermissionConfigCache.json");

    /// <summary>启动时应用本地缓存的最近一次成功配置（存在且可解析时）。</summary>
    private void TryLoadCachedConfig()
    {
        // 集控清单驱动关闭时不应加载任何配置（含缓存）
        if (!Settings.EnableManifestSource)
        {
            return;
        }

        try
        {
            if (!File.Exists(PermissionConfigCachePath))
            {
                return;
            }

            var json = File.ReadAllText(PermissionConfigCachePath);
            var config = DeserializeConfig(json);
            if (config == null)
            {
                _logger.LogWarning("本地权限配置缓存解析失败，已忽略：{Path}", PermissionConfigCachePath);
                return;
            }

            ApplyConfig(config, "本地缓存");
            _logger.LogInformation("已应用本地缓存的权限配置（网络刷新成功后会自动覆盖）。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取本地权限配置缓存失败。");
        }
    }

    /// <summary>写入本地缓存（仅保存成功拉取并解析的原始 JSON）。</summary>
    private void SaveCachedConfig(string json)
    {
        try
        {
            File.WriteAllText(PermissionConfigCachePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入本地权限配置缓存失败。");
        }
    }

    /// <summary>
    /// 立即重新加载权限配置（供状态页“更新”按钮调用；force=true 时无视版本号强制拉取）。
    /// </summary>
    public Task ReloadAsync(bool force = false) => RefreshAsync(force);

    /// <summary>
    /// 按集控清单刷新权限配置（v1.1.6 起唯一来源）：
    ///   1. 从客户端集控配置（Settings.json / ManagementPreset.json）取 ManifestUrlTemplate；
    ///   2. 拉取服务器原始 manifest.json（每次启动/刷新都会强制拉取，不受版本号控制）；
    ///   3. 清单中存在本插件自定义源 → 无条件拉取 Value 指向的 permissions.json 并应用；
    ///   4. 任一环节缺失/失败 → 记录引导性错误；已有配置（本地缓存或内存）继续生效。
    /// 说明：清单中的 Version 仅用于判定插件本体版本（见 CheckPluginUpdateAsync），
    /// 与 permissions.json 的下载无关；修改权限配置无需递增 Version。
    /// force 参数保留用于兼容旧调用：权限配置本身已无版本门槛，始终强制拉取。
    /// </summary>
    public async Task<bool> RefreshAsync(bool force = false)
    {
        if (_refreshing)
        {
            return false;
        }

        _refreshing = true;
        var success = false;
        try
        {
            if (!Settings.EnableManifestSource)
            {
                LastError = "集控清单驱动已在 Config.json 中关闭（EnableManifestSource=false），" +
                            "当前未加载任何权限配置。请将其改为 true 后重启插件。";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }

            // 只有清单源成功处理（Ok）才算本次刷新成功，用于启动重试判断
            success = await RefreshFromManifestAsync(force) == ManifestRefreshResult.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新权限配置失败");
            LastError = ex.Message;
        }
        finally
        {
            _refreshing = false;
            RefreshCompleted?.Invoke();
        }

        return success;
    }

    /// <summary>
    /// 集控清单驱动的刷新结果。
    /// </summary>
    private enum ManifestRefreshResult
    {
        /// <summary>清单中未配置该自定义源（或客户端未加入集控），无需接管。</summary>
        NotConfigured,

        /// <summary>已成功读取清单并应用权限配置（插件升级另行处理）。</summary>
        Ok,

        /// <summary>清单中配置了该源但拉取失败。</summary>
        Failed
    }

    /// <summary>
    /// 读取原始集控清单中的自定义源并处理：
    ///   1. 从客户端集控配置取 ManifestUrlTemplate；
    ///   2. 每次调用都直接 GET 原始 manifest.json（不读官方落盘副本，
    ///      因为官方反序列化会丢弃未知的自定义字段）；
    ///   3. 读取 FineControlPermissionSource：
    ///        - Value 为文件夹路径（推荐）→ 拼接 permissions.json 拉取，
    ///          并按“文件夹路径 + 固定前缀 + Version + .cipx”下载插件安装包；
    ///        - Value 直接指向 .json → 仅拉取该文件，插件自动升级失效；
    ///   4. Version 只表示插件本体版本等级，不控制 permissions.json 的下载
    ///      （清单与权限配置每次刷新都强制拉取）。
    /// force 参数保留用于兼容：权限配置始终强制拉取，无版本门槛。
    /// </summary>
    private async Task<ManifestRefreshResult> RefreshFromManifestAsync(bool force)
    {
        try
        {
            // 1) 取客户端集控配置中的清单 URL（已加入集控 → Settings.json；否则 → ManagementPreset.json）
            if (!TryGetManifestUrlTemplate(out var manifestUrl))
            {
                ManifestSourceConfigured = false;
                LastError = "未检测到官方集控配置：%LocalAppData%\\ClassIsland\\Management\\Settings.json " +
                            "与应用目录 ManagementPreset.json 均不存在或缺少 ManifestUrlTemplate。" +
                            "请先在 ClassIsland 中“加入管理”并配置集控服务器（见《服务端配置指南》）。";
                _logger.LogWarning("{Error}", LastError);
                return ManifestRefreshResult.NotConfigured;
            }

            // 2) 拉取服务器上的原始集控清单（含官方字段与我们的自定义字段）
            LastManifestCheckedAt = DateTime.Now;
            var manifestJson = await HttpGetStringAsync(manifestUrl);
            if (manifestJson == null)
            {
                LastError = $"拉取集控清单失败：{manifestUrl}";
                _logger.LogWarning("{Error}", LastError);
                return ManifestRefreshResult.Failed;
            }

            using var doc = JsonDocument.Parse(manifestJson);

            // 3) 解析自定义源（大小写不敏感 + 类型容错）
            if (!TryGetPropertyIgnoreCase(doc.RootElement, Settings.ManifestSourceName, out var sourceElement))
            {
                // 清单里没有本插件自定义源：不加载任何配置，给出引导提示
                ManifestSourceConfigured = false;
                LastError = $"集控清单中未找到自定义源“{Settings.ManifestSourceName}”。" +
                            "请在服务器 manifest.json 顶层添加该字段（见《服务端配置指南》）。";
                _logger.LogWarning("{Error}", LastError);
                return ManifestRefreshResult.NotConfigured;
            }

            // Version：兼容 JSON 数字与字符串（v1.3.21 起为插件版本号字符串，如 "1.3.21"；
            // 旧配置为数字时同样容错读取，仅作展示与插件更新判定）
            if (!TryGetPropertyIgnoreCase(sourceElement, "Version", out var versionElement) ||
                !TryGetPropertyIgnoreCase(sourceElement, "Value", out var valueElement) ||
                !TryParseVersionString(versionElement, out var versionText))
            {
                ManifestSourceConfigured = false;
                LastError = $"集控清单中的“{Settings.ManifestSourceName}”缺少有效的 Version 或 Value 字段。";
                _logger.LogWarning("{Error}", LastError);
                return ManifestRefreshResult.NotConfigured;
            }

            var rawValue = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                ManifestSourceConfigured = false;
                LastError = $"集控清单中的“{Settings.ManifestSourceName}”的 Value 为空。";
                _logger.LogWarning("{Error}", LastError);
                return ManifestRefreshResult.NotConfigured;
            }

            ManifestSourceConfigured = true;
            LastManifestVersion = int.TryParse(versionText, out var intVersion) ? intVersion : 0;
            LastManifestSourceVersion = versionText;
            LastManifestFetchedAt = DateTime.Now;
            LastManifestUrl = manifestUrl;

            // Value 兼容两种形态（v1.3.21）：
            //   推荐：指向插件文件夹（如 .../Plugin/，不以 .json 结尾）→ 拼接固定文件名
            //         permissions.json；同目录按 “ClassIslandFineControl-v{Version}.cipx” 存放更新包；
            //   旧格式：完整指向 permissions.json 直链 → 仅拉取该文件，插件自动升级失效。
            var valueIsFolder = !rawValue.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var configUrl = valueIsFolder
                ? rawValue.TrimEnd('/') + "/" + PermissionConfigFileName
                : rawValue;

            // 权限配置与清单一样每次强制拉取，不受 Version 控制；
            // 管理员修改了 permissions.json 而未递增版本号时，客户端重启/手动更新也能立即生效。
            var ok = await FetchUrlAsync(configUrl, "Manifest");
            if (!ok)
            {
                LastError = $"集控清单源拉取失败（{versionText}）：{configUrl}";
                return ManifestRefreshResult.Failed;
            }

            SaveAppliedManifestVersion(intVersion);
            _logger.LogInformation("集控清单源已应用：v{Version} → {Url}", versionText, configUrl);

            // 插件自更新（v1.3.21）：仅文件夹形态（Value 不以 .json 结尾）生效。
            // Version 只代表插件本体版本等级：与本地插件版本相等/更低 → 不更新；
            // 更高 → 按“文件夹路径 + 固定前缀 + Version + .cipx”下载安装包到官方 PluginPackages，
            // 下次重启时由官方静默安装。
            if (valueIsFolder)
            {
                await CheckPluginUpdateAsync(versionText, rawValue.TrimEnd('/'));
            }

            return ManifestRefreshResult.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取集控清单失败");
            LastError = $"读取集控清单失败：{ex.Message}";
            return ManifestRefreshResult.Failed;
        }
    }

    /// <summary>
    /// 从客户端集控配置中获取集控清单 URL（ManifestUrlTemplate）。
    /// 读取顺序：
    ///   1. %LocalAppData%\ClassIsland\Management\Settings.json（客户端已加入集控时由官方保存，
    ///      要求 IsManagementEnabled 为 true）；
    ///   2. &lt;ClassIsland根目录&gt;\ManagementPreset.json（未加入集控但放了预设文件时可用）。
    /// 两个文件都不可用或字段缺失时返回 false（调用方按“未配置”处理）。
    /// </summary>
    private bool TryGetManifestUrlTemplate(out string urlTemplate)
    {
        urlTemplate = "";

        // 优先：已加入集控时官方保存的客户端集控配置
        if (File.Exists(ManagementSettingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ManagementSettingsPath));
                // 已加入集控才视为有效；IsManagementEnabled 缺失时宽松视为有效
                var enabled = true;
                if (TryGetPropertyIgnoreCase(doc.RootElement, "IsManagementEnabled", out var enabledEl))
                {
                    enabled = enabledEl.ValueKind != JsonValueKind.False;
                }

                if (enabled &&
                    TryGetPropertyIgnoreCase(doc.RootElement, "ManifestUrlTemplate", out var urlEl) &&
                    !string.IsNullOrWhiteSpace(urlEl.GetString()))
                {
                    urlTemplate = urlEl.GetString()!;
                    _logger.LogInformation("已从客户端集控配置读取清单 URL：{Url}", urlTemplate);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取客户端集控配置失败：{Path}", ManagementSettingsPath);
            }
        }

        // 兜底：应用目录下的 ManagementPreset.json（未加入集控但已放置预设）
        if (File.Exists(ManagementPresetPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ManagementPresetPath));
                if (TryGetPropertyIgnoreCase(doc.RootElement, "ManifestUrlTemplate", out var urlEl) &&
                    !string.IsNullOrWhiteSpace(urlEl.GetString()))
                {
                    urlTemplate = urlEl.GetString()!;
                    _logger.LogInformation("已从 ManagementPreset.json 读取清单 URL：{Url}", urlTemplate);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取集控预设文件失败：{Path}", ManagementPresetPath);
            }
        }

        return false;
    }

    /// <summary>
    /// 通用 HTTP GET（带鉴权头，若有配置），返回响应体字符串；失败返回 null。
    /// </summary>
    private async Task<string?> HttpGetStringAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(Settings.HttpAuthHeaderName))
            {
                request.Headers.TryAddWithoutValidation(Settings.HttpAuthHeaderName, Settings.HttpAuthHeaderValue);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP 请求失败：{Url}", url);
            return null;
        }
    }

    /// <summary>
    /// 大小写不敏感地从 JSON 对象中读取属性（兼容管理员手写字段名的大小写差异）。
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 容错解析版本号文本：支持 JSON 数字（16）、数字字符串（"16"）与
    /// 插件版本号字符串（"1.3.21"），统一返回字符串；解析失败返回 false。
    /// </summary>
    private static bool TryParseVersionString(JsonElement element, out string versionText)
    {
        versionText = "";
        if (element.ValueKind == JsonValueKind.Number)
        {
            versionText = element.ToString();
            return !string.IsNullOrWhiteSpace(versionText);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            versionText = element.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(versionText);
        }

        return false;
    }

    /// <summary>
    /// 插件自更新检查（v1.3.21）：
    ///   集控清单 Version = 代码托管平台上最新安装包的版本号（仅代表插件本体版本等级）。
    ///   将它与本地插件版本（Plugins/{PluginId}/manifest.yml 的 version）比较：
    ///     相等/更低 → 不更新；更高 → 下载安装包到官方 PluginPackages（{PluginId}.cipx），
    ///     下次启动时由官方静默覆盖安装。
    ///   下载地址拼接：{文件夹路径}/{固定前缀}{Version}{后缀}，例如
    ///     https://host/Plugin/ClassIslandFineControl-v1.5.13.cipx
    ///   Value 必须配置为文件夹路径；若配置成 permissions.json 直链，则不会进入本方法，
    ///   远程自动升级将失效。
    ///   下载失败（含 404：管理员尚未上传对应版本安装包）仅记日志，不影响配置应用。
    /// </summary>
    private async Task CheckPluginUpdateAsync(string manifestVersion, string folderUrl)
    {
        try
        {
            // 记录检查时间（无论是否更新，均持久化，供状态页“更新检查时间”展示）
            _updateState ??= new PluginUpdateState();
            _updateState.LastCheckedAt = DateTime.Now;
            SavePluginUpdateState();

            var localVersion = ReadLocalPluginVersion();
            if (string.IsNullOrWhiteSpace(localVersion))
            {
                _logger.LogInformation("未读取到本地插件版本（插件目录缺 manifest.yml），跳过插件更新检查。");
                return;
            }

            if (!Version.TryParse(manifestVersion, out var remote) ||
                !Version.TryParse(localVersion, out var local))
            {
                _logger.LogDebug("清单版本 {Remote} 或本地插件版本 {Local} 不是合法版本号，跳过插件更新检查。",
                    manifestVersion, localVersion);
                return;
            }

            if (remote <= local)
            {
                _logger.LogInformation("插件版本已为最新（本地 v{Local}，清单 v{Remote}），无需更新。", localVersion, manifestVersion);
                return;
            }

            var packageUrl = folderUrl + "/" + string.Format(PluginPackageFileNameTemplate, manifestVersion);
            var ok = await DownloadPluginPackageAsync(packageUrl);
            if (ok)
            {
                // 记录下载完成时间（非生效时间）与来源，持久化供状态页跨重启展示
                _updateState = new PluginUpdateState
                {
                    PendingVersion = manifestVersion,
                    DownloadedAt = DateTime.Now,
                    SourceUrl = packageUrl
                };
                SavePluginUpdateState();
                PluginUpdatePending = true;
                _logger.LogInformation(
                    "插件更新包已下载（v{Remote}，下载完成 {DownloadedAt:yyyy-MM-dd HH:mm:ss}，" +
                    "重启后由官方自动安装生效）：{Url}",
                    manifestVersion, _updateState.DownloadedAt.Value, packageUrl);
            }
            else
            {
                _logger.LogWarning("插件更新包下载失败（v{Remote}）：{Url}。请确认已上传 {File}。",
                    manifestVersion, packageUrl, string.Format(PluginPackageFileNameTemplate, manifestVersion));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "插件自更新检查失败。");
        }
    }

    /// <summary>
    /// 读取本地插件版本：Plugins/{PluginId}/manifest.yml 的 version 字段（简单行解析，
    /// 不引入 YAML 依赖；该文件为官方固定格式）。
    /// </summary>
    private string? ReadLocalPluginVersion()
    {
        try
        {
            var manifestPath = Path.Combine(
                CommonDirectories.AppRootFolderPath, "Plugins", PluginId, PluginManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            const string prefix = "version:";
            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(prefix.Length).Trim().Trim('"', '\'');
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取插件自身版本失败。");
            return null;
        }
    }

    /// <summary>
    /// 静默下载插件安装包到官方待安装目录 PluginPackages/{PluginId}.cipx。
    /// 不弹窗、不通知；下载完成后由官方在下次启动时自动安装替换。
    /// </summary>
    private async Task<bool> DownloadPluginPackageAsync(string url)
    {
        try
        {
            var pkgRoot = Path.Combine(CommonDirectories.AppCacheFolderPath, "PluginPackages");
            Directory.CreateDirectory(pkgRoot);
            var pkgPath = Path.Combine(pkgRoot, PluginId + ".cipx");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(Settings.HttpAuthHeaderName))
            {
                request.Headers.TryAddWithoutValidation(Settings.HttpAuthHeaderName, Settings.HttpAuthHeaderValue);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(pkgPath))
            {
                await response.Content.CopyToAsync(fs);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "插件更新包下载失败：{Url}", url);
            return false;
        }
    }

    /// <summary>插件更新状态文件路径（插件配置目录 UpdateState.json）。</summary>
    private string PluginUpdateStatePath => Path.Combine(_configFolder, "UpdateState.json");

    /// <summary>读取持久化的插件更新状态；不存在或解析失败时保持 null。</summary>
    private void LoadPluginUpdateState()
    {
        try
        {
            if (!File.Exists(PluginUpdateStatePath))
            {
                return;
            }

            _updateState = JsonSerializer.Deserialize<PluginUpdateState>(
                File.ReadAllText(PluginUpdateStatePath), JsonOptions);
            PluginUpdatePending = _updateState is { AppliedAt: null, PendingVersion: not null };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取插件更新状态失败");
            _updateState = null;
        }
    }

    /// <summary>持久化插件更新状态（下载/生效时间跨重启保留）。</summary>
    private void SavePluginUpdateState()
    {
        try
        {
            File.WriteAllText(
                PluginUpdateStatePath,
                JsonSerializer.Serialize(_updateState, IndentedJsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入插件更新状态失败");
        }
    }

    /// <summary>
    /// 记录本次成功应用的清单源版本号（持久化到插件配置目录 ManifestVersions.json）。
    /// </summary>
    private void SaveAppliedManifestVersion(int version)
    {
        try
        {
            var state = new Dictionary<string, object>
            {
                ["SourceName"] = Settings.ManifestSourceName,
                ["Version"] = version
            };
            File.WriteAllText(
                ManifestVersionsPath,
                JsonSerializer.Serialize(state, IndentedJsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入清单版本缓存失败");
        }
    }

    /// <summary>
    /// 从指定 URL 拉取权限配置并应用（供集控清单模式使用）。
    /// 请求构建（含鉴权头）复用 <see cref="HttpGetStringAsync"/>。
    /// </summary>
    /// <param name="url">权限配置文件 URL（http/https）。</param>
    /// <param name="sourceLabel">来源标签（如 Manifest）。</param>
    /// <returns>是否拉取并解析成功。</returns>
    private async Task<bool> FetchUrlAsync(string url, string sourceLabel)
    {
        try
        {
            var json = await HttpGetStringAsync(url);
            if (json == null)
            {
                LastError = $"远程拉取失败：{url}";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }

            var config = DeserializeConfig(json);
            if (config == null)
            {
                LastError = "远程权限配置解析失败（内容不是合法 JSON 或结构不匹配）。";
                _logger.LogWarning("{Error}", LastError);
                return false;
            }

            ApplyConfig(config, $"{sourceLabel}:{url}");
            SaveCachedConfig(json); // 成功后写入本地缓存，供下次启动离线兜底
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取远程权限配置失败：{Url}", url);
            LastError = $"远程拉取失败：{ex.Message}";
            return false;
        }
    }

    private static PermissionConfig? DeserializeConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PermissionConfig>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 应用新配置（更新状态并通知订阅者）。若当前不在 UI 线程，则切回 UI 线程执行，
    /// 以便状态页、拦截器等 UI 订阅者安全刷新。
    /// </summary>
    private void ApplyConfig(PermissionConfig config, string source)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyConfig(config, source));
            return;
        }

        CurrentConfig = config;
        LastSource = source;
        LastLoadTime = DateTime.Now;
        LastError = null;
        _logger.LogInformation(
            "权限配置已加载（{Source}）：页面规则 {PageCount} 条，控件规则 {ItemCount} 条",
            source, config.SettingsPages.Count, config.SettingsItems.Count);

        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// 官方内置设置分组 → 组内设置页 Id 的映射表（v1.1.7 起支持“大页”级规则）。
    /// 取自 ClassIsland 2.1.0.1 源码：
    ///   ClassIsland/App.Services.xaml.cs 的 AddSettingsPageGroup（分组注册）与
    ///   ClassIsland/Views/SettingPages/*.axaml.cs 的 [Group(...)]（页面归属）。
    /// 目前官方只有两个分组，其余设置页（提醒/天气/更新/自动化/插件/主题/集控/
    /// 关于/调试等）为无分组独立页，不受大页规则影响，只能用小页规则配置。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GroupPageMap =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["classisland.general"] = new[] { "general", "clock", "storage", "privacy", "refreshing", "advanced" },
            ["classisland.mainwindow"] = new[] { "window", "appearance", "components" }
        };

    /// <summary>根据页面 Id 反查其所属的官方大页（分组）Id；无分组时返回 null。</summary>
    public static string? GetGroupIdOfPage(string pageId)
    {
        foreach (var pair in GroupPageMap)
        {
            if (pair.Value.Any(id => string.Equals(id, pageId, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// 查询某个设置分组（大页）的权限状态（Operable / Disabled / Hidden）。
    /// 优先级：树状格式（Groups）→ 平铺格式（SettingsGroups）→ Operable。
    /// </summary>
    public PermissionState GetGroupState(string groupId)
    {
        var config = CurrentConfig;
        if (config == null)
        {
            return PermissionState.Operable;
        }

        // 树状格式（v1.1.8 推荐）
        var groupNode = config.Groups.FirstOrDefault(
            x => string.Equals(x.Id, groupId, StringComparison.OrdinalIgnoreCase));
        if (groupNode != null)
        {
            return groupNode.State;
        }

        // 平铺格式（旧格式兼容）
        var entry = config.SettingsGroups.FirstOrDefault(
            x => string.Equals(x.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
        return entry?.State ?? PermissionState.Operable;
    }

    /// <summary>
    /// 查询某个设置页的权限状态（Operable / Disabled / Hidden）。
    /// 优先级（v1.1.8 树状 + 大页继承）：
    ///   1. 页面所属大页（分组）被显式配置为 Disabled/Hidden → 页面继承该状态
    ///      （大页禁用则整组小页全部失效）；
    ///   2. 树状格式中的小页节点（分组内 pages 或顶层 Pages）→ 按该节点权限；
    ///   3. 平铺格式 SettingsPages 显式列出 → 按该条规则；
    ///   4. 未显式列出的页面按默认权限处理。
    /// </summary>
    public PermissionState GetPageState(string pageId)
    {
        var config = CurrentConfig;
        if (config == null)
        {
            return PermissionState.Operable;
        }

        // 大页继承：所属分组被禁用/隐藏时，组内所有小页整体失效
        var groupId = GetGroupIdOfPage(pageId);
        if (groupId != null)
        {
            var groupState = GetGroupState(groupId);
            if (groupState != PermissionState.Operable)
            {
                return groupState;
            }
        }

        // 树状格式：分组内小页 + 顶层独立小页
        foreach (var group in config.Groups)
        {
            var page = group.Pages.FirstOrDefault(
                x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                return page.State;
            }
        }

        var standalonePage = config.Pages.FirstOrDefault(
            x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (standalonePage != null)
        {
            return standalonePage.State;
        }

        // 平铺格式（旧格式兼容）
        var entry = config.SettingsPages.FirstOrDefault(
            x => string.Equals(x.PageId, pageId, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            return entry.State;
        }

        // 未显式列出的页面按默认权限处理（v1.1.9：支持 Hidden，
        // 修复“默认权限=隐藏”时未列出页面只落到 Disabled 置灰而不隐藏的问题）
        if (config.IsOperableDefault)
        {
            return PermissionState.Operable;
        }

        return string.Equals(config.DefaultPermission, "Hidden", StringComparison.OrdinalIgnoreCase)
            ? PermissionState.Hidden
            : PermissionState.Disabled;
    }

    /// <summary>
    /// 查询某个设置分组（大页）是否被禁用/隐藏。
    /// </summary>
    public bool IsGroupDisabled(string groupId) => GetGroupState(groupId) != PermissionState.Operable;

    /// <summary>
    /// 查询某个设置页是否被禁用/隐藏（Disabled 或 Hidden 均视为不可进入）。
    /// </summary>
    public bool IsPageDisabled(string pageId) => GetPageState(pageId) != PermissionState.Operable;

    /// <summary>
    /// 页面是否在配置中被显式列出（树状分组内小页 / 顶层独立小页 / 平铺格式），
    /// 不包含“未配置 → 默认权限”的情况。（v1.3.17）
    /// 用于级联判定：只有未显式配置的页面才跟随插件页隐藏，显式开启的页面
    /// （如管理员手动打开的“关于 ClassIsland”）不受级联影响。
    /// </summary>
    public bool IsPageExplicitlyConfigured(string pageId)
    {
        var config = CurrentConfig;
        if (config == null)
        {
            return false;
        }

        foreach (var group in config.Groups)
        {
            if (group.Pages.Any(x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        if (config.Pages.Any(x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return config.SettingsPages.Any(
            x => string.Equals(x.PageId, pageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 获取指定设置页内的控件级权限规则。
    /// 优先级：树状格式（分组内 pages[].items / 顶层 Pages[].items）→ 平铺格式 SettingsItems。
    /// 树状 items 转换为平铺 PermissionItem 供拦截器使用（逻辑完全一致）。
    /// </summary>
    public IEnumerable<PermissionItem> GetItemsForPage(string pageId)
    {
        var config = CurrentConfig;
        if (config == null)
        {
            return Enumerable.Empty<PermissionItem>();
        }

        // 树状格式（v1.1.8 推荐）：分组内小页
        var groupPage = FindPageInGroups(config, pageId);
        if (groupPage != null)
        {
            return MapItemNodes(pageId, groupPage);
        }

        // 树状格式：顶层独立小页
        var standalonePage = config.Pages.FirstOrDefault(
            x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (standalonePage != null)
        {
            return MapItemNodes(pageId, standalonePage);
        }

        // 平铺格式（旧格式兼容）
        return config.SettingsItems.Where(
            x => string.Equals(x.PageId, pageId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 获取全部控件级权限规则（树状 + 平铺，统一为平铺 PermissionItem）。
    /// 供值级守卫在“全部设置项”范围内收集需锁定的 SettingsProperty。
    /// </summary>
    public IEnumerable<PermissionItem> GetAllPermissionItems()
    {
        var config = CurrentConfig;
        if (config == null)
        {
            return Enumerable.Empty<PermissionItem>();
        }

        var result = new List<PermissionItem>();
        foreach (var group in config.Groups)
        {
            foreach (var page in group.Pages)
            {
                result.AddRange(MapItemNodes(page.Id, page));
            }
        }

        foreach (var page in config.Pages)
        {
            result.AddRange(MapItemNodes(page.Id, page));
        }

        result.AddRange(config.SettingsItems);
        return result;
    }

    /// <summary>在树状配置的所有分组内查找指定页面节点；未找到返回 null。</summary>
    private static PermissionPageNode? FindPageInGroups(PermissionConfig config, string pageId)
    {
        foreach (var group in config.Groups)
        {
            var page = group.Pages.FirstOrDefault(
                x => string.Equals(x.Id, pageId, StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>把树状页面节点内的 items 节点映射为平铺 PermissionItem（拦截器统一消费）。</summary>
    private static IEnumerable<PermissionItem> MapItemNodes(string pageId, PermissionPageNode page)
    {
        return page.Items.Select(n => new PermissionItem
        {
            PageId = pageId,
            ControlKey = n.Key,
            MatchMode = n.MatchMode,
            Index = n.Index,
            Permission = n.Permission,
            SettingsProperty = n.SettingsProperty,
            Remark = n.Remark
        });
    }
}
