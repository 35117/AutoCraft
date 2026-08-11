# 自动合成（AutoCraft）— Unturned BepInEx 插件

作者：35117 + Deepseek-v4-flash-0731

Unturned 单人/本地主机自动合成、自动回收、自动修复插件：拾取物品时自动处理，无需手动操作。

## 版本号规则

版本号格式为 `年.月.日.第几版`，例如 `26.8.11.1` 表示 2026 年 8 月 11 日上传的第 1 版。

## 安装

1. 安装 [BepInEx 5](https://docs.bepinex.dev/)（x64 版本）到游戏根目录。
2. 从 [Release](https://github.com/35117/AutoCraft/releases) 下载 `AutoCraftMod-版本号.zip`，解压后把 `BepInEx` 文件夹覆盖到游戏根目录。
3. 启动游戏（单人游戏 / 本地主机）。控制台出现 `[AutoCraft] 配置已加载` 即生效。
4. 推荐配合 [PluginManagerMod](https://github.com/35117/PluginManagerMod) 使用：物品/配方选择器与循环切换按钮需要它。

> 仅对「单人游戏 / 本地主机」生效（需要本机同时是服务器）。连接他人开的专用服务器时不工作，这是 BepInEx 客户端 mod 的正常限制。

## 功能

- **自动合成（主）**：按配方自动合成，每次背包获得物品时检查；材料不足提示、充足自动合成；可选"保留一份原材料"。
- **自动回收（辅）**：指定物品自动拆解成废料（沿用游戏 Salvage 蓝图）；支持白名单/黑名单模式与"保底保留 1 个"。
- **自动修复**：耐久低于阈值自动修复主手/副手/背包物品；材料不足可提示、可切换空手防损坏。

## 配置

`BepInEx/config/com.trae.autorecycle.cfg`，游戏运行中修改约 5 秒后自动生效。

| 节 | 键 | 说明 |
|----|----|----|
| AutoCraft | Enabled | 自动合成总开关 |
| AutoCraft | NotifyCrafted | 合成成功时提示 |
| AutoCraft | NotifyNotEnough | 材料不足时提示 |
| AutoCraft | NotifyTarget | 提示位置：Chat/Popup/Both（循环按钮） |
| AutoCraft | KeepOneMaterial | 保留一份原材料开关 |
| AutoCraft | BlueprintRules | 配方列表 `所属物品ID:配方编号`（配方选择器） |
| AutoRepair | Enabled | 自动修复总开关（每 5 秒检查） |
| AutoRepair | RepairMainHand / RepairOffHand / RepairBackpack | 修复主手 / 副手 / 背包物品 |
| AutoRepair | MinQuality | 修复触发耐久阈值（0-100） |
| AutoRepair | NotifyNoMaterials | 材料不够时提示 |
| AutoRepair | SwitchToEmptyOnCannotRepair | 无法修复时切换空手防损坏 |
| AutoRepair | NotifyTarget | 修复提示位置（循环按钮） |
| General | LogPickedUpItems | 拾取物品 ID 写入日志（仅控制台） |
| General | PickupIdAnnounce | 拾取播报 ID：Off/Popup/Chat（循环按钮） |
| General | NotifyCooldownSeconds | 同类提示最短间隔（秒），0=不限 |
| General | ScanInterval | 定时全量扫描间隔（秒），0=仅拾取触发 |
| RecycleRules | Enabled | 自动回收总开关 |
| RecycleRules | NotifyInChat | 回收成功时提示 |
| RecycleRules | NotifyTarget | 回收提示位置（循环按钮） |
| RecycleRules | RemindNotDismantlable | 拾取无法拆解物品时提醒 |
| RecycleRules | RemindNotEnough | 拾取可拆解但数量不足时提醒 |
| RecycleRules | RecycleMode | Whitelist=只回收列表内 / Blacklist=列表外全部回收（循环按钮） |
| RecycleRules | KeepLastOne | 保底保留最后 1 个不回收 |
| RecycleRules | ItemRules | 对应模式的物品 ID 列表（物品选择器） |

## 编译

环境要求：.NET Framework 4.x、C# 5 语法、csc.exe。

运行 `build.bat`，输出 `BepInEx/Plugins/AutoCraftMod.dll`。
