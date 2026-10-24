using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIslandFineControl.Services;
using ClassIslandFineControl.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIslandFineControl;

/// <summary>
/// 插件入口类。
/// 遵循 ClassIsland 官方插件开发规范：
///   - 类上添加 [PluginEntrance] 特性；
///   - 继承 PluginBase 抽象类；
///   - 在 Initialize 中完成服务注册。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>
    /// 插件初始化（在应用主机构建阶段执行）。
    /// 注意：此时 IAppHost.Host 尚未就绪、UI 尚未创建，因此凡是要访问
    /// 应用服务 / 窗口 / 控件的工作，统一放到 AppBase.Current.AppStarted
    /// 事件触发后再做（官方文档推荐写法）。
    /// </summary>
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 1. 注册权限管理器（单例）：负责加载/拉取权限配置并对外提供权限查询
        services.AddSingleton<PermissionManager>();

        // 2. 注册设置值级守卫（单例）：对带 SettingsProperty 的禁用项做值级锁定
        services.AddSingleton<SettingsValueGuard>();

        // 3. 注册 UI 权限拦截器（单例）：扫描设置窗口，按权限配置禁用设置项
        services.AddSingleton<UiPermissionInterceptor>();

        // 3.1 注册自保护守卫（单例）：监视插件目录，立即删除 .disabled / .uninstall 标记
        services.AddSingleton<SelfProtectionGuard>();

        // 4. 注册插件自带“精细化管理”状态页（可在应用设置中查看配置状态/手动刷新）
        services.AddSettingsPage<FineControlStatusPage>();

        // 5. 应用启动完成后执行插件实际初始化
        AppBase.Current.AppStarted += (_, _) =>
        {
            // 防御：IAppHost.GetService 理论上可能返回 null（服务未注册/主机未就绪），
            // 判空后直接跳过，避免应用启动阶段空引用崩溃。
            var manager = IAppHost.GetService<PermissionManager>();
            var interceptor = IAppHost.GetService<UiPermissionInterceptor>();
            if (manager == null || interceptor == null)
            {
                return;
            }

            manager.Initialize(PluginConfigFolder);
            interceptor.Start(manager);

            // 自保护守卫（标记文件拦截）：受 selfProtection 总开关控制
            IAppHost.GetService<SelfProtectionGuard>()?.Start(manager);
        };
    }
}
