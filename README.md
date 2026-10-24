本插件为接入集控的 ClassIsland 客户端提供精细化的设置项权限管理：依据一份独立的权限配置，
对「应用设置」中的设置页与具体设置项执行**可操作 / 禁用 / 隐藏**三态控制，并可选锁定设置值，
避免管控策略被绕过。

## 功能

- **页面级**：`Disabled` 时设置窗口侧边导航中的对应设置页置灰、无法进入；`Hidden` 时该页
  彻底不显示在导航中。
- **控件级**：`Disabled` 时页内对应的设置项（开关、按钮、下拉框、输入框等）置灰、无法操作；
  `Hidden` 时该设置项从页面中彻底消失。
- **值级锁定（可选）**：对配置中指定 `settingsProperty` 的不可操作项，通过
  `SettingsService.AddSettingsOverlay` 将对应设置属性强制锁定，即使通过自动化、快捷键等
  其它途径修改也会被弹回。
- **防误伤匹配**：精确模式（HeaderText / Name / AutomationId）在页内只有唯一命中时才执行，
  多处同名默认跳过并记录警告；确需指定重复项时用 `index` 取第 N 个，批量匹配改用 `Wildcard`。
  控件应用全部为事件驱动（窗口打开、切页、页面挂载、列表选择、配置变更），无轮询。
- **应用菜单管控**：右上角“三个点”菜单的按钮与菜单项（含子菜单）可按伪页面
  `classisland.appmenu` 配置为可操作 / 置灰 / 隐藏。
- **托盘图标菜单管控**：托盘右键原生菜单（含“更多选项”子菜单）按伪页面
  `classisland.traymenu` 配置；页面级 `Hidden` 直接隐藏托盘图标，`Disabled` 让菜单整体置灰。
- **启动即管控**：权限配置拉取完成前先锁死托盘菜单，消除启动初期的短暂可操作窗口期；
  每次启动无条件拉取最新配置，配置中的项恢复为“可操作”后会自动重新启用/显示。
- **自保护**：默认锁定本插件自身的启用开关与移除按钮、置灰本插件详情工具菜单、隐藏插件页
  “刷新”，并拦截 `.disabled` / `.uninstall` 标记文件，避免管控被绕过；可由配置总开关关闭。

## 安装

1. 在 ClassIsland「应用设置 → 插件」中从本地安装 `.cipx`，或把插件文件放入
   `<ClassIsland根目录>/Plugins/cn.finecontrol.permission/`；
2. 重启 ClassIsland。

## 配置

权限配置只有两个环节：**集控清单声明权限源** + **服务器托管 permissions.json**。
客户端无需放置权限文件，启动时自动拉取。

### 1. 集控清单（manifest.json）

在服务器集控清单顶层增加本插件的自定义源（字段名可在 `Config.json` 中通过
`ManifestSourceName` 修改）：

```json
"FineControlPermissionSource": {
  "Version": "1.5.14.0",
  "Value": "https://example.com/classisland/"
}
```

- `Version`：**插件本体版本号**（对应托管平台最新安装包的版本），只用于与本地插件版本比较；
  高于本地版本才会下载更新包。它**不控制**清单或 `permissions.json` 的下载——这两者每次启动都会强制拉取。
- `Value`：**必须是插件文件夹路径**（结尾不是 `.json`）。客户端会拼接 `permissions.json`
  拉取配置，并按“文件夹路径 + 固定前缀 `ClassIslandFineControl-v` + `Version` + `.cipx`”下载更新包。
  若直接填写 `permissions.json` 直链，配置仍能拉取，但**远程自动升级会失效**。
- 更新包按固定命名放在同一文件夹，例如 `ClassIslandFineControl-v1.5.14.0.cipx`；
  下载完成后由官方在**下次启动**时静默覆盖安装。

### 2. permissions.json

```json
{
  "schemaVersion": 1,
  "defaultPermission": "Operable",
  "selfProtection": true,
  "lockedPlugins": ["cn.finecontrol.permission"],
  "groups": [
    {
      "id": "classisland.general",
      "name": "通用",
      "permission": "Operable",
      "pages": [
        {
          "id": "general",
          "name": "基本",
          "permission": "Operable",
          "items": [
            { "key": "开机自启", "matchMode": "HeaderText", "permission": "Disabled" }
          ]
        }
      ]
    }
  ],
  "pages": [
    { "id": "update", "name": "更新", "permission": "Hidden", "items": [] }
  ]
}
```

**顶层字段**

| 字段 | 说明 |
|---|---|
| `schemaVersion` | 配置格式版本，当前为 1 |
| `defaultPermission` | 未显式列出的设置页按此处理：`Operable` / `Disabled` / `Hidden` |
| `selfProtection` | 自保护总开关，默认 `true`，见下文「自保护」 |
| `lockedPlugins` | 锁定插件 id 列表：其“已启用”开关与“移除”按钮置灰 |
| `groups` | 大页（导航分组）节点；组内 `pages` 为小页，小页内 `items` 为设置项 |
| `pages` | 无分组的独立设置页节点 |

**节点字段**

| 字段 | 说明 |
|---|---|
| `id` / `name` | 设置页/分组的 Id 与显示名（名称仅便于阅读） |
| `permission` | `Operable` / `Disabled` / `Hidden`；大页的 Disabled/Hidden 会级联到组内所有小页 |
| `items[].key` | 设置项匹配键：控件标题文本 / x:Name / AutomationId |
| `items[].matchMode` | `HeaderText`（默认）/ `Name` / `AutomationId` / `Wildcard` |
| `items[].index` | 同名项保底：按页面可视顺序取第 N 个（1 起） |
| `items[].settingsProperty` | 可选，Settings 模型属性名；填写后启用值级锁定 |
| `remark` | 备注，不影响逻辑 |

常用设置页 Id：`general`（基本）、`clock`（时钟）、`storage`（存储）、`privacy`（隐私）、
`components`（组件）、`appearance`（外观）、`window`（窗口）、`notification`（提醒）、
`weather`（天气）、`update`（更新）、`automation`（自动化）、`classisland.plugins`（插件）、
`classisland.themes`（主题）、`management`（集控）、`advanced`（高级）、`about`（关于）。

**应用菜单与托盘菜单**：两者不是真实设置页，用伪页面 Id 配置。
`classisland.appmenu` 的页面级权限控制“三个点”按钮本身，`items` 按菜单标题控制菜单项；
`classisland.traymenu` 的页面级权限控制托盘图标（`Hidden` 隐藏图标、`Disabled` 菜单整体置灰），
`items` 按菜单标题控制托盘菜单项。原生菜单只支持 `HeaderText` 与 `Wildcard` 匹配。

### 3. 拉取失败与离线兜底

- 每次成功拉取并应用后，原始 `permissions.json` 会缓存到插件配置目录
  `PermissionConfigCache.json`；下次启动时**先应用缓存**，再拉取网络最新配置，
  因此一次网络/清单故障不会导致整场管控失效。
- 启动刷新失败会按 0 / 20 / 60 秒退避重试共 3 次；仍失败则继续使用已有配置（缓存或内存），
  可在“精细化管理”状态页点击“更新”手动重试。
- 插件自更新按清单版本号静默下载安装包，由官方在下次启动时安装；该流程不受官方
  “自动更新插件”开关控制（开关仅影响插件市场自身的更新）。

### 4. Config.json（可选）

位于 `<ClassIsland根目录>/Config/Plugins/cn.finecontrol.permission/Config.json`，不存在时自动生成：

| 字段 | 默认值 | 说明 |
|---|---|---|
| `EnableManifestSource` | `true` | 是否启用集控清单驱动；关闭后不加载任何权限配置 |
| `ManifestSourceName` | `FineControlPermissionSource` | 集控清单中的自定义源字段名 |
| `EnableValueLock` | `true` | 是否启用值级锁定；关闭后只做 UI 拦截 |
| `DefaultSettingsPageId` | `management` | 打开设置窗口时默认导航到的页；留空则不强制跳转 |
| `HttpAuthHeaderName` / `HttpAuthHeaderValue` | 空 | 拉取清单/配置时附加的请求头（如 `Authorization`） |

### 5. 配置生成器

仓库中的 `ConfigBuilder/权限配置生成器.html`（不随插件打包）提供可视化配置：左侧选择设置页，
逐项设置三态权限与 `index`，导入/导出**完整**的 `permissions.json`。
值级锁定字段 `settingsProperty` 暂未在生成器中提供，可在导出的 JSON 中手工补充。

### 生效与更新

- 客户端每次启动都会强制拉取并应用最新 `permissions.json`，修改配置**无需递增** Version；
- 只有发布新插件版本时才需递增清单中的 `Version`（四段格式，如 `1.5.14.0`）并上传对应的 `.cipx`；
  上架市场时 Release Tag 也必须使用同名四段版本号。

## 自保护

`permissions.json` 顶层 `selfProtection`（默认 `false`）控制以下动作，开启后生效：

- 置灰本插件详情“更多选项”中的 打包插件 / 打开插件文件夹… / 打开配置文件夹…；
- 隐藏插件页头部的“刷新”入口；
- 锁定本插件自身的“已启用”开关与“移除”按钮（即使 `lockedPlugins` 中未列出本插件）；
- 监视插件安装目录，发现 `.disabled`（撤销启用标识）或 `.uninstall`（标记移除标识）立即删除，
  使取消启用/移除的效果只停留在 UI，重启后插件仍启用；启动时与退出/重启前各补一次清理。

关闭（默认状态）时不执行上述任何动作；`lockedPlugins` 中**其它插件**的锁定不受影响。

## 更新策略

- 本插件的升级统一走**集控私服**：管理员下载新版 `.cipx` 后上传到自己的集控目录，
  客户端按清单 Version 静默下载并重启安装。
- **不使用官方插件市场的自动更新**：请勿在 GitHub 发布更高版本的 Release；
  市场条目仅用于首次分发，后续版本只在集控私服流转。

## 适用场景与合规

- 本插件面向**由管理员统一部署、授权的集控/班牌环境**，用于统一管理系统设置的可见性与可操作性；
  请勿在个人设备上用于限制他人对设备的正常控制。
- 插件不采集、不上传任何个人数据；仅访问配置中指定的集控清单与权限配置 URL。
- 自保护（`selfProtection`）默认关闭；集控场景可由管理员在 `permissions.json` 中显式开启。
- 插件以 MIT 许可证开源，源代码与问题反馈见项目仓库。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。

## 兼容性

- ClassIsland 2.0.x / 2.1.0.x（.NET 8 版），`apiVersion` 2.0.0.0；
- 编译目标：`net8.0-windows`；
- 插件 SDK：`ClassIsland.PluginSdk` 2.1.0.1。
