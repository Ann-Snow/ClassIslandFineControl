using System;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIslandFineControl.Services;

namespace ClassIslandFineControl.Views.SettingsPages;

/// <summary>
/// 插件自带的“精细化管理”状态页。
/// 在应用设置导航中显示为“精细化管理”，分“插件类 / 策略类”两区块展示
/// 插件与集控清单的更新状态（检查/下载/生效时间、来源、版本）与拦截规则统计。
/// 所有条目恒显，无信息时显示（暂无信息），便于管理员一眼定位缺失项。
/// v1.3.20：删除“更新”按钮；v1.4.2：按用户格式重构为双区块 + 恒显条目。
/// 页面 UI 全部用代码构建（不依赖 .axaml 编译），保证工程只使用标准 .NET SDK 即可编译。
/// </summary>
[SettingsPageInfo("cn.finecontrol.status", "精细化管理", SettingsPageCategory.External)]
public class FineControlStatusPage : SettingsPageBase
{
    private readonly PermissionManager _manager;
    private readonly TextBlock _statusText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        LineHeight = 22,
        Foreground = Brushes.Gray
    };

    public FineControlStatusPage(PermissionManager manager)
    {
        _manager = manager;
        BuildUi();
        RefreshStatus();

        // 配置变化时自动刷新本页状态；页面卸载时退订，避免单例管理器长期持有页面实例
        _manager.ConfigChanged += RefreshStatus;
        Unloaded += (_, _) => _manager.ConfigChanged -= RefreshStatus;
    }

    /// <summary>用代码构建页面 UI：标题 + 状态信息。</summary>
    private void BuildUi()
    {
        var title = new TextBlock
        {
            Text = "精细化管理",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(24, 16, 24, 24),
            Spacing = 8
        };
        panel.Children.Add(title);
        panel.Children.Add(_statusText);

        Content = new ScrollViewer
        {
            Content = panel
        };
    }

    /// <summary>
    /// 刷新状态信息（v1.4.2）：双区块（插件类 / 策略类），全部条目恒显，
    /// 无信息时显示“（暂无信息）”；配置未加载时拦截规则全 0 并在底部给出最近错误。
    /// </summary>
    private void RefreshStatus()
    {
        var config = _manager.CurrentConfig;
        var text = new StringBuilder();

        // —— 插件类 ——
        text.AppendLine("—————————— 插件类 ——————————");
        text.AppendLine("插件版本：" + (string.IsNullOrWhiteSpace(_manager.LocalPluginVersion)
            ? "（暂无信息）" : "v" + _manager.LocalPluginVersion));
        text.AppendLine("插件更新来源：" + (_manager.PluginUpdateSourceUrl ?? "（暂无信息）"));
        text.AppendLine("更新检查时间：" + Fmt(_manager.PluginUpdateCheckedAt));
        text.AppendLine("插件下载时间：" + Fmt(_manager.PluginUpdateDownloadedAt));
        text.AppendLine("插件更新时间：" + Fmt(_manager.PluginUpdateAppliedAt));

        // —— 策略类 ——
        text.AppendLine("—————————— 策略类 ——————————");
        text.AppendLine("清单更新来源：" + (_manager.ManifestSourceConfigured ? _manager.LastManifestUrl : "（暂无信息）"));
        text.AppendLine("更新检查时间：" + Fmt(_manager.LastManifestCheckedAt != default ? _manager.LastManifestCheckedAt : (DateTime?)null));
        text.AppendLine("清单下载时间：" + Fmt(_manager.LastManifestFetchedAt != default ? _manager.LastManifestFetchedAt : (DateTime?)null));
        text.AppendLine("清单生效时间：" + Fmt(_manager.LastLoadTime != default ? _manager.LastLoadTime : (DateTime?)null));

        // —— 拦截规则统计 ——
        text.AppendLine("拦截规则：");
        if (config == null)
        {
            text.AppendLine("置灰大页 0 · 置灰小页 0 · 置灰控件 0");
            text.AppendLine("隐藏大页 0 · 隐藏小页 0 · 隐藏控件 0");
            text.AppendLine("最近错误：" + (_manager.LastError ?? "（暂无）"));
        }
        else
        {
            var disabledGroups = config.Groups.Count(g => g.State == Models.PermissionState.Disabled);
            var disabledPages = config.Pages.Count(p => p.State == Models.PermissionState.Disabled) +
                                config.Groups.SelectMany(g => g.Pages).Count(p => p.State == Models.PermissionState.Disabled);
            var hiddenGroups = config.Groups.Count(g => g.State == Models.PermissionState.Hidden);
            var hiddenPages = config.Pages.Count(p => p.State == Models.PermissionState.Hidden) +
                              config.Groups.SelectMany(g => g.Pages).Count(p => p.State == Models.PermissionState.Hidden);
            var disabledItems = 0;
            var hiddenItems = 0;
            foreach (var item in _manager.GetAllPermissionItems())
            {
                if (item.IsHidden)
                {
                    hiddenItems++;
                }
                else if (item.IsDisabled)
                {
                    disabledItems++;
                }
            }

            text.AppendLine($"置灰大页 {disabledGroups} · 置灰小页 {disabledPages} · 置灰控件 {disabledItems}");
            text.AppendLine($"隐藏大页 {hiddenGroups} · 隐藏小页 {hiddenPages} · 隐藏控件 {hiddenItems}");
        }

        _statusText.Text = text.ToString();
    }

    /// <summary>格式化时间：null 显示（暂无信息），否则 yyyy-MM-dd HH:mm:ss。</summary>
    private static string Fmt(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "（暂无信息）";
}
