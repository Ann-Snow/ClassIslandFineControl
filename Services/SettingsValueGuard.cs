using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using ClassIsland.Shared;
using ClassIslandFineControl.Models;
using Microsoft.Extensions.Logging;

namespace ClassIslandFineControl.Services;

/// <summary>
/// 设置值级守卫。
/// 原理（使用 ClassIsland 官方 SettingsService 能力）：
///   SettingsService.AddSettingsOverlay(Guid, 属性名, 值) 可以在设置对象上
///   强制覆盖某个设置属性的值，并记录到 Settings.SettingsOverlays 中。
///   本守卫在权限配置加载后，对每个带 SettingsProperty 的禁用项执行：
///     1. 读取当前值 → AddSettingsOverlay 锁定；
///     2. 订阅 Settings.PropertyChanged，一旦该属性被外部修改立即弹回锁定值。
/// 说明：SettingsService 是 ClassIsland 主程序内部服务（具体类型，未提供公开接口），
/// 插件无法直接引用主程序程序集，因此通过程序集扫描 + 反射调用其公开成员，
/// 全部基于源码中的真实成员（Settings / AddSettingsOverlay / RemoveSettingsOverlay）。
/// 若运行时找不到该服务（版本不兼容），自动降级为仅 UI 拦截，不影响插件其它功能。
/// </summary>
public class SettingsValueGuard
{
    private readonly ILogger<SettingsValueGuard> _logger;
    private PermissionManager? _manager;

    /// <summary>被锁定的设置属性名 -> 锁定的值。</summary>
    private readonly Dictionary<string, object?> _lockedValues = new(StringComparer.OrdinalIgnoreCase);

    private object? _settingsService;
    private object? _settings;

    public SettingsValueGuard(ILogger<SettingsValueGuard> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 启动值级守卫。权限配置后续刷新时自动重新应用全部锁定。
    /// </summary>
    /// <param name="manager">权限管理器实例。</param>
    public void Start(PermissionManager manager)
    {
        _manager = manager;
        if (!_manager.Settings.EnableValueLock)
        {
            _logger.LogInformation("值级锁定已通过 Config.json 关闭，仅保留 UI 拦截。");
            return;
        }

        _settingsService = ResolveSettingsService();
        if (_settingsService == null)
        {
            _logger.LogWarning("未找到 SettingsService（当前 ClassIsland 版本可能不兼容），值级锁定降级跳过，仅保留 UI 拦截。");
            return;
        }

        try
        {
            _settings = _settingsService.GetType().GetProperty("Settings")?.GetValue(_settingsService);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 SettingsService.Settings 失败，值级锁定降级跳过。");
            _settings = null;
        }

        if (_settings is INotifyPropertyChanged notify)
        {
            notify.PropertyChanged += OnSettingsPropertyChanged;
        }

        ApplyAllLocks();
        _manager.ConfigChanged += ApplyAllLocks;
    }

    /// <summary>
    /// 依据当前权限配置，对全部带 SettingsProperty 的禁用项执行锁定；
    /// 同时解除“已不再禁用”的属性的旧锁定（权限恢复为 Operable 后，
    /// 该项必须能重新自由修改，否则官方设置叠层会把值永久钉住）。
    /// </summary>
    private void ApplyAllLocks()
    {
        var service = _settingsService;
        var settings = _settings;
        if (service == null || settings == null)
        {
            return;
        }

        var config = _manager?.CurrentConfig;
        if (config == null)
        {
            return;
        }

        // 1) 计算本次应锁定的属性集合
        //    （v1.3.3 修复：改用 GetAllPermissionItems 统一遍历树状格式
        //    groups[].pages[].items / pages[].items 与旧平铺 SettingsItems，
        //    此前只遍历 SettingsItems 导致树状格式的 settingsProperty 从不生效）
        var target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _manager!.GetAllPermissionItems())
        {
            if (item.IsDisabled && !string.IsNullOrWhiteSpace(item.SettingsProperty))
            {
                target.Add(item.SettingsProperty!);
            }
        }

        // 2) 解除不再锁定（权限已恢复）的属性：调用官方 RemoveSettingsOverlay
        foreach (var oldProperty in _lockedValues.Keys.ToList())
        {
            if (!target.Contains(oldProperty))
            {
                RemoveLock(oldProperty);
                _lockedValues.Remove(oldProperty);
            }
        }

        // 3) 应用新增/保持的锁定
        foreach (var propertyName in target)
        {
            LockProperty(propertyName);
        }
    }

    /// <summary>
    /// 解除单个设置属性的叠层锁定（调用官方 RemoveSettingsOverlay）。
    /// 官方实现会把属性值恢复到锁定前的值；失败仅记日志，不影响其它功能。
    /// </summary>
    private void RemoveLock(string propertyName)
    {
        var service = _settingsService;
        if (service == null)
        {
            return;
        }

        var removeMethod = service.GetType().GetMethod("RemoveSettingsOverlay");
        if (removeMethod == null)
        {
            return;
        }

        try
        {
            removeMethod.Invoke(service, new object[] { PermissionManager.PluginGuid, propertyName });
            _logger.LogInformation("已解除设置属性锁定：{Property}", propertyName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解除设置属性锁定失败：{Property}", propertyName);
        }
    }

    /// <summary>
    /// 锁定单个设置属性为当前值。
    /// </summary>
    private void LockProperty(string propertyName)
    {
        var service = _settingsService;
        var settings = _settings;
        if (service == null || settings == null)
        {
            return;
        }

        var settingsType = settings.GetType();
        // 与 SettingsService.SettingsPropertiesFlags 保持一致：仅取 Settings 自身声明的公开实例属性
        var property = settingsType.GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (property == null)
        {
            // 不是 Settings 模型属性（例如由系统服务托管的属性），跳过值级锁定
            return;
        }

        object? value;
        try
        {
            value = property.GetValue(settings);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取设置属性 {Property} 失败", propertyName);
            return;
        }

        _lockedValues[propertyName] = value;

        var addMethod = service.GetType().GetMethod("AddSettingsOverlay");
        if (addMethod == null)
        {
            return;
        }

        try
        {
            addMethod.Invoke(service, new[] { PermissionManager.PluginGuid, propertyName, value });
            _logger.LogInformation("已锁定设置属性 {Property} = {Value}", propertyName, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "锁定设置属性 {Property} 失败", propertyName);
        }
    }

    /// <summary>
    /// Settings 属性被修改时触发：把被锁定的属性弹回锁定值。
    /// 注意：AddSettingsOverlay 内部通过 PropertyInfo.SetValue 直接写值，
    /// 不会再次触发 Settings 的 PropertyChanged，因此这里不会形成递归。
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            return;
        }

        if (!_lockedValues.ContainsKey(e.PropertyName))
        {
            return;
        }

        LockProperty(e.PropertyName);
    }

    /// <summary>
    /// 通过程序集扫描定位 ClassIsland 主程序中的 SettingsService 实例。
    /// </summary>
    private object? ResolveSettingsService()
    {
        try
        {
            var appAssembly = Array.Find(
                AppDomain.CurrentDomain.GetAssemblies(),
                a => a.GetName().Name == "ClassIsland");
            var serviceType = appAssembly?.GetType("ClassIsland.Services.SettingsService");
            if (serviceType == null)
            {
                return null;
            }

            return IAppHost.Host?.Services.GetService(serviceType);
        }
        catch (Exception ex)
        {
            // 极端情况下记录日志并返回 null（调用方会降级）
            _logger.LogWarning(ex, "解析 SettingsService 失败，值级锁定降级跳过。");
            return null;
        }
    }
}
