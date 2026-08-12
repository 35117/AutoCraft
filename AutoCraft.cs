// AutoCraft 自动合成插件
// 作者: 35117 + Deepseek-v4-flash-0731
// 版本 v26.8.12.1
// 功能一（合成，主）：每次背包获得物品后，检查配置中的配方；材料不足按配置提示，充足则自动合成。
//      可选"保留一份原材料"：每种材料至少保留 1 份，保留后仍够一批才合成。
//      合成界面 Alt+左键点击配方可标记/取消标记自动合成。
// 功能二（回收，辅）：物品进入背包后自动检测其 Salvage（拆解）蓝图并回收成废料；
//      支持白名单/黑名单模式与"保底保留 1 个"；背包界面 Alt+左键点击物品可标记/取消标记自动回收。
// 功能三（修复）：耐久低于阈值时自动修复主手/副手/背包物品；手持主副手时每 1 秒检测，未手持时每 5 秒；
//      未手持时不提示（仍自动修复）；材料不足时手持物品才提示，并可切换空手防损坏。
// 适用：单人游戏 / 本地主机（Provider.isServer 为真时生效）。专用服务器无效。
// 配置：BepInEx\config\com.trae.autorecycle.cfg
//       ItemRules 带 "Unturned.ItemList" 标签，BlueprintRules 带 "Unturned.BlueprintList" 标签；
//       Off/Popup/Chat/Both 等字符串选项带 "Unturned.Cycle" 标签，可在插件管理器中用按钮循环切换；
//       全部配置带 "Unturned.Category:分类名" 标签，插件管理器左侧分类导航按 通用设置/自动合成/自动回收/自动修复 分组显示。
// 编译目标：.NET Framework 4.x（BepInEx 5），C# 5 语法。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace AutoCraft
{
    [BepInPlugin("com.trae.autorecycle", "自动合成", "26.8.12.1")]
    public class AutoCraftPlugin : BaseUnityPlugin
    {
        // 供 Harmony 补丁访问插件实例与日志
        internal static AutoCraftPlugin Instance;

        internal static void LogErrorStatic(string message)
        {
            if (Instance != null)
            {
                Instance.Logger.LogError(message);
            }
            else
            {
                UnityEngine.Debug.LogError(message);
            }
        }
        private const string GeneralSection = "General";
        private const string CraftSection = "AutoCraft";
        private const string RecycleSection = "RecycleRules";
        private const string RepairSection = "AutoRepair";

        // PluginManager 配置分类标签（界面左侧分类导航按此分组显示）
        private const string CatGeneral = "Unturned.Category:通用设置";
        private const string CatCraft = "Unturned.Category:自动合成";
        private const string CatRecycle = "Unturned.Category:自动回收";
        private const string CatRepair = "Unturned.Category:自动修复";

        // 生成带 PluginManager 分类标签的配置描述（额外标签如 Unturned.ItemList / Unturned.Cycle 原样合并）
        private static ConfigDescription Category(string categoryTag, string description, params object[] extraTags)
        {
            object[] tags;
            if (extraTags == null || extraTags.Length == 0)
            {
                tags = new object[] { categoryTag };
            }
            else
            {
                tags = new object[extraTags.Length + 1];
                tags[0] = categoryTag;
                for (int i = 0; i < extraTags.Length; i++)
                {
                    tags[i + 1] = extraTags[i];
                }
            }
            return new ConfigDescription(description, null, tags);
        }

        private static ConfigDescription Category(string categoryTag, string description, AcceptableValueBase acceptableValues, params object[] extraTags)
        {
            object[] tags;
            if (extraTags == null || extraTags.Length == 0)
            {
                tags = new object[] { categoryTag };
            }
            else
            {
                tags = new object[extraTags.Length + 1];
                tags[0] = categoryTag;
                for (int i = 0; i < extraTags.Length; i++)
                {
                    tags[i + 1] = extraTags[i];
                }
            }
            return new ConfigDescription(description, acceptableValues, tags);
        }

        // 修复蓝图分类标签（Repair）
        private static readonly Guid RepairCategoryGuid = new Guid("732ee6ffeb18418985cf4f9fde33dd11");

        // ---------- 配置项 ----------
        // 通用
        private ConfigEntry<bool> cfgLogPickedUpItems;
        private ConfigEntry<string> cfgPickupIdAnnounce;
        private ConfigEntry<float> cfgNotifyCooldownSeconds;
        private ConfigEntry<float> cfgScanInterval;
        // 合成
        private ConfigEntry<bool> cfgCraftEnabled;
        private ConfigEntry<bool> cfgCraftNotifyCrafted;
        private ConfigEntry<bool> cfgCraftNotifyNotEnough;
        private ConfigEntry<string> cfgCraftNotifyTarget;
        private ConfigEntry<bool> cfgCraftKeepOneMaterial;
        private ConfigEntry<string> cfgBlueprintRules;
        // 回收
        private ConfigEntry<bool> cfgRecycleEnabled;
        private ConfigEntry<bool> cfgRecycleNotifyInChat;
        private ConfigEntry<string> cfgRecycleNotifyTarget;
        private ConfigEntry<bool> cfgRemindNotDismantlable;
        private ConfigEntry<bool> cfgRemindNotEnough;
        private ConfigEntry<string> cfgRecycleMode;
        private ConfigEntry<bool> cfgKeepLastOne;
        private ConfigEntry<string> cfgItemRules;
        // 修复
        private ConfigEntry<bool> cfgRepairEnabled;
        private ConfigEntry<bool> cfgRepairMainHand;
        private ConfigEntry<bool> cfgRepairOffHand;
        private ConfigEntry<bool> cfgRepairBackpack;
        private ConfigEntry<int> cfgRepairMinQuality;
        private ConfigEntry<bool> cfgRepairNotifyNoMaterials;
        private ConfigEntry<bool> cfgRepairSwitchToEmpty;
        private ConfigEntry<string> cfgRepairNotifyTarget;

        // ---------- 运行时数据 ----------
        private readonly HashSet<ushort> itemRules = new HashSet<ushort>();
        private readonly Dictionary<ushort, SalvageInfo> salvageCache = new Dictionary<ushort, SalvageInfo>();
        private readonly HashSet<ushort> noSalvageCache = new HashSet<ushort>();
        private readonly HashSet<ushort> warnedNotDismantlable = new HashSet<ushort>();
        // 合成配方：原始规则（纯数字，Awake 阶段安全解析）与懒解析后的蓝图列表
        private readonly List<CraftRule> craftRules = new List<CraftRule>();
        private readonly List<Blueprint> craftBlueprints = new List<Blueprint>();
        private bool craftResolved;
        // 修复蓝图缓存
        private readonly Dictionary<ushort, Blueprint> repairBlueprintCache = new Dictionary<ushort, Blueprint>();
        private readonly HashSet<ushort> noRepairCache = new HashSet<ushort>();
        // 提示冷却
        private readonly Dictionary<string, float> lastNotifyTimes = new Dictionary<string, float>();

        private Player localPlayer;
        private bool subscribed;
        private bool isProcessing;
        private DateTime lastConfigWriteTime;
        private float nextConfigCheckTime;
        private float nextScanTime;
        private float nextRepairTime;
        // 待处理物品（进入背包后延迟一小段时间再处理，避免拾取动画/数据未落定的问题）
        private readonly Dictionary<ushort, float> pendingItemAddTimes = new Dictionary<ushort, float>();

        private void Awake()
        {
            try
            {
                // ---- 通用配置 ----
                cfgLogPickedUpItems = Config.Bind(GeneralSection, "LogPickedUpItems", false, Category(CatGeneral, "开启后每次拾取物品都会在 BepInEx 日志中打印物品 ID，方便查找需要配置的物品"));
                cfgPickupIdAnnounce = Config.Bind(GeneralSection, "PickupIdAnnounce", "Off",
                    Category(CatGeneral, "拾取物品时播报 ID：Off=关闭，Popup=屏幕中下方提示栏，Chat=聊天栏",
                        new AcceptableValueList<string>("Off", "Popup", "Chat"),
                        "Unturned.Cycle"));
                cfgNotifyCooldownSeconds = Config.Bind(GeneralSection, "NotifyCooldownSeconds", 5f, Category(CatGeneral, "同类提示最短间隔（秒），用于防止连捡多个物品时刷屏；0=不限制"));
                cfgScanInterval = Config.Bind(GeneralSection, "ScanInterval", 0f, Category(CatGeneral, "定时全量扫描间隔（秒），自动检查库存执行回收/合成，兜住所有来源；0=仅拾取/获得时触发"));

                // ---- 合成配置（主功能）----
                cfgCraftEnabled = Config.Bind(CraftSection, "Enabled", true, Category(CatCraft, "自动合成总开关"));
                cfgCraftNotifyCrafted = Config.Bind(CraftSection, "NotifyCrafted", true, Category(CatCraft, "合成成功时是否提示"));
                cfgCraftNotifyNotEnough = Config.Bind(CraftSection, "NotifyNotEnough", true, Category(CatCraft, "材料不足时是否提示还缺什么"));
                cfgCraftNotifyTarget = Config.Bind(CraftSection, "NotifyTarget", "Chat",
                    Category(CatCraft, "合成提示位置：Chat=聊天栏，Popup=屏幕中下方提示栏，Both=两者都显示",
                        new AcceptableValueList<string>("Popup", "Chat", "Both"),
                        "Unturned.Cycle"));
                cfgCraftKeepOneMaterial = Config.Bind(CraftSection, "KeepOneMaterial", true, Category(CatCraft, "保留一份原材料开关：每种材料至少保留 1 份，保留后仍够一批才会合成"));
                cfgBlueprintRules = Config.Bind(CraftSection, "BlueprintRules", "",
                    Category(CatCraft, "需要自动合成的配方列表，格式: 所属物品ID:配方编号，多条用英文逗号分隔。每次背包获得物品时自动检查：材料不足按 NotifyNotEnough 提示，材料充足自动合成。可在插件管理器中用配方选择器可视化编辑",
                        null,
                        "Unturned.BlueprintList"));

                // ---- 回收配置（辅助功能）----
                cfgRecycleEnabled = Config.Bind(RecycleSection, "Enabled", true, Category(CatRecycle, "自动回收总开关"));
                cfgRecycleNotifyInChat = Config.Bind(RecycleSection, "NotifyInChat", true, Category(CatRecycle, "回收成功时是否提示"));
                cfgRecycleNotifyTarget = Config.Bind(RecycleSection, "NotifyTarget", "Chat",
                    Category(CatRecycle, "回收提示位置：Chat=聊天栏，Popup=屏幕中下方提示栏，Both=两者都显示",
                        new AcceptableValueList<string>("Popup", "Chat", "Both"),
                        "Unturned.Cycle"));
                cfgRemindNotDismantlable = Config.Bind(RecycleSection, "RemindNotDismantlable", true, Category(CatRecycle, "拾取配置中「无法拆解」的物品时，是否提醒（每个物品每条会话只提醒一次）"));
                cfgRemindNotEnough = Config.Bind(RecycleSection, "RemindNotEnough", true, Category(CatRecycle, "拾取可拆解但数量不足的物品时，是否提醒还需多少个才能拆解"));
                cfgRecycleMode = Config.Bind(RecycleSection, "RecycleMode", "Whitelist",
                    Category(CatRecycle, "回收模式：Whitelist=只回收 ItemRules 中的物品，Blacklist=除 ItemRules 外的可拆解物品全部回收",
                        new AcceptableValueList<string>("Whitelist", "Blacklist"),
                        "Unturned.Cycle"));
                cfgKeepLastOne = Config.Bind(RecycleSection, "KeepLastOne", true, Category(CatRecycle, "保底保留：每个物品始终保留最后 1 个不回收（数量只有 1 时不回收）"));
                cfgItemRules = Config.Bind(RecycleSection, "ItemRules", "6666, 6667",
                    Category(CatRecycle, "回收模式对应的物品 ID 列表，多个用英文逗号分隔，只填 ID。拆解所需数量与产物自动从该物品的游戏 Salvage（拆解）蓝图检测；没有拆解蓝图的物品只会提醒不会回收",
                        null,
                        "Unturned.ItemList"));

                // ---- 修复配置 ----
                cfgRepairEnabled = Config.Bind(RepairSection, "Enabled", true, Category(CatRepair, "自动修复总开关（每 5 秒检查一次耐久）"));
                cfgRepairMainHand = Config.Bind(RepairSection, "RepairMainHand", true, Category(CatRepair, "自动修复主手（页0）物品"));
                cfgRepairOffHand = Config.Bind(RepairSection, "RepairOffHand", true, Category(CatRepair, "自动修复副手（页1）物品"));
                cfgRepairBackpack = Config.Bind(RepairSection, "RepairBackpack", false, Category(CatRepair, "自动修复背包内物品（背包/背心/衬衫/裤子）"));
                cfgRepairMinQuality = Config.Bind(RepairSection, "MinQuality", 50,
                    Category(CatRepair, "修复触发的最低耐久阈值：耐久低于该值时尝试修复（0-100）",
                        new AcceptableValueRange<int>(0, 100)));
                cfgRepairNotifyNoMaterials = Config.Bind(RepairSection, "NotifyNoMaterials", true, Category(CatRepair, "耐久低于阈值但修复材料不够时，是否提示"));
                cfgRepairSwitchToEmpty = Config.Bind(RepairSection, "SwitchToEmptyOnCannotRepair", true, Category(CatRepair, "耐久低于阈值但无法修复时，是否直接切换空手防止物品损坏"));
                cfgRepairNotifyTarget = Config.Bind(RepairSection, "NotifyTarget", "Chat",
                    Category(CatRepair, "修复提示位置：Chat=聊天栏，Popup=屏幕中下方提示栏，Both=两者都显示",
                        new AcceptableValueList<string>("Popup", "Chat", "Both"),
                        "Unturned.Cycle"));

                lastConfigWriteTime = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                ReloadRules();

                Player.onPlayerCreated += OnPlayerCreated;
                Player.onPlayerDestroyed += OnPlayerDestroyed;
                Level.onLevelLoaded += OnLevelLoaded;

                Instance = this;
                Harmony.CreateAndPatchAll(typeof(AutoCraftPlugin).Assembly);

                Logger.LogInfo("[AutoCraft] 插件启动完成");
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 插件初始化异常：" + exception);
            }
        }

        private void OnDestroy()
        {
            Player.onPlayerCreated -= OnPlayerCreated;
            Player.onPlayerDestroyed -= OnPlayerDestroyed;
            Level.onLevelLoaded -= OnLevelLoaded;
            Unsubscribe();
            Instance = null;
        }

        private void OnPlayerDestroyed(Player player)
        {
            // 玩家死亡/重生会创建新 Player，先解绑，让 Update 兜底重新订阅
            Unsubscribe();
            localPlayer = null;
        }

        private void OnPlayerCreated(Player player)
        {
            Unsubscribe();
            localPlayer = player;
            if (player != null && player.inventory != null && Provider.isServer)
            {
                player.inventory.onInventoryAdded += OnItemAdded;
                subscribed = true;
                Logger.LogInfo("[AutoCraft] 已监听本地玩家背包，自动合成/回收/修复已启用");
                ScanExistingItems(false);
            }
        }

        private void OnLevelLoaded(int level)
        {
            Unsubscribe();
            localPlayer = null;
        }

        private void Update()
        {
            try
            {
                // 专用服务器上不运行（本插件只支持单人/本地主机）
                if (Dedicator.IsDedicatedServer)
                {
                    return;
                }

                // 兜底：如果 onPlayerCreated 之前已错过（例如插件后装），轮询订阅
                if (!subscribed && Provider.isServer)
                {
                    Player player = Player.LocalPlayer;
                    if (player != null && player.inventory != null)
                    {
                        localPlayer = player;
                        player.inventory.onInventoryAdded += OnItemAdded;
                        subscribed = true;
                        Logger.LogInfo("[AutoCraft] 已监听本地玩家背包，自动合成/回收/修复已启用");
                        ScanExistingItems(false);
                    }
                }

                // 配置文件热重载：外部修改 .cfg 后约 5 秒内自动生效
                if (Time.realtimeSinceStartup >= nextConfigCheckTime)
                {
                    nextConfigCheckTime = Time.realtimeSinceStartup + 5f;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(Config.ConfigFilePath) != lastConfigWriteTime)
                        {
                            Config.Reload();
                            ReloadRules();
                            lastConfigWriteTime = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                            // 配置被修改后，动态检查新规则/配方（存量足够直接回收/合成）
                            if (subscribed && localPlayer != null && localPlayer.inventory != null)
                            {
                                ScanExistingItems(false);
                            }
                        }
                    }
                    catch
                    {
                        // 文件可能暂时被占用，下次再试
                    }
                }

                // 定时全量扫描（ScanInterval > 0 时启用）
                if (subscribed && cfgScanInterval.Value > 0f && Time.realtimeSinceStartup >= nextScanTime)
                {
                    nextScanTime = Time.realtimeSinceStartup + cfgScanInterval.Value;
                    ScanExistingItems(false);
                }

                // 处理进入背包满 0.3 秒的物品（合成/回收只在物品真正进入背包后执行）
                if (pendingItemAddTimes.Count > 0 && subscribed && localPlayer != null)
                {
                    List<ushort> ready = new List<ushort>();
                    float now = Time.realtimeSinceStartup;
                    foreach (KeyValuePair<ushort, float> pair in pendingItemAddTimes)
                    {
                        if (now - pair.Value >= 0.3f)
                        {
                            ready.Add(pair.Key);
                        }
                    }
                    foreach (ushort id in ready)
                    {
                        pendingItemAddTimes.Remove(id);
                        ProcessItem(id);
                    }
                }

                // 自动修复定时检查：手持主/副手武器时每 1 秒，否则每 5 秒
                if (subscribed && cfgRepairEnabled.Value && Time.realtimeSinceStartup >= nextRepairTime)
                {
                    float interval = IsHoldingMainOrOffHand() ? 1f : 5f;
                    nextRepairTime = Time.realtimeSinceStartup + interval;
                    TryRepairAll();
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 主循环异常：" + exception);
            }
        }

        private void Unsubscribe()
        {
            if (localPlayer != null && localPlayer.inventory != null)
            {
                localPlayer.inventory.onInventoryAdded -= OnItemAdded;
            }
            subscribed = false;
        }

        // ---------- 配置解析 ----------

        private void ReloadRules()
        {
            itemRules.Clear();
            salvageCache.Clear();
            noSalvageCache.Clear();
            warnedNotDismantlable.Clear();
            craftRules.Clear();
            craftBlueprints.Clear();
            craftResolved = false;
            repairBlueprintCache.Clear();
            noRepairCache.Clear();

            // 回收规则：物品 ID 列表（纯数字解析，不依赖游戏资产）
            string rawItems = cfgItemRules.Value;
            if (!string.IsNullOrWhiteSpace(rawItems))
            {
                foreach (string rule in rawItems.Split(','))
                {
                    string trimmed = rule != null ? rule.Trim() : string.Empty;
                    ushort itemId;
                    if (trimmed.Length > 0 && ushort.TryParse(trimmed, out itemId))
                    {
                        itemRules.Add(itemId);
                    }
                    else if (trimmed.Length > 0)
                    {
                        Logger.LogWarning("[AutoCraft] 无法解析回收规则，已跳过: " + trimmed);
                    }
                }
            }

            // 合成配方：所属物品ID:配方编号（先只做纯数字解析，蓝图等进游戏资产加载后再懒解析）
            string rawBlueprints = cfgBlueprintRules.Value;
            if (!string.IsNullOrWhiteSpace(rawBlueprints))
            {
                foreach (string rule in rawBlueprints.Split(','))
                {
                    string trimmed = rule != null ? rule.Trim() : string.Empty;
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                    string[] parts = trimmed.Split(':');
                    ushort ownerId;
                    int index;
                    if (parts.Length >= 2 &&
                        ushort.TryParse(parts[0].Trim(), out ownerId) &&
                        int.TryParse(parts[1].Trim(), out index))
                    {
                        craftRules.Add(new CraftRule(ownerId, index));
                    }
                    else
                    {
                        Logger.LogWarning("[AutoCraft] 无法解析配方，已跳过: " + trimmed);
                    }
                }
            }

            Logger.LogInfo("[AutoCraft] 配置已加载，合成配方 " + craftRules.Count + " 条，回收规则 " + itemRules.Count + " 条");
        }

        // 懒解析合成配方：Awake 阶段资产未加载，必须等到进游戏后再解析；失败规则仅提示一次
        private void EnsureCraftBlueprintsResolved()
        {
            if (craftResolved || craftRules.Count == 0)
            {
                return;
            }
            try
            {
                craftBlueprints.Clear();
                bool allResolved = true;
                foreach (CraftRule rule in craftRules)
                {
                    Blueprint blueprint = ResolveBlueprint(rule.OwnerId, rule.Index);
                    if (blueprint == null)
                    {
                        allResolved = false;
                        Logger.LogWarning("[AutoCraft] 配方 " + rule.OwnerId + ":" + rule.Index + " 无效（物品或配方不存在），已跳过");
                        continue;
                    }
                    craftBlueprints.Add(blueprint);
                }
                craftResolved = true;
                if (allResolved)
                {
                    Logger.LogInfo("[AutoCraft] 合成配方解析完成，共 " + craftBlueprints.Count + " 条");
                }
            }
            catch (Exception exception)
            {
                // 资产可能仍未就绪，下次事件再试
                Logger.LogWarning("[AutoCraft] 配方解析暂不可用（资产未加载？），稍后重试: " + exception.Message);
            }
        }

        // 根据 所属物品ID:配方编号 解析蓝图；编号即 Blueprint.Index（列表中顺序）
        private Blueprint ResolveBlueprint(ushort ownerId, int index)
        {
            ItemAsset asset = Assets.find(EAssetType.ITEM, ownerId) as ItemAsset;
            if (asset == null || asset.blueprints == null || asset.blueprints.Count <= index)
            {
                return null;
            }
            foreach (Blueprint blueprint in asset.blueprints)
            {
                if (blueprint != null && blueprint.Index == (byte)index)
                {
                    return blueprint;
                }
            }
            return asset.blueprints[index];
        }

        // 查找物品的修复蓝图（Operation=RepairTargetItem 或分类标签为 Repair）
        private Blueprint FindRepairBlueprint(ushort itemId)
        {
            Blueprint cached;
            if (repairBlueprintCache.TryGetValue(itemId, out cached))
            {
                return cached;
            }
            if (noRepairCache.Contains(itemId))
            {
                return null;
            }
            ItemAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            if (asset != null && asset.blueprints != null)
            {
                foreach (Blueprint blueprint in asset.blueprints)
                {
                    if (blueprint == null)
                    {
                        continue;
                    }
                    if (blueprint.Operation == EBlueprintOperation.RepairTargetItem ||
                        blueprint.CategoryTagRef.Guid == RepairCategoryGuid)
                    {
                        repairBlueprintCache[itemId] = blueprint;
                        return blueprint;
                    }
                }
            }
            noRepairCache.Add(itemId);
            return null;
        }

        // ---------- 事件入口 ----------

        // 进游戏 / 配置重载 / 定时扫描时，对规则/配方做一次清点（不弹不足提醒，但会回收存量/合成现成配方）
        private void ScanExistingItems(bool showReminders)
        {
            if (cfgCraftEnabled.Value)
            {
                EnsureCraftBlueprintsResolved();
                foreach (Blueprint blueprint in craftBlueprints)
                {
                    TryCraftBlueprint(blueprint, false);
                }
            }
            if (cfgRecycleEnabled.Value)
            {
                if (IsWhitelistMode())
                {
                    foreach (ushort itemId in itemRules)
                    {
                        TryRecycle(itemId, showReminders);
                    }
                }
                else
                {
                    // 黑名单模式：扫描背包中所有物品
                    foreach (ushort itemId in CollectInventoryItemIds())
                    {
                        TryRecycle(itemId, showReminders);
                    }
                }
            }
        }

        private void OnItemAdded(byte page, byte index, ItemJar jar)
        {
            if (isProcessing)
            {
                return;
            }
            if (!Provider.isServer)
            {
                return;
            }
            if (jar == null || jar.item == null)
            {
                return;
            }
            // 只处理玩家自己的背包页（0-6）。打开箱子时物品加入的是 STORAGE 页(7)，并未拾取，必须忽略
            if (page > PlayerInventory.PANTS)
            {
                return;
            }
            try
            {
                if (cfgLogPickedUpItems.Value)
                {
                    ItemAsset asset = jar.item.GetAsset();
                    Logger.LogInfo("[AutoCraft] 拾取物品 ID=" + jar.item.id + (asset != null ? " (" + asset.FriendlyName + ")" : string.Empty));
                }
                if (!string.Equals(cfgPickupIdAnnounce.Value, "Off", StringComparison.OrdinalIgnoreCase) &&
                    ShouldNotify("announce:" + jar.item.id))
                {
                    AnnouncePickedUpItem(jar.item, cfgPickupIdAnnounce.Value);
                }
                // 合成/回收延迟到物品进入背包 0.3 秒后再处理
                if (!pendingItemAddTimes.ContainsKey(jar.item.id))
                {
                    pendingItemAddTimes[jar.item.id] = Time.realtimeSinceStartup;
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 处理拾取事件异常：" + exception);
            }
        }

        // 物品进入背包后统一处理：先查合成配方，再查回收
        private void ProcessItem(ushort itemId)
        {
            if (!subscribed || localPlayer == null)
            {
                return;
            }
            try
            {
                if (cfgCraftEnabled.Value)
                {
                    EnsureCraftBlueprintsResolved();
                    foreach (Blueprint blueprint in craftBlueprints)
                    {
                        if (blueprint != null && BlueprintUsesItem(blueprint, itemId))
                        {
                            TryCraftBlueprint(blueprint, true);
                        }
                    }
                }
                if (cfgRecycleEnabled.Value && IsRecycleItem(itemId))
                {
                    TryRecycle(itemId, true);
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 处理物品 " + itemId + " 异常：" + exception);
            }
        }

        // 按配置在聊天栏或屏幕中下方提示栏播报捡起的物品 ID
        private void AnnouncePickedUpItem(Item item, string target)
        {
            if (item == null)
            {
                return;
            }
            ItemAsset asset = item.GetAsset();
            string name = asset != null ? asset.FriendlyName : item.id.ToString();
            SendNotify(target, "拾取物品 ID " + item.id + " (" + name + ")", Color.cyan);
        }

        // 统一提示出口：Chat=聊天栏（带插件前缀），Popup=屏幕中下方提示栏（简洁无前缀），Both=两者
        private void SendNotify(string target, string message, Color color)
        {
            bool toPopup = string.Equals(target, "Popup", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(target, "Both", StringComparison.OrdinalIgnoreCase);
            bool toChat = string.Equals(target, "Chat", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(target, "Both", StringComparison.OrdinalIgnoreCase);
            if (toPopup)
            {
                PlayerUI.message(EPlayerMessage.NPC_CUSTOM, message, 3f);
            }
            if (toChat)
            {
                ChatManager.serverSendMessage("[AutoCraft] " + message, color);
            }
        }

        // 提示冷却：同一 key 的提示在冷却时间内只发一次
        private bool ShouldNotify(string key)
        {
            float cooldown = cfgNotifyCooldownSeconds.Value;
            if (cooldown <= 0f)
            {
                return true;
            }
            float now = Time.realtimeSinceStartup;
            float last;
            if (lastNotifyTimes.TryGetValue(key, out last) && now - last < cooldown)
            {
                return false;
            }
            lastNotifyTimes[key] = now;
            return true;
        }

        // ---------- 合成 ----------

        // 检查并尝试自动合成一个配方
        private void TryCraftBlueprint(Blueprint blueprint, bool showNotifications)
        {
            if (blueprint == null || localPlayer == null || localPlayer.crafting == null || localPlayer.inventory == null)
            {
                return;
            }
            try
            {
                // 技能不足的配方不自动合成（避免每次材料够时反复失败），也不弹提示
                if (blueprint.RequiresSkill)
                {
                    int skillLevel = blueprint.GetPlayerSkillLevel(localPlayer);
                    if (skillLevel < blueprint.level)
                    {
                        return;
                    }
                }

                string missing;
                int crafts = ComputeCraftableCount(blueprint, out missing);
                if (crafts >= 1)
                {
                    isProcessing = true;
                    try
                    {
                        // 按计算出的可合成次数逐次请求（asManyAsPossible=false 每次只合一批，尊重保留材料）
                        for (int i = 0; i < crafts; i++)
                        {
                            localPlayer.crafting.SendRequestToCraft(blueprint, false);
                        }
                        if (showNotifications && cfgCraftNotifyCrafted.Value)
                        {
                            string outputName = GetBlueprintOutputName(blueprint);
                            SendNotify(cfgCraftNotifyTarget.Value, crafts > 1 ? "已自动合成 " + crafts + " × " + outputName : "已自动合成 " + outputName, Color.green);
                        }
                        Logger.LogInfo("[AutoCraft] 已请求自动合成配方 " + GetBlueprintOutputName(blueprint) + " ×" + crafts);
                    }
                    finally
                    {
                        isProcessing = false;
                    }
                }
                else if (showNotifications && cfgCraftNotifyNotEnough.Value)
                {
                    Asset ownerAsset = blueprint.GetOwnerAsset();
                    string key = "craft:" + (ownerAsset != null ? ownerAsset.id.ToString() : "?") + ":" + blueprint.Index;
                    if (ShouldNotify(key))
                    {
                        string message = "配方 " + GetBlueprintOutputName(blueprint) + " 材料不足";
                        if (missing != null && missing.Length > 0)
                        {
                            message += "，" + missing;
                        }
                        SendNotify(cfgCraftNotifyTarget.Value, message, Color.yellow);
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 自动合成异常：" + exception);
            }
        }

        // 该配方是否用到某物品（作为消耗材料或工具）
        private bool BlueprintUsesItem(Blueprint blueprint, ushort itemId)
        {
            if (blueprint == null || blueprint.supplies == null)
            {
                return false;
            }
            foreach (BlueprintSupply supply in blueprint.supplies)
            {
                if (supply != null)
                {
                    ItemAsset supplyAsset = supply.FindItemAsset();
                    if (supplyAsset != null && supplyAsset.id == itemId)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // 计算配方当前可合成次数（考虑"保留一份原材料"）；不足时输出缺什么
        // 可合成次数 = 每种消耗材料 (持有数 - 保留数) / 单次消耗量 的最小值
        private int ComputeCraftableCount(Blueprint blueprint, out string missingDescription)
        {
            missingDescription = string.Empty;
            if (blueprint == null || blueprint.supplies == null || blueprint.supplies.Length == 0)
            {
                return 1;
            }

            int reserve = cfgCraftKeepOneMaterial.Value ? 1 : 0;
            int craftable = int.MaxValue;
            foreach (BlueprintSupply supply in blueprint.supplies)
            {
                if (supply == null)
                {
                    continue;
                }
                ItemAsset supplyAsset = supply.FindItemAsset();
                if (supplyAsset == null)
                {
                    continue;
                }
                int have = CountItemAll(supplyAsset.id);
                if (!supply.ShouldConsume)
                {
                    // 工具等不消耗的材料：必须持有
                    if (have < 1)
                    {
                        missingDescription = "缺少工具 " + supplyAsset.FriendlyName;
                        return 0;
                    }
                    continue;
                }

                int availableForCraft = have - reserve;
                if (availableForCraft < supply.amount)
                {
                    int missing = supply.amount - availableForCraft;
                    missingDescription = "还缺 " + missing + " × " + supplyAsset.FriendlyName + (reserve > 0 ? "（需保留 " + reserve + "）" : string.Empty);
                    return 0;
                }
                int perMaterialCrafts = availableForCraft / supply.amount;
                if (perMaterialCrafts < craftable)
                {
                    craftable = perMaterialCrafts;
                }
            }

            if (craftable == int.MaxValue)
            {
                // 只有工具没有消耗材料（理论不出现）
                return 1;
            }
            return craftable;
        }

        private string GetBlueprintOutputName(Blueprint blueprint)
        {
            if (blueprint == null || blueprint.outputs == null || blueprint.outputs.Length < 1)
            {
                return "未知";
            }
            BlueprintOutput output = blueprint.outputs[0];
            ItemAsset outputAsset = output != null ? output.FindItemAsset() : null;
            return outputAsset != null ? outputAsset.FriendlyName : "未知";
        }

        // ---------- 修复 ----------

        // 定时检查：对主手/副手/背包中耐久低于阈值的物品自动修复
        private void TryRepairAll()
        {
            if (localPlayer == null || localPlayer.inventory == null || localPlayer.equipment == null)
            {
                return;
            }
            try
            {
                HashSet<ushort> lowQualityIds = new HashSet<ushort>();
                if (cfgRepairMainHand.Value)
                {
                    CollectLowQualityIds((byte)(PlayerInventory.BACKPACK - 3), lowQualityIds); // 页0 = 主手
                }
                if (cfgRepairOffHand.Value)
                {
                    CollectLowQualityIds((byte)(PlayerInventory.BACKPACK - 2), lowQualityIds); // 页1 = 副手
                }
                if (cfgRepairBackpack.Value)
                {
                    for (byte page = PlayerInventory.BACKPACK; page <= PlayerInventory.PANTS; page++)
                    {
                        CollectLowQualityIds(page, lowQualityIds);
                    }
                }

                foreach (ushort itemId in lowQualityIds)
                {
                    TryRepairItemType(itemId);
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("[AutoCraft] 自动修复异常：" + exception);
            }
        }

        // 收集指定页中耐久低于阈值的物品 ID
        private void CollectLowQualityIds(byte page, HashSet<ushort> result)
        {
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return;
            }
            Items items = localPlayer.inventory.items[page];
            if (items == null)
            {
                return;
            }
            int threshold = cfgRepairMinQuality.Value;
            for (byte index = 0; index < items.getItemCount(); index++)
            {
                ItemJar jar = items.getItem(index);
                if (jar != null && jar.item != null && jar.item.quality < threshold)
                {
                    result.Add(jar.item.id);
                }
            }
        }

        // 尝试修复某一类物品（同一类型的多个低耐久物品逐个修复）
        private void TryRepairItemType(ushort itemId)
        {
            Blueprint repairBlueprint = FindRepairBlueprint(itemId);
            if (repairBlueprint != null && repairBlueprint.RequiresSkill)
            {
                int skillLevel = repairBlueprint.GetPlayerSkillLevel(localPlayer);
                if (skillLevel < repairBlueprint.level)
                {
                    // 技能不足，无法修复（也不提示）
                    return;
                }
            }

            bool insufficient = false;
            if (repairBlueprint != null)
            {
                int iterations = 0;
                while (iterations < 10 && HasLowQualityItem(itemId))
                {
                    string missing;
                    int craftable = ComputeCraftableCount(repairBlueprint, out missing);
                    if (craftable < 1)
                    {
                        insufficient = true;
                        break;
                    }
                    isProcessing = true;
                    try
                    {
                        localPlayer.crafting.SendRequestToCraft(repairBlueprint, false);
                    }
                    finally
                    {
                        isProcessing = false;
                    }
                    iterations++;
                }
            }
            else
            {
                // 无修复蓝图 = 永远无法修复
                insufficient = true;
            }

            if (!insufficient || !HasLowQualityItem(itemId))
            {
                return;
            }

            // 材料不足（或无修复蓝图）且仍存在低耐久物品：仅当该物品正手持时才提示（未手持仍会静默尝试修复）
            if (cfgRepairNotifyNoMaterials.Value && IsEquippedAtRisk(itemId) && ShouldNotify("repair:" + itemId))
            {
                ItemAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
                string name = asset != null ? asset.FriendlyName : itemId.ToString();
                SendNotify(cfgRepairNotifyTarget.Value, "物品 " + name + " 耐久过低但修复材料不足，无法修复", Color.yellow);
            }
            if (cfgRepairSwitchToEmpty.Value && IsEquippedAtRisk(itemId))
            {
                // 切换空手防止物品损坏
                localPlayer.equipment.dequip();
            }
        }

        // 是否仍存在耐久低于阈值的该物品（范围受 RepairMainHand/OffHand/Backpack 控制）
        private bool HasLowQualityItem(ushort itemId)
        {
            if (cfgRepairMainHand.Value && HasLowQualityInPage((byte)(PlayerInventory.BACKPACK - 3), itemId))
            {
                return true;
            }
            if (cfgRepairOffHand.Value && HasLowQualityInPage((byte)(PlayerInventory.BACKPACK - 2), itemId))
            {
                return true;
            }
            if (cfgRepairBackpack.Value)
            {
                for (byte page = PlayerInventory.BACKPACK; page <= PlayerInventory.PANTS; page++)
                {
                    if (HasLowQualityInPage(page, itemId))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool HasLowQualityInPage(byte page, ushort itemId)
        {
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return false;
            }
            Items items = localPlayer.inventory.items[page];
            if (items == null)
            {
                return false;
            }
            int threshold = cfgRepairMinQuality.Value;
            for (byte index = 0; index < items.getItemCount(); index++)
            {
                ItemJar jar = items.getItem(index);
                if (jar != null && jar.item != null && jar.item.id == itemId && jar.item.quality < threshold)
                {
                    return true;
                }
            }
            return false;
        }

        // 该物品是否正装备在手上（主手/副手）且耐久低于阈值
        private bool IsEquippedAtRisk(ushort itemId)
        {
            if (localPlayer == null || localPlayer.equipment == null || localPlayer.inventory == null)
            {
                return false;
            }
            byte page = localPlayer.equipment.equippedPage;
            if (page != (byte)(PlayerInventory.BACKPACK - 3) && page != (byte)(PlayerInventory.BACKPACK - 2))
            {
                return false; // 只关心主手/副手
            }
            Items items = localPlayer.inventory.items[page];
            if (items == null || items.getItemCount() < 1)
            {
                return false;
            }
            ItemJar jar = items.getItem(0);
            return jar != null && jar.item != null && jar.item.id == itemId && jar.item.quality < cfgRepairMinQuality.Value;
        }

        // 是否正手持主手/副手武器（决定修复检测间隔与是否提示）
        private bool IsHoldingMainOrOffHand()
        {
            if (localPlayer == null || localPlayer.equipment == null)
            {
                return false;
            }
            byte page = localPlayer.equipment.equippedPage;
            return page == (byte)(PlayerInventory.BACKPACK - 3) || page == (byte)(PlayerInventory.BACKPACK - 2);
        }

        // ---------- Alt+左键 快捷标记 ----------

        // Alt+左键点击背包物品：标记/取消标记自动回收
        internal void ToggleItemRule(ushort itemId)
        {
            if (cfgItemRules == null)
            {
                return;
            }
            string key = itemId.ToString();
            bool added = ToggleCsvEntry(cfgItemRules, key);
            Config.Save();
            ReloadRules();
            string msg = added ? "已将物品 ID " + itemId + " 加入自动回收清单" : "已将物品 ID " + itemId + " 移出自动回收清单";
            SendNotify(cfgRecycleNotifyTarget.Value, msg, added ? Color.green : Color.yellow);
            Logger.LogInfo("[AutoCraft] " + msg);
        }

        // Alt+左键点击合成界面配方：标记/取消标记自动合成
        internal void ToggleBlueprintRule(Blueprint blueprint)
        {
            if (blueprint == null || cfgBlueprintRules == null)
            {
                return;
            }
            Asset ownerAsset = blueprint.GetOwnerAsset();
            if (ownerAsset == null)
            {
                return;
            }
            string key = ownerAsset.id + ":" + blueprint.Index;
            bool added = ToggleCsvEntry(cfgBlueprintRules, key);
            Config.Save();
            ReloadRules();
            string msg = added ? "已将配方 " + GetBlueprintOutputName(blueprint) + "（" + key + "）加入自动合成" : "已将配方 " + GetBlueprintOutputName(blueprint) + "（" + key + "）移出自动合成";
            SendNotify(cfgCraftNotifyTarget.Value, msg, added ? Color.green : Color.yellow);
            Logger.LogInfo("[AutoCraft] " + msg);
        }

        // 在逗号分隔的配置项中切换一个条目（存在则移除，不存在则追加），返回是否新增
        private bool ToggleCsvEntry(ConfigEntry<string> entry, string entryValue)
        {
            List<string> items = new List<string>();
            string current = entry.Value;
            if (!string.IsNullOrWhiteSpace(current))
            {
                foreach (string part in current.Split(','))
                {
                    string trimmed = part != null ? part.Trim() : string.Empty;
                    if (trimmed.Length > 0)
                    {
                        items.Add(trimmed);
                    }
                }
            }
            bool added;
            if (items.Remove(entryValue))
            {
                added = false;
            }
            else
            {
                items.Add(entryValue);
                added = true;
            }
            items.Sort(StringComparer.Ordinal);
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(items[i]);
            }
            entry.Value = builder.ToString();
            return added;
        }

        // 是否按住 Alt 键
        private static bool IsAltHeld()
        {
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        }

        // ---------- 回收 ----------

        private bool IsWhitelistMode()
        {
            return string.Equals(cfgRecycleMode.Value, "Blacklist", StringComparison.OrdinalIgnoreCase) ? false : true;
        }

        // 白名单：只处理 ItemRules 内；黑名单：处理 ItemRules 之外
        private bool IsRecycleItem(ushort itemId)
        {
            bool inList = itemRules.Contains(itemId);
            return IsWhitelistMode() ? inList : !inList;
        }

        // 收集背包中出现的所有物品 ID（黑名单模式扫描用）
        private HashSet<ushort> CollectInventoryItemIds()
        {
            HashSet<ushort> ids = new HashSet<ushort>();
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return ids;
            }
            PlayerInventory inventory = localPlayer.inventory;
            for (byte page = 0; page < PlayerInventory.PAGES; page++)
            {
                if (page == PlayerInventory.STORAGE || page == PlayerInventory.AREA)
                {
                    continue;
                }
                Items items = inventory.items[page];
                if (items == null)
                {
                    continue;
                }
                for (byte index = 0; index < items.getItemCount(); index++)
                {
                    ItemJar jar = items.getItem(index);
                    if (jar != null && jar.item != null)
                    {
                        ids.Add(jar.item.id);
                    }
                }
            }
            return ids;
        }

        private void TryRecycle(ushort itemId, bool showReminders)
        {
            if (!IsRecycleItem(itemId))
            {
                return;
            }

            SalvageInfo info = ResolveSalvage(itemId);
            if (info == null)
            {
                // 无法拆解：按配置提醒（每物品每会话一次）
                if (showReminders && cfgRemindNotDismantlable.Value && warnedNotDismantlable.Add(itemId))
                {
                    ItemAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
                    string name = asset != null ? asset.FriendlyName : "未知物品";
                    SendNotify(cfgRecycleNotifyTarget.Value, "物品 " + name + " (ID " + itemId + ") 无法拆解，不会自动回收", Color.red);
                }
                return;
            }

            int total = CountItem(itemId);
            // 保底保留：始终留下 1 个
            int keep = cfgKeepLastOne.Value ? 1 : 0;
            int recyclable = total - keep;
            if (recyclable < info.RequiredCount)
            {
                // 数量不足：按配置提醒（带冷却）
                if (showReminders && cfgRemindNotEnough.Value && ShouldNotify("recycle:" + itemId))
                {
                    ItemAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
                    string name = asset != null ? asset.FriendlyName : itemId.ToString();
                    int needed = info.RequiredCount + keep;
                    SendNotify(cfgRecycleNotifyTarget.Value, "物品 " + name + " 需攒够 " + needed + " 个才能拆解（当前 " + total + " 个）", Color.yellow);
                }
                return;
            }

            int batches = recyclable / info.RequiredCount;
            int toRemove = batches * info.RequiredCount;

            isProcessing = true;
            try
            {
                RemoveItems(itemId, toRemove);
                int rewardTotal = batches * info.RewardPerCraft;
                if (rewardTotal > 0)
                {
                    AddReward(info.RewardItemId, rewardTotal);
                }
                if (cfgRecycleNotifyInChat.Value)
                {
                    SendNotify(cfgRecycleNotifyTarget.Value, GetRecycledMessage(itemId, toRemove, rewardTotal, info.RewardItemId), Color.green);
                }
                Logger.LogInfo("[AutoCraft] 回收 " + toRemove + " × 物品 " + itemId + "，获得 " + rewardTotal + " × 物品 " + info.RewardItemId);
            }
            finally
            {
                isProcessing = false;
            }
        }

        // 从该物品的游戏 Salvage（拆解）蓝图中检测拆解所需数量与产物
        private SalvageInfo ResolveSalvage(ushort itemId)
        {
            SalvageInfo cached;
            if (salvageCache.TryGetValue(itemId, out cached))
            {
                return cached;
            }
            if (noSalvageCache.Contains(itemId))
            {
                return null;
            }

            ItemAsset asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            if (asset == null || asset.blueprints == null)
            {
                noSalvageCache.Add(itemId);
                return null;
            }

            foreach (Blueprint blueprint in asset.blueprints)
            {
                if (blueprint == null || blueprint.CategoryTagRef.Guid != EBlueprintTypeEx.salvageCategoryTagRef.Guid)
                {
                    continue;
                }
                if (blueprint.outputs == null || blueprint.outputs.Length < 1)
                {
                    continue;
                }
                BlueprintOutput output = blueprint.outputs[0];
                ItemAsset outputAsset = output != null ? output.FindItemAsset() : null;
                if (outputAsset == null)
                {
                    continue;
                }

                // 拆解所需数量：查该物品在蓝图输入中的数量（找不到则按 1 处理）
                int requiredCount = 1;
                if (blueprint.supplies != null && blueprint.supplies.Length > 0)
                {
                    foreach (BlueprintSupply supply in blueprint.supplies)
                    {
                        if (supply != null)
                        {
                            ItemAsset supplyAsset = supply.FindItemAsset();
                            if (supplyAsset != null && supplyAsset.id == itemId && supply.amount > 0)
                            {
                                requiredCount = supply.amount;
                                break;
                            }
                        }
                    }
                }

                SalvageInfo info = new SalvageInfo(outputAsset.id, Mathf.Max(1, output.amount), Mathf.Max(1, requiredCount));
                salvageCache[itemId] = info;
                return info;
            }

            noSalvageCache.Add(itemId);
            return null;
        }

        private string GetRecycledMessage(ushort itemId, int count, int rewardTotal, ushort rewardItemId)
        {
            ItemAsset itemAsset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            ItemAsset rewardAsset = Assets.find(EAssetType.ITEM, rewardItemId) as ItemAsset;
            string itemName = itemAsset != null ? itemAsset.FriendlyName : itemId.ToString();
            string rewardName = rewardAsset != null ? rewardAsset.FriendlyName : rewardItemId.ToString();
            return "已回收 " + count + " × " + itemName + "，获得 " + rewardTotal + " × " + rewardName;
        }

        // ---------- 库存操作 ----------

        // 统计背包页（3 背包 / 4 背心 / 5 衬衫 / 6 裤子）中该物品的总数，不含装备栏（用于回收）
        private int CountItem(ushort itemId)
        {
            return CountItemInPages(itemId, PlayerInventory.BACKPACK, PlayerInventory.PANTS);
        }

        // 统计全部玩家页（0-6，含手上装备）中该物品的总数（用于合成材料检查）
        private int CountItemAll(ushort itemId)
        {
            int count = 0;
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return 0;
            }
            PlayerInventory inventory = localPlayer.inventory;
            for (byte page = 0; page < PlayerInventory.PAGES; page++)
            {
                if (page == PlayerInventory.STORAGE || page == PlayerInventory.AREA)
                {
                    continue;
                }
                count += CountItemInPages(itemId, page, page);
            }
            return count;
        }

        private int CountItemInPages(ushort itemId, byte fromPage, byte toPage)
        {
            int count = 0;
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return 0;
            }
            PlayerInventory inventory = localPlayer.inventory;
            for (byte page = fromPage; page <= toPage; page++)
            {
                Items items = inventory.items[page];
                if (items == null)
                {
                    continue;
                }
                for (byte index = 0; index < items.getItemCount(); index++)
                {
                    ItemJar jar = items.getItem(index);
                    if (jar != null && jar.item != null && jar.item.id == itemId)
                    {
                        count += jar.item.amount;
                    }
                }
            }
            return count;
        }

        // 从背包页中移除指定数量的物品（倒序移除，避免索引偏移）
        private void RemoveItems(ushort itemId, int count)
        {
            int remaining = count;
            if (localPlayer == null || localPlayer.inventory == null)
            {
                return;
            }
            PlayerInventory inventory = localPlayer.inventory;
            for (byte page = PlayerInventory.BACKPACK; page <= PlayerInventory.PANTS && remaining > 0; page++)
            {
                Items items = inventory.items[page];
                if (items == null)
                {
                    continue;
                }
                for (int index = items.getItemCount() - 1; index >= 0 && remaining > 0; index--)
                {
                    ItemJar jar = items.getItem((byte)index);
                    if (jar != null && jar.item != null && jar.item.id == itemId)
                    {
                        int amount = jar.item.amount;
                        if (amount > remaining)
                        {
                            // 该格数量足够，只扣减不整格移除
                            inventory.updateAmount(page, (byte)index, (byte)(amount - remaining));
                            remaining = 0;
                        }
                        else
                        {
                            inventory.removeItem(page, (byte)index);
                            remaining -= amount;
                        }
                    }
                }
            }
        }

        // 发放奖励物品；背包放不下时掉落在玩家脚下
        private void AddReward(ushort rewardItemId, int amount)
        {
            ItemAsset rewardAsset = Assets.find(EAssetType.ITEM, rewardItemId) as ItemAsset;
            if (rewardAsset == null)
            {
                Logger.LogWarning("[AutoCraft] 奖励物品 " + rewardItemId + " 不存在，无法发放。");
                return;
            }
            int maxStack = Mathf.Max(1, rewardAsset.MaxAmount);
            int remaining = amount;
            while (remaining > 0)
            {
                byte stack = (byte)Mathf.Min(remaining, maxStack);
                Item rewardItem = new Item(rewardItemId, stack, 100);
                if (!localPlayer.inventory.tryAddItem(rewardItem, false, false))
                {
                    ItemManager.dropItem(rewardItem, localPlayer.transform.position, false, true, true);
                }
                remaining -= stack;
            }
        }
    }

    // 拆解蓝图检测结果：一次拆解消耗的原料数量、产物物品与数量
    public class SalvageInfo
    {
        public ushort RewardItemId;
        public int RewardPerCraft;
        public int RequiredCount;

        public SalvageInfo(ushort rewardItemId, int rewardPerCraft, int requiredCount)
        {
            RewardItemId = rewardItemId;
            RewardPerCraft = rewardPerCraft;
            RequiredCount = requiredCount;
        }
    }

    // 合成配方原始规则（仅数字，配置解析阶段安全）
    public class CraftRule
    {
        public ushort OwnerId;
        public int Index;

        public CraftRule(ushort ownerId, int index)
        {
            OwnerId = ownerId;
            Index = index;
        }
    }

    // Alt+左键点击合成界面配方：标记/取消标记自动合成（跳过原点击行为）
    [HarmonyPatch(typeof(PlayerDashboardCraftingUI), "OnClickedBlueprint")]
    internal static class PatchBlueprintAltClick
    {
        internal static bool Prefix(object blueprintStatus)
        {
            if (!IsAltHeld())
            {
                return true;
            }
            try
            {
                if (AutoCraftPlugin.Instance != null && blueprintStatus != null)
                {
                    System.Reflection.FieldInfo field = blueprintStatus.GetType().GetField("blueprint",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    Blueprint blueprint = field != null ? (Blueprint)field.GetValue(blueprintStatus) : null;
                    AutoCraftPlugin.Instance.ToggleBlueprintRule(blueprint);
                }
            }
            catch (Exception exception)
            {
                AutoCraftPlugin.LogErrorStatic("[AutoCraft] Alt标记配方异常：" + exception);
            }
            return false;
        }

        private static bool IsAltHeld()
        {
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        }
    }

    // Alt+左键点击背包物品：标记/取消标记自动回收（跳过原拖拽行为）
    [HarmonyPatch(typeof(PlayerDashboardInventoryUI), "onGrabbedItem")]
    internal static class PatchItemAltClick
    {
        internal static bool Prefix(object item)
        {
            if (!IsAltHeld())
            {
                return true;
            }
            try
            {
                if (AutoCraftPlugin.Instance != null)
                {
                    SleekItem sleekItem = item as SleekItem;
                    if (sleekItem != null && sleekItem.jar != null && sleekItem.jar.item != null)
                    {
                        AutoCraftPlugin.Instance.ToggleItemRule(sleekItem.jar.item.id);
                    }
                }
            }
            catch (Exception exception)
            {
                AutoCraftPlugin.LogErrorStatic("[AutoCraft] Alt标记物品异常：" + exception);
            }
            return false;
        }

        private static bool IsAltHeld()
        {
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        }
    }
}
