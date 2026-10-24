using System;
using System.IO;
using System.Threading.Tasks;
using ClassIsland.Core;
using Microsoft.Extensions.Logging;

namespace ClassIslandFineControl.Services;

/// <summary>
/// 插件自保护守卫（v1.5.11）：监视本插件安装目录，发现 ClassIsland 的
/// “撤销启用”标记 <c>.disabled</c> 与“标记移除”标记 <c>.uninstall</c> 时立即删除。
///   - <c>.disabled</c>：存在该文件时，ClassIsland 下次启动会禁用本插件；
///   - <c>.uninstall</c>：存在该文件时，ClassIsland 下次启动会删除本插件。
/// 检测方式为 FileSystemWatcher（事件驱动，无轮询）：用户在插件页取消“已启用”
/// 或点击“移除”时官方会立即写出标记文件，本守卫随即删除，使操作效果只停留在 UI，
/// 重启后插件仍保持启用。启动时、应用退出（重启）前各再补一次检查。
/// 受权限配置的自保护总开关 <see cref="PermissionConfig.SelfProtection"/> 控制：
/// 关闭自保护后不再拦截这两个标记文件（允许正常禁用/卸载）。
/// </summary>
public sealed class SelfProtectionGuard : IDisposable
{
    private readonly ILogger<SelfProtectionGuard> _logger;

    /// <summary>需要拦截并删除的标记文件名（位于插件安装目录根部）。</summary>
    private static readonly string[] MarkerFiles = { ".disabled", ".uninstall" };

    private PermissionManager? _manager;
    private FileSystemWatcher? _watcher;
    private string? _pluginFolder;

    public SelfProtectionGuard(ILogger<SelfProtectionGuard> logger)
    {
        _logger = logger;
    }

    /// <summary>启动监听（应用启动完成后、权限管理器初始化之后调用）。</summary>
    public void Start(PermissionManager manager)
    {
        _manager = manager;
        _pluginFolder = ResolvePluginFolder();
        if (_pluginFolder == null)
        {
            _logger.LogWarning("自保护：无法确定插件安装目录，标记文件拦截已跳过。");
            return;
        }

        // 启动时先清理已存在的标记（可能在插件加载前就被创建）
        RemoveMarkersNow("启动检查");

        // 配置热更新：自保护重新开启时补一次检查
        manager.ConfigChanged += () => RemoveMarkersNow("配置变更");

        // 退出/重启前再清理一次：官方若在退出流程写标记，也能在进程结束前删除
        AppBase.Current.AppStopping += (_, _) => RemoveMarkersNow("应用退出前");

        try
        {
            _watcher = new FileSystemWatcher(_pluginFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自保护：无法监听插件目录 {Folder}，标记文件拦截已跳过。", _pluginFolder);
            return;
        }

        _logger.LogInformation("自保护：已监听插件目录 {Folder}（拦截 .disabled / .uninstall）。", _pluginFolder);
    }

    /// <summary>自保护总开关：未加载配置时按默认关闭处理。</summary>
    private bool IsEnabled => _manager?.CurrentConfig?.SelfProtection ?? false;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (!Array.Exists(MarkerFiles,
                m => string.Equals(m, e.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        RemoveMarker(e.Name!);
    }

    private void RemoveMarkersNow(string trigger)
    {
        if (!IsEnabled || _pluginFolder == null)
        {
            return;
        }

        foreach (var marker in MarkerFiles)
        {
            RemoveFile(Path.Combine(_pluginFolder, marker), marker, trigger, retry: 3);
        }
    }

    /// <summary>
    /// 文件事件触发的删除：官方写入方可能仍持有句柄，放到后台做有限次重试，
    /// 避免阻塞 UI 线程。
    /// </summary>
    private void RemoveMarker(string marker)
    {
        if (_pluginFolder == null)
        {
            return;
        }

        var path = Path.Combine(_pluginFolder, marker);
        _ = Task.Run(() => RemoveFile(path, marker, "文件事件", retry: 5));
    }

    /// <summary>同步删除标记文件（仅存在时才操作）。</summary>
    private void RemoveFile(string path, string marker, string trigger, int retry)
    {
        for (var attempt = 0; attempt < retry; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                _logger.LogWarning("自保护：已删除标记文件 {File}（{Trigger}）。", path, trigger);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == retry - 1)
                {
                    _logger.LogWarning(ex, "自保护：删除标记文件 {File} 失败（已重试 {Retry} 次）。", path, retry);
                    return;
                }

                System.Threading.Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自保护：删除标记文件 {File} 失败。", path);
                return;
            }
        }
    }

    /// <summary>解析本插件安装目录：优先取入口程序集所在目录，兜底应用目录下的 Plugins 目录。</summary>
    private static string? ResolvePluginFolder()
    {
        var assemblyLocation = typeof(SelfProtectionGuard).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            return Path.GetDirectoryName(assemblyLocation);
        }

        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrWhiteSpace(baseDir)
            ? null
            : Path.Combine(baseDir, "Plugins", "cn.finecontrol.permission");
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
