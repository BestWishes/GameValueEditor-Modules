using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Runtime;
using Iced.Intel;

namespace GameValueEditor.Modules.Deskrawl;

internal sealed record DeskrawlMaterial(
    string AssetName,
    string DisplayName,
    int Count,
    int Maximum,
    ulong ItemAddress);

internal sealed record DeskrawlStackableItem(
    string AssetName,
    string DisplayName,
    int Count,
    int Maximum,
    ulong ItemAddress);

internal sealed record DeskrawlFishingSettings(
    int? SuccessRateOverride);

internal sealed record DeskrawlDropRulesSettings(
    int? EquipmentDropRateOverride,
    int? OfflineTradableDropMultiplierOverride,
    int EffectiveOfflineTradableDropMultiplierPercent);

internal sealed class DeskrawlRuntime : IDisposable
{
    private const string Assembly = "Assembly-CSharp.dll";
    private const string ActkAssembly = "ACTk.Runtime.dll";
    private const string ActkNamespace = "CodeStage.AntiCheat.ObscuredTypes";
    private const string ObscuredIntType = "CodeStage.AntiCheat.ObscuredTypes.ObscuredInt";
    private const string ObscuredLongType = "CodeStage.AntiCheat.ObscuredTypes.ObscuredLong";
    private const string ObscuredFloatType = "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat";
    private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;
    private const ulong StatOverrideMagic = 0x3130315441545344UL; // DSTAT101
    private const ulong StatOverrideDataOffset = 0xF00;
    private static readonly byte[] EquipmentMaximumBytes = [0x0F, 0x28, 0xC1, 0x90, 0x90];
    private static readonly object MainThreadGate = new();
    private static readonly object StatOverrideGate = new();
    private static readonly object DropRuleGate = new();
    private static readonly Dictionary<int, StatOverrideState> StatOverrides = [];
    private static readonly Dictionary<int, EquipmentDropOverrideState> EquipmentDropOverrides = [];

    private static readonly IReadOnlyDictionary<string, StatField> SessionStats =
        new Dictionary<string, StatField>(StringComparer.Ordinal)
        {
            ["strength"] = new(0, "力量（最终值）"),
            ["dexterity"] = new(1, "敏捷（最终值）"),
            ["intelligence"] = new(2, "智力（最终值）"),
            ["gold-find"] = new(15, "金币发现（%）", true),
            ["item-find-rate"] = new(16, "物品发现（%）", true),
            ["hp-potion-find"] = new(17, "生命药水发现（%）", true),
            ["material-find"] = new(19, "材料发现（%）", true),
            ["xp-gain"] = new(41, "获得经验值（%）", true)
        };

    private readonly GameProcessContext _context;
    private readonly Process _process;
    private readonly IntPtr _handle;
    private readonly Il2CppRuntimeResolver _runtime;
    private readonly string _gameAssemblyPath;
    private readonly DeskrawlSemanticResolver _semantic;

    internal DeskrawlRuntime(GameProcessContext context)
    {
        _context = context;
        _process = Process.GetProcessById(context.ProcessId);
        _gameAssemblyPath = Path.Combine(Path.GetDirectoryName(context.ExecutablePath)!, "GameAssembly.dll");
        _handle = OpenProcess(ProcessAccess, false, context.ProcessId);
        if (_handle == IntPtr.Zero)
        {
            _process.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接桌面破坏神进程。");
        }
        try
        {
            _runtime = new Il2CppRuntimeResolver(context);
            _semantic = new DeskrawlSemanticResolver(_runtime, _gameAssemblyPath);
        }
        catch
        {
            CloseHandle(_handle);
            _process.Dispose();
            throw;
        }
    }

    internal static bool IsDeskrawl(GameProcessContext process) =>
        Il2CppRuntimeResolver.IsNamedGame(process, "Deskrawl", "桌面破坏神") &&
        File.Exists(Path.Combine(Path.GetDirectoryName(process.ExecutablePath)!, "GameAssembly.dll"));

    internal bool HasLoadedCharacter
    {
        get
        {
            try { return Player != 0; }
            catch { return false; }
        }
    }

    internal GameDeclaredVersionInfo ReadApplicationMetadata()
    {
        if (!IsDeskrawl(_context)) throw new InvalidOperationException("当前进程身份与桌面破坏神不一致。");

        // The productName getter is stripped in this build. Unity's startup files
        // retain the name/GUID declarations without needing character, hook or save calls.
        var version = ReadManagedString(_runtime.Call(_runtime.Method(
            "UnityEngine.CoreModule.dll", "UnityEngine", "Application", "get_version", true, "System.String").Pointer));
        var dataDirectory = Path.Combine(Path.GetDirectoryName(_context.ExecutablePath)!, "Deskrawl_Data");
        _runtime.CheckAlive();
        var productName = DeskrawlApplicationMetadata.ReadProductName(dataDirectory);
        var buildGuid = DeskrawlApplicationMetadata.ReadBuildGuid(dataDirectory);
        _runtime.CheckAlive();
        return new(
            version,
            productName,
            buildGuid);
    }

    internal void ValidateCore()
    {
        if (!IsDeskrawl(_context)) throw new InvalidOperationException("当前进程身份与桌面破坏神不一致。");
        _ = Class("GameManager");
        _ = Method("GameManager", "dkg", true, "GameManager");
        _ = ExecuteTasks;
    }

    internal void ValidateCharacter()
    {
        ValidateCore();
        _ = Method("GameManager", "dkm", false, "Player");
        _ = Method("PlayerData", "fmc", true, "PlayerData");
        _ = Method("PlayerData", "fmg", false, "System.Void", "System.Int32");
        _ = Method("PlayerTalentBook", "fnu", true, "PlayerTalentBook");
        _ = Method("PlayerTalentBook", "fob", false, "System.Int32");
        _ = Method("PlayerTalentBook", "fot", false, "System.Void", "System.Int32");
        _ = Method("Player", "fkt", false, "System.Void", "System.Int32", "System.Int64");
        _ = Method("Player", "fko", true, "System.Int64", "System.Int32");
        _ = Method("Player", "bjy", false, "System.Double", "StatType");
        _ = Method("Player", "fdt", false, "System.Void", "StatType", "System.Double");
        _ = Method("hh", "dym", true, "System.Int32");
        _ = Method("hh", "dyl", true, "System.Boolean");
        _ = Method("hh", "dyn", true, "System.Int64");
        _ = Method("hh", "dyp", true, "System.Int32");
        _ = Method("hh", "dyq", true, "System.Int32");
        _ = Method("hh", "dyr", true, "System.Int32");
        _ = Method("hh", "dzd", true, "System.Void", "System.Boolean", "System.Int32", "System.Int64");
        _ = ObscuredIntGetter;
        _ = ObscuredLongGetter;
        _ = ObscuredFloatGetter;
        _ = ObscuredFloatConstructor;

        var playerData = PlayerData;
        var player = Player;
        var talent = TalentBook;
        if (playerData == 0 || player == 0 || talent == 0)
            throw new InvalidOperationException("人物尚未加载，请进入当前角色后刷新。");
        _ = _runtime.Address(playerData, "Gold", ObscuredIntType);
        _ = _runtime.Address(playerData, "Level", ObscuredIntType);
        _ = _runtime.Address(player, "CurrentXP", ObscuredLongType);
        _ = _runtime.Address(player, "BasePrimaryStat", ObscuredFloatType);
        var config = GameConfig;
        if (config == 0 || _runtime.ReadInt(_runtime.Address(config, "ParagonPointsPerLevel", "System.Int32")) <= 0)
            throw new InvalidDataException("游戏巅峰点配置无效，未开放写入。");
    }

    internal void ValidateMaterials()
    {
        ValidateCore();
        _ = Method("Inventory", "fbq", true, "Inventory");
        _ = Method("Inventory", "fcb", false, "System.Boolean", "ItemData", "System.Int32", "System.String");
        _ = Method("Inventory", "fcv", false, "System.Boolean", "ItemData", "System.Int32");
        _ = Method("Inventory", "fcs", false, "System.Int32", "ItemData");
        _ = Method("Inventory", "fcp", false, "System.Int32", "ItemData");
        _ = Method("ItemData", "ceu", false, "System.String");
        var save = SaveSystem;
        if (save == 0 || Inventory == 0)
            throw new InvalidOperationException("材料背包尚未加载，请进入存档后刷新。");
        var definitions = ItemDefinitions(save);
        if (definitions == 0) throw new InvalidOperationException("游戏材料定义表尚未加载。");
        _ = ReadDefinitionEntries(definitions);
    }

    internal void ValidateStackableItems()
    {
        ValidateMaterials();
        _ = Method("Inventory", "fcu", false, "System.Int32", "ItemData");
        var definitions = ItemDefinitions(SaveSystem);
        var hasStackable = ReadDefinitionEntries(definitions).Any(row =>
            _runtime.ReadInt(_runtime.Address(row.Value, "Type", "ItemType")) != 1 &&
            _runtime.ReadInt(_runtime.Address(row.Value, "MaxStackSize", "System.Int32")) > 1);
        if (!hasStackable) throw new InvalidDataException("当前构建没有可识别的非材料可堆叠物品。");
    }

    internal void ValidateEquipmentGeneration()
    {
        ValidateCore();
        _ = LocateEquipmentMaximumPatch();
    }

    internal void ValidateFishing()
    {
        ValidateCore();
        _ = CreateEntryPatch(Method("FishingModifiers", "ccm", false, "System.Double", "System.Single").Pointer, 16);
        _ = Method("FishingMapData", "ccq", true, "System.Single", "FishCatchEntry");
    }

    internal void ValidateDropRules()
    {
        ValidateCore();
        _ = CreateEntryPatch(Method("LootManager", "duh", true, "System.Single").Pointer, 10);
        var vectors = ReadEquipmentLootVectors();
        if (vectors.Count != 10)
            throw new InvalidDataException("游戏装备掉落配置数量与当前构建不一致，未开放修改。");
        foreach (var vector in vectors)
        {
            _ = ReadLootVector(vector);
        }
    }

    internal void ValidateSave()
    {
        var save = SaveSystem;
        if (save == 0) throw new InvalidOperationException("游戏存档系统尚未加载。");
        _ = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
    }

    internal AdapterEditorEntity ReadCharacter()
    {
        ValidateCharacter();
        var playerData = PlayerData;
        var player = Player;
        var talent = TalentBook;
        var level = ReadObscuredInt(_runtime.Address(playerData, "Level", ObscuredIntType));
        var gold = ReadObscuredInt(_runtime.Address(playerData, "Gold", ObscuredIntType));
        var experience = ReadObscuredLong(_runtime.Address(player, "CurrentXP", ObscuredLongType));
        var basePrimary = ReadObscuredFloat(_runtime.Address(player, "BasePrimaryStat", ObscuredFloatType));
        var talentPoints = checked((int)_runtime.Call(Method("PlayerTalentBook", "fob", false, "System.Int32").Pointer, talent));
        var paragonLevel = checked((int)_runtime.Call(Method("hh", "dym", true, "System.Int32").Pointer));
        var paragonTotal = checked((int)_runtime.Call(Method("hh", "dyp", true, "System.Int32").Pointer));
        var paragonPoints = checked((int)_runtime.Call(Method("hh", "dyq", true, "System.Int32").Pointer));
        var paragonSpent = checked((int)_runtime.Call(Method("hh", "dyr", true, "System.Int32").Pointer));
        var pointsPerLevel = _runtime.ReadInt(_runtime.Address(GameConfig, "ParagonPointsPerLevel", "System.Int32"));
        var nextExperience = unchecked((long)_runtime.Call(Method("Player", "fko", true, "System.Int64", "System.Int32").Pointer,
            unchecked((ulong)level)));

        if (level < 1 || gold < 0 || experience < 0 || talentPoints < 0 || paragonLevel < 0 ||
            paragonTotal < 0 || paragonPoints < 0 || paragonSpent < 0 || pointsPerLevel <= 0 ||
            paragonTotal != paragonPoints + paragonSpent || !float.IsFinite(basePrimary))
            throw new InvalidDataException("人物核心字段返回了无效值，未开放写入。");
        var experienceMaximum = Math.Max(experience, Math.Max(0, nextExperience - 1));

        var fields = new List<AdapterEditorField>
        {
            Persisted("gold", "金币", gold, 0, int.MaxValue),
            Persisted("experience", "当前经验", experience, 0, experienceMaximum,
                $"游戏存档字段 · 当前等级 {level} 的安全上限"),
            Persisted("talent-points", "可用天赋点", talentPoints, 0, int.MaxValue),
            Persisted("paragon-points", "可用巅峰点", paragonPoints, 0, int.MaxValue - paragonSpent,
                $"独立巅峰系统 · 每级 {pointsPerLevel} 点 · 修改点数会换算巅峰等级并请求保存"),
            Persisted("base-primary", "基础主属性", Round(basePrimary), 0, 1_000_000,
                "游戏存档字段 · 当前英雄的力量/敏捷/智力主属性基值")
        };

        foreach (var pair in SessionStats)
        {
            var value = ReadFinalStat(player, pair.Value.StatType);
            var displayValue = pair.Value.IsPercentage ? value * 100d : value;
            fields.Add(new AdapterEditorField(pair.Key, pair.Value.DisplayName, Round(displayValue), 0, 1_000_000,
                Status: pair.Value.IsPercentage
                    ? "人物面板百分比 · 底层小数已换算为百分比 · 本次运行覆盖最终属性消费者"
                    : "本次运行覆盖最终属性消费者 · 不会被装备或属性重算冲掉"));
        }

        return new AdapterEditorEntity(
            DeskrawlGameAdapter.CharacterEntityId,
            "当前角色",
            $"等级 {level} · 巅峰 {paragonLevel} · 金币 {gold.ToString(CultureInfo.InvariantCulture)} · {fields.Count} 项",
            fields);
    }

    private static AdapterEditorField Persisted(
        string key,
        string name,
        long value,
        long minimum,
        long maximum,
        string status = "游戏存档字段 · 写后回读并请求 SaveSystem 保存") =>
        new(key, name, value, minimum, maximum, Status: status);

    internal AdapterEditorEntity WriteCharacterField(string key, long target)
    {
        var before = ReadCharacter();
        var field = before.Fields.SingleOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"人物属性“{key}”不存在。");
        if (target < field.Minimum || target > field.Maximum)
            throw new InvalidOperationException($"{field.DisplayName}必须在 {field.RangeDisplay} 之间。");

        var player = Player;
        var playerData = PlayerData;
        var talent = TalentBook;
        if (SessionStats.TryGetValue(key, out var stat))
        {
            WriteFinalStat(player, stat.StatType, stat.IsPercentage ? target / 100d : target);
        }
        else
        {
            switch (key)
            {
                case "gold":
                    WriteGold(playerData, checked((int)target));
                    break;
                case "experience":
                    WriteExperience(playerData, player, target);
                    break;
                case "talent-points":
                    WriteTalentPoints(talent, checked((int)target));
                    break;
                case "paragon-points":
                    WriteParagonPoints(checked((int)target));
                    break;
                case "base-primary":
                    WriteBasePrimary(player, checked((float)target));
                    break;
                default:
                    throw new InvalidOperationException($"人物属性“{key}”没有写入实现。");
            }
        }

        var updated = ReadCharacter();
        var actual = updated.Fields.Single(item => string.Equals(item.Key, key, StringComparison.Ordinal)).Value;
        if (actual != target)
            throw new InvalidOperationException($"{field.DisplayName}写后回读不一致：目标 {target}，实际 {actual}。");
        return updated;
    }

    internal IReadOnlyList<DeskrawlMaterial> ReadMaterials()
    {
        ValidateMaterials();
        var save = SaveSystem;
        var inventory = Inventory;
        var definitions = ItemDefinitions(save);
        var countMethod = Method("Inventory", "fcs", false, "System.Int32", "ItemData");
        var capacityMethod = Method("Inventory", "fcp", false, "System.Int32", "ItemData");
        var definitionsRows = ReadDefinitionEntries(definitions)
            .Where(row => _runtime.ReadInt(_runtime.Address(row.Value, "Type", "ItemType")) == 1)
            .ToList();
        var localizedNames = ReadLocalizedItemNames(definitionsRows.Select(row => row.Value).ToList());
        var rows = new List<DeskrawlMaterial>();
        foreach (var (assetName, item) in definitionsRows)
        {
            var count = checked((int)_runtime.Call(countMethod.Pointer, inventory, item));
            var capacity = checked((int)_runtime.Call(capacityMethod.Pointer, inventory, item));
            if (count < 0 || capacity < 0)
                throw new InvalidDataException($"材料“{assetName}”的实时数量或携带上限无效。");
            // Inventory.fcn treats a zero capacity as unbounded. Existing saves can
            // also exceed a subsequently reduced capacity, so never hide the value.
            var maximum = capacity > 0 ? Math.Max(count, capacity) : int.MaxValue;
            rows.Add(new DeskrawlMaterial(assetName, localizedNames[item], count, maximum, item));
        }
        if (rows.Count == 0) throw new InvalidDataException("当前游戏定义表没有可识别的材料项。");
        return rows.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    internal IReadOnlyList<DeskrawlStackableItem> ReadStackableItems(bool includeZero = false)
    {
        ValidateMaterials();
        var save = SaveSystem;
        var inventory = Inventory;
        var definitions = ItemDefinitions(save);
        var countMethod = Method("Inventory", "fcu", false, "System.Int32", "ItemData");
        var definitionsRows = ReadDefinitionEntries(definitions)
            .Where(row =>
            {
                var type = _runtime.ReadInt(_runtime.Address(row.Value, "Type", "ItemType"));
                var maximumStack = _runtime.ReadInt(_runtime.Address(row.Value, "MaxStackSize", "System.Int32"));
                return type != 1 && maximumStack > 1;
            })
            .ToList();
        var localizedNames = ReadLocalizedItemNames(definitionsRows.Select(row => row.Value).ToList());
        var rows = new List<DeskrawlStackableItem>();
        foreach (var (assetName, item) in definitionsRows)
        {
            var count = checked((int)_runtime.Call(countMethod.Pointer, inventory, item));
            var maxCarry = _runtime.ReadInt(_runtime.Address(item, "MaxCarry", "System.Int32"));
            if (count < 0 || maxCarry < 0)
                throw new InvalidDataException($"背包物品“{assetName}”的数量或携带上限无效。");
            var maximum = maxCarry > 0 ? Math.Max(count, maxCarry) : int.MaxValue;
            if (includeZero || count > 0)
                rows.Add(new DeskrawlStackableItem(assetName, localizedNames[item], count, maximum, item));
        }
        return rows.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    internal DeskrawlStackableItem WriteStackableItem(string assetName, int target)
    {
        var item = ReadStackableItems(includeZero: true).SingleOrDefault(row => string.Equals(row.AssetName, assetName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"当前游戏定义中没有可堆叠背包物品“{assetName}”。");
        if (target < 0 || target > item.Maximum)
            throw new InvalidOperationException($"背包物品“{item.DisplayName}”必须在 0 到 {item.Maximum} 之间。");
        if (target == item.Count) return item;

        var inventory = Inventory;
        var save = SaveSystem;
        var count = Method("Inventory", "fcu", false, "System.Int32", "ItemData");
        var add = Method("Inventory", "fcb", false, "System.Boolean", "ItemData", "System.Int32", "System.String");
        var remove = Method("Inventory", "fcv", false, "System.Boolean", "ItemData", "System.Int32");
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(count.Pointer, inventory, item.ItemAddress, 0, 0);
            code.Emit(0x3D); code.Emit(BitConverter.GetBytes(item.Count));
            var stale = code.NearConditionalJump(0x85);
            var delta = target - item.Count;
            if (delta > 0)
                code.Call(add.Pointer, inventory, item.ItemAddress, unchecked((ulong)delta), 0, stackArgument: 0);
            else
                code.Call(remove.Pointer, inventory, item.ItemAddress, unchecked((ulong)-delta), 0);
            code.Emit(0x84, 0xC0);
            var rejected = code.NearConditionalJump(0x84);
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
            var done = code.NearJump();
            code.Patch(stale, code.Position);
            code.SetInt32(status, 4);
            var staleDone = code.NearJump();
            code.Patch(rejected, code.Position);
            code.SetInt32(status, 5);
            code.Patch(done, code.Position);
            code.Patch(staleDone, code.Position);
        });

        var updated = ReadStackableItems(includeZero: true).Single(row => string.Equals(row.AssetName, assetName, StringComparison.Ordinal));
        if (updated.Count != target)
            throw new InvalidOperationException($"背包物品写后回读不一致：目标 {target}，实际 {updated.Count}。");
        return updated;
    }

    internal DeskrawlMaterial WriteMaterial(string assetName, int target)
    {
        var material = ReadMaterials().SingleOrDefault(item => string.Equals(item.AssetName, assetName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"当前游戏定义中没有材料“{assetName}”。");
        if (target < 0 || target > material.Maximum)
            throw new InvalidOperationException($"材料“{material.DisplayName}”必须在 0 到 {material.Maximum} 之间。");
        if (target == material.Count) return material;

        var inventory = Inventory;
        var save = SaveSystem;
        var count = Method("Inventory", "fcs", false, "System.Int32", "ItemData");
        var add = Method("Inventory", "fcb", false, "System.Boolean", "ItemData", "System.Int32", "System.String");
        var remove = Method("Inventory", "fcv", false, "System.Boolean", "ItemData", "System.Int32");
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(count.Pointer, inventory, material.ItemAddress, 0, 0);
            code.Emit(0x3D); code.Emit(BitConverter.GetBytes(material.Count));
            var stale = code.NearConditionalJump(0x85);

            var delta = target - material.Count;
            if (delta > 0)
                code.Call(add.Pointer, inventory, material.ItemAddress, unchecked((ulong)delta), 0, stackArgument: 0);
            else
                code.Call(remove.Pointer, inventory, material.ItemAddress, unchecked((ulong)-delta), 0);
            code.Emit(0x84, 0xC0);
            var rejected = code.NearConditionalJump(0x84);
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
            var done = code.NearJump();

            code.Patch(stale, code.Position);
            code.SetInt32(status, 4);
            var staleDone = code.NearJump();
            code.Patch(rejected, code.Position);
            code.SetInt32(status, 5);
            code.Patch(done, code.Position);
            code.Patch(staleDone, code.Position);
        });

        var updated = ReadMaterials().Single(item => string.Equals(item.AssetName, assetName, StringComparison.Ordinal));
        if (updated.Count != target)
            throw new InvalidOperationException($"材料写后回读不一致：目标 {target}，实际 {updated.Count}。");
        return updated;
    }

    internal DeskrawlFishingSettings ReadFishingSettings()
    {
        ValidateFishing();
        var successPatch = CreateEntryPatch(
            Method("FishingModifiers", "ccm", false, "System.Double", "System.Single").Pointer, 16);
        var failureOverride = ReadDoubleReturnPatch(successPatch);
        return new DeskrawlFishingSettings(failureOverride is null
            ? null
            : checked((int)Math.Round((1d - failureOverride.Value) * 100d, MidpointRounding.AwayFromZero)));
    }

    internal DeskrawlFishingSettings WriteFishingSetting(string key, int? target)
    {
        switch (key)
        {
            case "success-rate":
            {
                var patch = CreateEntryPatch(
                    Method("FishingModifiers", "ccm", false, "System.Double", "System.Single").Pointer, 16);
                if (target is null) RestorePatch(patch, ReadDoubleReturnPatch(patch) is not null);
                else
                {
                    if (target is < 0 or > 100) throw new InvalidOperationException("钓鱼成功率必须在 0 到 100 之间。");
                    WriteOwnedPatch(patch, BuildDoubleReturnPatch(patch.Original.Length, 1d - target.Value / 100d));
                }
                break;
            }
            default:
                throw new InvalidOperationException($"钓鱼设置“{key}”不存在。");
        }
        return ReadFishingSettings();
    }

    internal void RestoreFishingSettings()
    {
        var success = CreateEntryPatch(
            Method("FishingModifiers", "ccm", false, "System.Double", "System.Single").Pointer, 16);
        if (ReadDoubleReturnPatch(success) is not null) RestorePatch(success, true);
    }

    internal DeskrawlDropRulesSettings ReadDropRules()
    {
        ValidateDropRules();
        var specialPatch = CreateEntryPatch(Method("LootManager", "duh", true, "System.Single").Pointer, 10);
        var specialOverride = ReadFloatReturnPatch(specialPatch);
        var effectiveSpecial = specialOverride ?? checked((float)CallFloating(
            Method("LootManager", "duh", true, "System.Single").Pointer,
            FloatingKind.Float, 0, 0, 0, 0));
        if (!float.IsFinite(effectiveSpecial) || effectiveSpecial < 0)
            throw new InvalidDataException("游戏离线可交易特殊掉落倍率无效，未开放修改。");

        int? equipmentOverride = null;
        lock (DropRuleGate)
        {
            if (TryGetEquipmentDropOverride(out var state)) equipmentOverride = state.TargetPercent;
        }
        return new DeskrawlDropRulesSettings(
            equipmentOverride,
            specialOverride is null ? null : checked((int)Math.Round(specialOverride.Value * 100f, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(effectiveSpecial * 100f, MidpointRounding.AwayFromZero)));
    }

    internal DeskrawlDropRulesSettings WriteDropRule(string key, int? target)
    {
        switch (key)
        {
            case "equipment-drop-rate":
                if (target is null) RestoreEquipmentDropRate();
                else SetEquipmentDropRate(target.Value);
                break;
            case "offline-tradable-drop-multiplier":
            {
                var patch = CreateEntryPatch(Method("LootManager", "duh", true, "System.Single").Pointer, 10);
                if (target is null) RestorePatch(patch, ReadFloatReturnPatch(patch) is not null);
                else
                {
                    if (target is < 0 or > 100_000)
                        throw new InvalidOperationException("特殊掉落权重倍率必须在 0% 到 100000% 之间。");
                    WriteOwnedPatch(patch, BuildFloatReturnPatch(patch.Original.Length, target.Value / 100f));
                }
                break;
            }
            default:
                throw new InvalidOperationException($"掉落规则“{key}”不存在。");
        }
        return ReadDropRules();
    }

    internal void RestoreDropRules()
    {
        RestoreEquipmentDropRate();
        var special = CreateEntryPatch(Method("LootManager", "duh", true, "System.Single").Pointer, 10);
        if (ReadFloatReturnPatch(special) is not null) RestorePatch(special, true);
    }

    private void SetEquipmentDropRate(int targetPercent)
    {
        if (targetPercent is < 1 or > 100)
            throw new InvalidOperationException("每次掉落尝试的装备掉落率必须在 1% 到 100% 之间。");

        lock (DropRuleGate)
        {
            var vectors = ReadEquipmentLootVectors();
            EquipmentDropOverrideState state;
            if (!TryGetEquipmentDropOverride(out state))
            {
                var originals = vectors.Select(address =>
                {
                    var value = ReadLootVector(address);
                    return new LootVectorOriginal(address, value.NoDrop);
                }).ToArray();
                state = new EquipmentDropOverrideState(ProcessStartTicks, LootConfig, originals, targetPercent);
            }
            else if (!state.Originals.Select(item => item.Address).SequenceEqual(vectors))
            {
                throw new InvalidOperationException("游戏掉落配置对象已变化，请先重新进入存档后刷新。");
            }

            var writes = new List<(ulong Address, float Value)>();
            foreach (var vector in vectors)
            {
                var value = ReadLootVector(vector);
                var nonEmpty = value.Common + value.Uncommon + value.Rare + value.Legendary;
                if (!float.IsFinite(nonEmpty) || nonEmpty <= 0)
                    throw new InvalidDataException("游戏装备稀有度权重无效，未修改掉落率。");
                var noDrop = targetPercent == 100
                    ? 0f
                    : nonEmpty * (100f - targetPercent) / targetPercent;
                if (!float.IsFinite(noDrop) || noDrop < 0)
                    throw new InvalidDataException("目标装备掉落率换算后超出游戏权重范围。");
                writes.Add((_runtime.Address(vector, "NoDrop", "System.Single"), noDrop));
            }

            WriteFloatFields(writes);
            EquipmentDropOverrides[_context.ProcessId] = state with { TargetPercent = targetPercent };
            foreach (var vector in vectors)
            {
                var value = ReadLootVector(vector);
                var total = value.NoDrop + value.Common + value.Uncommon + value.Rare + value.Legendary;
                var actual = checked((int)Math.Round(
                    (value.Common + value.Uncommon + value.Rare + value.Legendary) / total * 100f,
                    MidpointRounding.AwayFromZero));
                if (actual != targetPercent)
                    throw new InvalidOperationException($"装备掉落率写后回读不一致：目标 {targetPercent}%，实际 {actual}%。");
            }
        }
    }

    private void RestoreEquipmentDropRate()
    {
        lock (DropRuleGate)
        {
            if (!TryGetEquipmentDropOverride(out var state)) return;
            if (state.LootConfig == LootConfig)
            {
                WriteFloatFields(state.Originals.Select(item =>
                    (_runtime.Address(item.Address, "NoDrop", "System.Single"), item.NoDrop)).ToList());
            }
            EquipmentDropOverrides.Remove(_context.ProcessId);
        }
    }

    private bool TryGetEquipmentDropOverride(out EquipmentDropOverrideState state)
    {
        if (EquipmentDropOverrides.TryGetValue(_context.ProcessId, out state!) &&
            state.ProcessStartTicks == ProcessStartTicks)
            return true;
        EquipmentDropOverrides.Remove(_context.ProcessId);
        state = null!;
        return false;
    }

    private IReadOnlyList<ulong> ReadEquipmentLootVectors()
    {
        var loot = LootConfig;
        if (loot == 0) throw new InvalidOperationException("游戏掉落配置尚未加载。");
        var result = new List<ulong>();
        foreach (var field in new[]
                 {
                     "NormalDropSettings", "EliteDropSettings", "MiniBossDropSettings",
                     "BossDropSettings", "MythicBossDropSettings"
                 })
        {
            var settingsType = field == "MythicBossDropSettings"
                ? "LootConfig/MythicEncounterDropSettings"
                : "LootConfig/EncounterDropSettings";
            var settings = _runtime.Reference(loot, field, settingsType);
            if (settings == 0) throw new InvalidDataException($"游戏掉落配置 {field} 为空。");
            foreach (var vectorField in new[] { "MinLootVector", "MaxLootVector" })
            {
                var vector = _runtime.Reference(settings, vectorField, "LootConfig/LootVector");
                if (vector == 0) throw new InvalidDataException($"游戏掉落向量 {field}.{vectorField} 为空。");
                result.Add(vector);
            }
        }
        return result;
    }

    private LootVectorValues ReadLootVector(ulong vector)
    {
        float Read(string field) => BitConverter.ToSingle(_runtime.Read(
            _runtime.Address(vector, field, "System.Single"), 4), 0);
        var value = new LootVectorValues(Read("NoDrop"), Read("Common"), Read("Uncommon"), Read("Rare"), Read("Legendary"));
        if (!float.IsFinite(value.NoDrop) || value.NoDrop < 0 ||
            !float.IsFinite(value.Common) || value.Common < 0 ||
            !float.IsFinite(value.Uncommon) || value.Uncommon < 0 ||
            !float.IsFinite(value.Rare) || value.Rare < 0 ||
            !float.IsFinite(value.Legendary) || value.Legendary < 0)
            throw new InvalidDataException("游戏装备掉落权重包含无效值。");
        return value;
    }

    private void WriteFloatFields(IReadOnlyList<(ulong Address, float Value)> writes)
    {
        ExecuteMainThread((code, status, result) =>
        {
            foreach (var write in writes)
                code.SetInt32(write.Address, BitConverter.SingleToInt32Bits(write.Value));
            code.SetInt32(status, 1);
        });
    }

    internal bool IsEquipmentMaximumEnabled()
    {
        var patch = LocateEquipmentMaximumPatch();
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (current.SequenceEqual(patch.Original)) return false;
        if (current.SequenceEqual(EquipmentMaximumBytes)) return true;
        throw new InvalidOperationException("装备词条随机区间已被其他工具修改，未接管该位置。");
    }

    internal bool SetEquipmentMaximum(bool enabled)
    {
        var patch = LocateEquipmentMaximumPatch();
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (!current.SequenceEqual(patch.Original) && !current.SequenceEqual(EquipmentMaximumBytes))
            throw new InvalidOperationException("装备词条随机区间已被其他工具修改，未覆盖其代码。");
        WriteCodePatch(patch.Address, enabled ? EquipmentMaximumBytes : patch.Original);
        if (IsEquipmentMaximumEnabled() != enabled)
            throw new InvalidOperationException("装备词条最高值开关写后验证失败。");
        return enabled;
    }

    internal void RestoreEquipmentMaximum()
    {
        var patch = LocateEquipmentMaximumPatch();
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (current.SequenceEqual(EquipmentMaximumBytes)) WriteCodePatch(patch.Address, patch.Original);
        else if (!current.SequenceEqual(patch.Original))
            throw new InvalidOperationException("装备词条随机区间已被其他工具修改，未尝试恢复。");
    }

    private void WriteGold(ulong playerData, int target)
    {
        var goldAddress = _runtime.Address(playerData, "Gold", ObscuredIntType);
        var addGold = Method("PlayerData", "fmg", false, "System.Void", "System.Int32");
        var save = SaveSystem;
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(ObscuredIntGetter.Pointer, goldAddress, 0, 0, 0);
            code.MovRdx(unchecked((ulong)target));
            code.Emit(0x29, 0xC2); // sub edx,eax: game method consumes a delta.
            code.MovRcx(playerData); code.MovR8(0); code.MovRax(addGold.Pointer); code.CallRax();
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
        });
    }

    private void WriteExperience(ulong playerData, ulong player, long target)
    {
        var levelAddress = _runtime.Address(playerData, "Level", ObscuredIntType);
        var setLevelExperience = Method("Player", "fkt", false, "System.Void", "System.Int32", "System.Int64");
        var save = SaveSystem;
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(ObscuredIntGetter.Pointer, levelAddress, 0, 0, 0);
            code.Emit(0x89, 0xC2); // mov edx,eax: keep the current level.
            code.MovRcx(player); code.MovR8(unchecked((ulong)target)); code.MovR9(0);
            code.MovRax(setLevelExperience.Pointer); code.CallRax();
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
        });
    }

    private void WriteTalentPoints(ulong talent, int target)
    {
        var getPoints = Method("PlayerTalentBook", "fob", false, "System.Int32");
        var addPoints = Method("PlayerTalentBook", "fot", false, "System.Void", "System.Int32");
        var save = SaveSystem;
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(getPoints.Pointer, talent, 0, 0, 0);
            code.MovRdx(unchecked((ulong)target)); code.Emit(0x29, 0xC2);
            code.MovRcx(talent); code.MovR8(0); code.MovRax(addPoints.Pointer); code.CallRax();
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
        });
    }

    private void WriteParagonPoints(int target)
    {
        var getUnlocked = Method("hh", "dyl", true, "System.Boolean");
        var getExperience = Method("hh", "dyn", true, "System.Int64");
        var getAvailable = Method("hh", "dyq", true, "System.Int32");
        var getSpent = Method("hh", "dyr", true, "System.Int32");
        var setParagon = Method("hh", "dzd", true, "System.Void", "System.Boolean", "System.Int32", "System.Int64");
        var spent = checked((int)_runtime.Call(getSpent.Pointer));
        var current = checked((int)_runtime.Call(getAvailable.Pointer));
        var pointsPerLevel = _runtime.ReadInt(_runtime.Address(GameConfig, "ParagonPointsPerLevel", "System.Int32"));
        var desiredTotal = checked((long)target + spent);
        if (pointsPerLevel <= 0 || desiredTotal % pointsPerLevel != 0)
        {
            var lower = Math.Max(0, desiredTotal / Math.Max(1, pointsPerLevel) * Math.Max(1, pointsPerLevel) - spent);
            var upper = checked(lower + Math.Max(1, pointsPerLevel));
            throw new InvalidOperationException(
                $"游戏不单独保存巅峰点余额，而是每个巅峰等级发放 {pointsPerLevel} 点；保留已分配点数后，可用点只能设为 {lower}、{upper} 等合法档位。");
        }
        var level = checked((int)(desiredTotal / pointsPerLevel));
        var experience = unchecked((long)_runtime.Call(getExperience.Pointer));
        var unlocked = _runtime.Call(getUnlocked.Pointer) != 0 || level > 0;
        var save = SaveSystem;
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.Call(getAvailable.Pointer, 0, 0, 0, 0);
            code.Emit(0x3D); code.Emit(BitConverter.GetBytes(current));
            var stale = code.NearConditionalJump(0x85);
            code.Call(setParagon.Pointer, unlocked ? 1UL : 0UL, unchecked((ulong)level), unchecked((ulong)experience), 0);
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
            var done = code.NearJump();
            code.Patch(stale, code.Position);
            code.SetInt32(status, 4);
            code.Patch(done, code.Position);
        });
    }

    private void WriteBasePrimary(ulong player, float target)
    {
        var address = _runtime.Address(player, "BasePrimaryStat", ObscuredFloatType);
        var save = SaveSystem;
        var saveMethod = Method("SaveSystem", "egw", false, "System.Void", "System.Boolean");
        ExecuteMainThread((code, status, result) =>
        {
            code.MovRcx(address);
            code.MovXmm1(target);
            code.MovR8(0);
            code.MovRax(ObscuredFloatConstructor.Pointer); code.CallRax();
            code.Call(saveMethod.Pointer, save, 0, 0, 0);
            code.SetInt32(status, 1);
        });
    }

    private void WriteFinalStat(ulong player, int statType, double target) =>
        WriteFinalStatExact(player, statType, target);

    private void WriteFinalStatExact(ulong player, int statType, double target)
    {
        if (!double.IsFinite(target) || target < 0)
            throw new InvalidOperationException("人物最终属性覆盖值无效。");
        lock (StatOverrideGate)
        {
            var get = Method("Player", "bjy", false, "System.Double", "StatType");
            var patch = CreateEntryPatch(get.Pointer, 14);
            StatOverrideState? existing = TryGetStatOverride(patch, out var candidate) ? candidate : null;

            if (existing is not null)
            {
                var current = _runtime.Read(patch.Address, patch.Original.Length);
                var ownedJump = Il2CppMainThreadHook.Jump(existing.RemoteBase, patch.Original.Length);
                if (!current.SequenceEqual(ownedJump))
                    throw new InvalidOperationException("人物最终属性函数已被其他工具修改，未覆盖其代码。");
                WriteCodePatch(patch.Address, patch.Original);
            }
            else
            {
                var current = _runtime.Read(patch.Address, patch.Original.Length);
                if (!current.SequenceEqual(patch.Original))
                    throw new InvalidOperationException("人物最终属性函数已被其他工具修改，未接管该位置。");
            }

            var remoteBase = existing?.RemoteBase ?? unchecked((ulong)Il2CppMainThreadHook
                .AllocateNear(_handle, patch.Address, 4096).ToInt64());
            var values = existing is not null && existing.Player == player
                ? new Dictionary<int, double>(existing.Values)
                : [];
            values[statType] = target;

            var code = new Emitter();
            code.MovRax(player);
            code.Emit(0x48, 0x39, 0xC1); // cmp rcx,rax
            var differentPlayer = code.NearConditionalJump(0x85);
            foreach (var pair in values.OrderBy(item => item.Key))
            {
                code.Emit(0x81, 0xFA); // cmp edx,imm32
                code.Emit(BitConverter.GetBytes(pair.Key));
                var next = code.NearConditionalJump(0x85);
                code.MovRax(unchecked((ulong)BitConverter.DoubleToInt64Bits(pair.Value)));
                code.Emit(0x66, 0x48, 0x0F, 0x6E, 0xC0, 0xC3); // movq xmm0,rax; ret
                code.Patch(next, code.Position);
            }
            code.Patch(differentPlayer, code.Position);
            var relocated = Il2CppMainThreadHook.Relocate(patch.Original, patch.Address, remoteBase + (ulong)code.Position);
            code.Emit(relocated);
            var replacement = code.ToArray();
            if ((ulong)replacement.Length >= StatOverrideDataOffset)
                throw new InvalidDataException("人物最终属性覆盖代码超出缓冲区。");
            _runtime.Write(remoteBase, replacement);
            _runtime.Write(remoteBase + StatOverrideDataOffset, BuildStatOverrideData(player, values));
            if (!FlushInstructionCache(_handle, (IntPtr)(long)remoteBase, (nuint)replacement.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法刷新人物属性覆盖代码缓存。");
            WriteCodePatch(patch.Address, Il2CppMainThreadHook.Jump(remoteBase, patch.Original.Length));
            StatOverrides[_context.ProcessId] = new StatOverrideState(
                ProcessStartTicks, player, remoteBase, values);
        }
    }

    private bool TryGetStatOverride(CodePatch patch, out StatOverrideState state)
    {
        if (StatOverrides.TryGetValue(_context.ProcessId, out state!) &&
            state.ProcessStartTicks == ProcessStartTicks)
            return true;
        StatOverrides.Remove(_context.ProcessId);

        try
        {
            var current = _runtime.Read(patch.Address, patch.Original.Length);
            if (current.Length < 14 || !current.AsSpan(0, 6).SequenceEqual(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }))
            {
                state = null!;
                return false;
            }
            var remoteBase = BitConverter.ToUInt64(current, 6);
            var data = _runtime.Read(remoteBase + StatOverrideDataOffset, 256);
            if (BitConverter.ToUInt64(data, 0) != StatOverrideMagic)
            {
                state = null!;
                return false;
            }
            var player = BitConverter.ToUInt64(data, 8);
            var count = BitConverter.ToInt32(data, 16);
            if (player == 0 || count is < 1 or > 12)
                throw new InvalidDataException("人物属性覆盖状态损坏。");
            var values = new Dictionary<int, double>();
            for (var index = 0; index < count; index++)
            {
                var offset = 24 + index * 16;
                values.Add(BitConverter.ToInt32(data, offset), BitConverter.ToDouble(data, offset + 8));
            }
            state = new StatOverrideState(ProcessStartTicks, player, remoteBase, values);
            StatOverrides[_context.ProcessId] = state;
            return true;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch
        {
            state = null!;
            return false;
        }
    }

    private static byte[] BuildStatOverrideData(ulong player, IReadOnlyDictionary<int, double> values)
    {
        var data = new byte[256];
        BitConverter.GetBytes(StatOverrideMagic).CopyTo(data, 0);
        BitConverter.GetBytes(player).CopyTo(data, 8);
        BitConverter.GetBytes(values.Count).CopyTo(data, 16);
        var index = 0;
        foreach (var pair in values.OrderBy(item => item.Key))
        {
            var offset = 24 + index++ * 16;
            BitConverter.GetBytes(pair.Key).CopyTo(data, offset);
            BitConverter.GetBytes(pair.Value).CopyTo(data, offset + 8);
        }
        return data;
    }

    private double ReadFinalStat(ulong player, int statType) =>
        CallFloating(Method("Player", "bjy", false, "System.Double", "StatType").Pointer,
            FloatingKind.Double, player, unchecked((ulong)statType), 0, 0);

    private int ReadObscuredInt(ulong address) =>
        checked((int)_runtime.Call(ObscuredIntGetter.Pointer, address));

    private long ReadObscuredLong(ulong address) =>
        unchecked((long)_runtime.Call(ObscuredLongGetter.Pointer, address));

    private float ReadObscuredFloat(ulong address) =>
        checked((float)CallFloating(ObscuredFloatGetter.Pointer, FloatingKind.Float, address, 0, 0, 0));

    private IReadOnlyList<(string Key, ulong Value)> ReadDefinitionEntries(ulong dictionary)
    {
        var dictionaryClass = _runtime.ObjectClass(dictionary);
        var entriesType = _runtime.FieldType(dictionaryClass, "_entries");
        var entries = _runtime.Reference(dictionary, "_entries", entriesType);
        var count = _runtime.ReadInt(_runtime.Address(dictionary, "_count", "System.Int32"));
        var freeCount = _runtime.ReadInt(_runtime.Address(dictionary, "_freeCount", "System.Int32"));
        if (entries == 0 || count is < 0 or > 100_000 || freeCount is < 0 || freeCount > count)
            throw new InvalidDataException("材料定义字典结构无效。");
        var capacity = _runtime.ReadPointer(entries + 0x18);
        if ((ulong)count > capacity || capacity > 100_000)
            throw new InvalidDataException("材料定义字典容量无效。");

        var arrayClass = _runtime.ObjectClass(entries);
        var entryClass = _runtime.Call(_runtime.Export("il2cpp_class_get_element_class"), arrayClass);
        var stride = checked((int)_runtime.Call(_runtime.Export("il2cpp_array_element_size"), arrayClass));
        if (entryClass == 0 || stride is < 16 or > 128)
            throw new InvalidDataException("材料定义字典条目布局无效。");
        var entryIsValueType = _runtime.Call(_runtime.Export("il2cpp_class_is_valuetype"), entryClass) != 0;
        var keyOffset = ValueFieldOffset(entryClass, "key", "System.String", entryIsValueType);
        var valueOffset = ValueFieldOffset(entryClass, "value", "ItemData", entryIsValueType);
        if (keyOffset + 8 > (ulong)stride || valueOffset + 8 > (ulong)stride)
            throw new InvalidDataException("材料定义字典条目字段越界。");

        var result = new List<(string Key, ulong Value)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var entry = entries + 0x20UL + (ulong)(index * stride);
            var key = _runtime.ReadPointer(entry + keyOffset);
            var value = _runtime.ReadPointer(entry + valueOffset);
            if (key == 0 || value == 0) continue;
            var name = ReadManagedString(key);
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidDataException("材料定义表包含空白或重复的稳定资产名。");
            result.Add((name, value));
        }
        if (result.Count != count - freeCount)
            throw new InvalidDataException("材料定义字典活动条目数与运行时结构不一致。");
        return result;
    }

    private IReadOnlyDictionary<ulong, string> ReadLocalizedItemNames(IReadOnlyList<ulong> items)
    {
        var result = new Dictionary<ulong, string>();
        if (items.Count == 0) return result;
        var getName = Method("ItemData", "ceu", false, "System.String");
        const int batchSize = 20;
        for (var offset = 0; offset < items.Count; offset += batchSize)
        {
            var batch = items.Skip(offset).Take(batchSize).ToArray();
            using var pointers = _runtime.Allocate(checked(batch.Length * 8));
            ExecuteMainThread((code, status, ignored) =>
            {
                for (var index = 0; index < batch.Length; index++)
                {
                    code.Call(getName.Pointer, batch[index], 0, 0, 0);
                    code.StoreRax(pointers.Address + (ulong)(index * 8));
                }
                code.SetInt32(status, 1);
            });
            for (var index = 0; index < batch.Length; index++)
            {
                var address = _runtime.ReadPointer(pointers.Address + (ulong)(index * 8));
                var name = ReadManagedString(address).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException("游戏本地化系统返回了空物品名称，未回退到内部资产名。");
                result[batch[index]] = name;
            }
        }
        return result;
    }

    private ulong ValueFieldOffset(ulong klass, string fieldName, string expectedType, bool containingTypeIsValueType)
    {
        using var iterator = _runtime.Allocate(8);
        for (var index = 0; index < 64; index++)
        {
            var field = _runtime.Call(_runtime.Export("il2cpp_class_get_fields"), klass, iterator.Address);
            if (field == 0) break;
            var name = _runtime.ReadUtf8(_runtime.Call(_runtime.Export("il2cpp_field_get_name"), field));
            if (!string.Equals(name, fieldName, StringComparison.Ordinal)) continue;
            var type = _runtime.TypeName(_runtime.Call(_runtime.Export("il2cpp_field_get_type"), field));
            if (!Il2CppRuntimeResolver.SameType(type, expectedType))
                throw new InvalidOperationException($"材料定义条目字段 {fieldName} 类型为 {type}，预期 {expectedType}。");
            var offset = _runtime.Call(_runtime.Export("il2cpp_field_get_offset"), field);
            // IL2CPP reports boxed/value-type FieldInfo offsets with the 0x10 object
            // header, while an SZArray stores the unboxed payload at each element.
            if (containingTypeIsValueType)
            {
                if (offset < 0x10) throw new InvalidDataException("材料定义条目值类型字段偏移无效。");
                offset -= 0x10;
            }
            return offset <= 0x100 ? offset : throw new InvalidDataException("材料定义条目字段偏移无效。");
        }
        throw new InvalidOperationException($"材料定义条目缺少字段 {fieldName}。");
    }

    private CodePatch LocateEquipmentMaximumPatch()
    {
        var generation = Method("gh", "dpe", true, "StatModifier",
            "StatType", "System.Int32", "ItemRarity", "System.Boolean");
        var randomRange = _runtime.Method("UnityEngine.CoreModule.dll", "UnityEngine", "Random", "Range",
            true, "System.Single", "System.Single", "System.Single");
        var source = ReadOriginalCode(generation.Pointer, 512);
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(source));
        decoder.IP = generation.Pointer;
        var matches = new List<ulong>();
        while (decoder.IP < generation.Pointer + (ulong)source.Length)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid) break;
            if (instruction.FlowControl == FlowControl.Call &&
                instruction.Op0Kind == OpKind.NearBranch64 &&
                instruction.NearBranchTarget == randomRange.Pointer)
            {
                if (instruction.Length != 5)
                    throw new InvalidDataException("装备词条随机调用指令长度已变化，未安装最高值功能。");
                matches.Add(instruction.IP);
            }
            if (instruction.FlowControl == FlowControl.Return) break;
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("无法唯一定位装备词条的 0.85～1.15 随机取值调用，未安装最高值功能。");
        var original = ReadOriginalCode(matches[0], EquipmentMaximumBytes.Length);
        if (original.Length != 5 || original[0] != 0xE8)
            throw new InvalidDataException("装备词条随机调用原始指令不符合预期，未安装最高值功能。");
        return new CodePatch(matches[0], original);
    }

    private CodePatch CreateEntryPatch(ulong address, int minimumLength)
    {
        var source = ReadOriginalCode(address, 64);
        var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(source));
        decoder.IP = address;
        while (decoder.IP - address < (ulong)minimumLength)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch)
                throw new InvalidDataException("目标函数入口太短或结构已变化，未安装运行时覆盖。");
        }
        var length = checked((int)(decoder.IP - address));
        return new CodePatch(address, source[..length]);
    }

    private byte[] ReadOriginalCode(ulong address, int length)
    {
        if (address < _runtime.ModuleBase || address + (ulong)length > _runtime.ModuleEnd)
            throw new InvalidDataException("目标代码地址不属于当前 GameAssembly。");
        var rva = checked((int)(address - _runtime.ModuleBase));
        using var stream = File.OpenRead(_gameAssemblyPath);
        using var pe = new PEReader(stream);
        var block = pe.GetSectionData(rva);
        if (block.Length < length) throw new InvalidDataException("GameAssembly 原始代码块长度不足。");
        return block.GetContent(0, length).ToArray();
    }

    private float? ReadFloatReturnPatch(CodePatch patch)
    {
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (current.SequenceEqual(patch.Original)) return null;
        if (current.Length < 10 || current[0] != 0xB8 ||
            !current.AsSpan(5, 5).SequenceEqual(new byte[] { 0x66, 0x0F, 0x6E, 0xC0, 0xC3 }) ||
            current.Skip(10).Any(value => value != 0x90))
            throw new InvalidOperationException("物品发现函数已被其他工具修改，未接管该位置。");
        return BitConverter.Int32BitsToSingle(BitConverter.ToInt32(current, 1));
    }

    private double? ReadDoubleReturnPatch(CodePatch patch)
    {
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (current.SequenceEqual(patch.Original)) return null;
        if (current.Length < 16 || current[0] != 0x48 || current[1] != 0xB8 ||
            !current.AsSpan(10, 6).SequenceEqual(new byte[] { 0x66, 0x48, 0x0F, 0x6E, 0xC0, 0xC3 }) ||
            current.Skip(16).Any(value => value != 0x90))
            throw new InvalidOperationException("钓鱼成功率函数已被其他工具修改，未接管该位置。");
        return BitConverter.Int64BitsToDouble(BitConverter.ToInt64(current, 2));
    }

    private static byte[] BuildFloatReturnPatch(int length, float value)
    {
        if (length < 10 || !float.IsFinite(value)) throw new InvalidDataException("物品发现覆盖值无效。");
        var bytes = Enumerable.Repeat((byte)0x90, length).ToArray();
        bytes[0] = 0xB8;
        BitConverter.GetBytes(BitConverter.SingleToInt32Bits(value)).CopyTo(bytes, 1);
        new byte[] { 0x66, 0x0F, 0x6E, 0xC0, 0xC3 }.CopyTo(bytes, 5);
        return bytes;
    }

    private static byte[] BuildDoubleReturnPatch(int length, double value)
    {
        if (length < 16 || !double.IsFinite(value)) throw new InvalidDataException("钓鱼成功率覆盖值无效。");
        var bytes = Enumerable.Repeat((byte)0x90, length).ToArray();
        bytes[0] = 0x48; bytes[1] = 0xB8;
        BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(value)).CopyTo(bytes, 2);
        new byte[] { 0x66, 0x48, 0x0F, 0x6E, 0xC0, 0xC3 }.CopyTo(bytes, 10);
        return bytes;
    }

    private void WriteOwnedPatch(CodePatch patch, byte[] replacement)
    {
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        var owned = replacement[0] == 0xB8
            ? TryReadFloatReturnPatch(current, out _)
            : TryReadDoubleReturnPatch(current, out _);
        if (!current.SequenceEqual(patch.Original) && !owned)
            throw new InvalidOperationException("目标函数已被其他工具修改，未覆盖其代码。");
        WriteCodePatch(patch.Address, replacement);
    }

    private void RestorePatch(CodePatch patch, bool owned)
    {
        var current = _runtime.Read(patch.Address, patch.Original.Length);
        if (current.SequenceEqual(patch.Original)) return;
        if (!owned) throw new InvalidOperationException("目标函数不是本模块安装的覆盖，未尝试恢复。");
        WriteCodePatch(patch.Address, patch.Original);
    }

    private static bool TryReadFloatReturnPatch(byte[] bytes, out float value)
    {
        value = 0;
        if (bytes.Length < 10 || bytes[0] != 0xB8 ||
            !bytes.AsSpan(5, 5).SequenceEqual(new byte[] { 0x66, 0x0F, 0x6E, 0xC0, 0xC3 }) ||
            bytes.Skip(10).Any(item => item != 0x90)) return false;
        value = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, 1));
        return true;
    }

    private static bool TryReadDoubleReturnPatch(byte[] bytes, out double value)
    {
        value = 0;
        if (bytes.Length < 16 || bytes[0] != 0x48 || bytes[1] != 0xB8 ||
            !bytes.AsSpan(10, 6).SequenceEqual(new byte[] { 0x66, 0x48, 0x0F, 0x6E, 0xC0, 0xC3 }) ||
            bytes.Skip(16).Any(item => item != 0x90)) return false;
        value = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(bytes, 2));
        return true;
    }

    private void WriteCodePatch(ulong address, byte[] bytes)
    {
        if (!VirtualProtectEx(_handle, (IntPtr)(long)address, (nuint)bytes.Length, PageExecuteReadWrite, out var oldProtection))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏规则函数。");
        try
        {
            Il2CppMainThreadHook.WritePatch(_process, _handle, address, bytes);
            if (!FlushInstructionCache(_handle, (IntPtr)(long)address, (nuint)bytes.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法刷新游戏规则函数指令缓存。");
        }
        finally
        {
            VirtualProtectEx(_handle, (IntPtr)(long)address, (nuint)bytes.Length, oldProtection, out _);
        }
    }

    private double CallFloating(
        ulong target,
        FloatingKind kind,
        ulong arg1,
        ulong arg2,
        ulong arg3,
        ulong arg4)
    {
        using var result = _runtime.Allocate(8);
        using var code = _runtime.Allocate(256);
        var emitter = new Emitter();
        emitter.Emit(0x53, 0x48, 0x83, 0xEC, 0x30);
        emitter.MovRax(_runtime.Export("il2cpp_domain_get")); emitter.CallRax();
        emitter.Emit(0x48, 0x89, 0xC1);
        emitter.MovRax(_runtime.Export("il2cpp_thread_attach")); emitter.CallRax();
        emitter.Emit(0x48, 0x89, 0xC3);
        emitter.Call(target, arg1, arg2, arg3, arg4);
        emitter.MovRdx(result.Address);
        emitter.Emit(kind == FloatingKind.Double
            ? [0xF2, 0x0F, 0x11, 0x02]
            : [0xF3, 0x0F, 0x11, 0x02]);
        emitter.Emit(0x48, 0x89, 0xD9);
        emitter.MovRax(_runtime.Export("il2cpp_thread_detach")); emitter.CallRax();
        emitter.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x30, 0x5B, 0xC3);
        _runtime.Write(code.Address, emitter.ToArray());
        var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, (IntPtr)(long)code.Address, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法执行只读浮点查询。");
        try
        {
            if (WaitForSingleObject(thread, 5000) != 0)
            {
                code.Retain(); result.Retain();
                throw new TimeoutException("游戏运行时浮点查询未在五秒内结束。");
            }
            var bytes = _runtime.Read(result.Address, 8);
            return kind == FloatingKind.Double ? BitConverter.ToDouble(bytes, 0) : BitConverter.ToSingle(bytes, 0);
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    private void ExecuteMainThread(Action<Emitter, ulong, ulong> buildBody)
    {
        lock (MainThreadGate) ExecuteMainThreadCore(buildBody);
    }

    private void ExecuteMainThreadCore(Action<Emitter, ulong, ulong> buildBody)
    {
        var hook = ExecuteTasks.Pointer;
        var bytes = _runtime.Read(hook, 64);
        var length = Il2CppMainThreadHook.InstructionLength(bytes, hook);
        var original = bytes[..length];
        var remote = Il2CppMainThreadHook.AllocateNear(_handle, hook, 4096);
        var remoteBase = unchecked((ulong)remote.ToInt64());
        var continuation = remoteBase + 0xC00;
        var status = remoteBase + 0xE00;
        var result = remoteBase + 0xE08;
        var oldProtection = 0u;
        var patched = false;
        var canFree = true;
        try
        {
            var body = new Emitter();
            buildBody(body, status, result);
            var trampoline = Il2CppMainThreadHook.Wrap(body.ToArray(), status, continuation);
            if (trampoline.Length >= 0xC00) throw new InvalidDataException("桌面破坏神主线程操作代码超出缓冲区。");
            _runtime.Write(continuation, Il2CppMainThreadHook.Relocate(original, hook, continuation));
            _runtime.Write(remoteBase, trampoline);
            if (!VirtualProtectEx(_handle, (IntPtr)(long)hook, (nuint)original.Length, PageExecuteReadWrite, out oldProtection))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法临时写入游戏主线程入口。");
            canFree = false;
            patched = true;
            Il2CppMainThreadHook.WritePatch(_process, _handle, hook, Il2CppMainThreadHook.Jump(remoteBase, original.Length));
            if (!FlushInstructionCache(_handle, (IntPtr)(long)hook, (nuint)original.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法刷新游戏指令缓存。");

            var timer = Stopwatch.StartNew();
            var state = 0;
            while (state is 0 or 2 && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(10);
                state = _runtime.ReadInt(status);
            }
            switch (state)
            {
                case 1:
                    break;
                case 4:
                    throw new InvalidOperationException("游戏数据在操作前已变化，请刷新后重试；未执行修改。");
                case 5:
                    throw new InvalidOperationException("游戏自身的业务写入路径拒绝了本次修改。");
                case 0:
                case 2:
                    throw new TimeoutException(state == 2
                        ? "游戏主线程已进入操作，但未在五秒内完成。"
                        : "游戏主线程五秒内没有执行操作。");
                default:
                    throw new InvalidOperationException($"游戏主线程操作返回未知状态 {state}。");
            }
        }
        finally
        {
            if (patched)
            {
                Il2CppMainThreadHook.WritePatch(_process, _handle, hook, original);
                FlushInstructionCache(_handle, (IntPtr)(long)hook, (nuint)original.Length);
            }
            if (oldProtection != 0)
                VirtualProtectEx(_handle, (IntPtr)(long)hook, (nuint)original.Length, oldProtection, out _);
            if (canFree) VirtualFreeEx(_handle, remote, 0, MemRelease);
        }
    }

    private ulong GameManager => _runtime.Call(Method("GameManager", "dkg", true, "GameManager").Pointer);
    private ulong Player
    {
        get
        {
            var manager = GameManager;
            return manager == 0 ? 0 : _runtime.Call(Method("GameManager", "dkm", false, "Player").Pointer, manager);
        }
    }
    private ulong PlayerData => _runtime.Call(Method("PlayerData", "fmc", true, "PlayerData").Pointer);
    private ulong TalentBook => _runtime.Call(Method("PlayerTalentBook", "fnu", true, "PlayerTalentBook").Pointer);
    private ulong Inventory => _runtime.Call(Method("Inventory", "fbq", true, "Inventory").Pointer);
    private ulong SaveSystem => _runtime.Call(Method("SaveSystem", "edw", true, "SaveSystem").Pointer);
    private ulong GameConfig
    {
        get
        {
            var manager = GameManager;
            return manager == 0 ? 0 : _runtime.Reference(manager, "Config", "GameConfig");
        }
    }
    private ulong LootConfig => GameConfig == 0 ? 0 : _runtime.Reference(GameConfig, "Loot", "LootConfig");
    private long ProcessStartTicks => _process.StartTime.ToUniversalTime().Ticks;
    private Il2CppRuntimeResolver.NativeMethod ExecuteTasks =>
        _runtime.Method("UnityEngine.CoreModule.dll", "UnityEngine", "UnitySynchronizationContext", "ExecuteTasks", true, "System.Void");
    private Il2CppRuntimeResolver.NativeMethod ObscuredIntGetter =>
        Method("ObscuredInt", "kff", false, "System.Int32");
    private Il2CppRuntimeResolver.NativeMethod ObscuredLongGetter =>
        Method("ObscuredLong", "kfy", false, "System.Int64");
    private Il2CppRuntimeResolver.NativeMethod ObscuredFloatGetter =>
        Method("ObscuredFloat", "kdo", false, "System.Single");
    private Il2CppRuntimeResolver.NativeMethod ObscuredFloatConstructor =>
        _runtime.Method(ActkAssembly, ActkNamespace, "ObscuredFloat", ".ctor", false, "System.Void", "System.Single");

    private ulong Class(string name) => _runtime.Class(Assembly, string.Empty, name);
    private Il2CppRuntimeResolver.NativeMethod Method(
        string klass,
        string name,
        bool isStatic,
        string result,
        params string[] parameters) =>
        _semantic.Method(klass, name, isStatic, result, parameters);

    private ulong ItemDefinitions(ulong save) => _runtime.ReadPointer(save + _runtime.UniqueInstanceField(
        _runtime.ObjectClass(save), "System.Collections.Generic.Dictionary<System.String,ItemData>"));

    private string ReadManagedString(ulong address)
    {
        if (address == 0) return string.Empty;
        var length = _runtime.ReadInt(address + 0x10);
        if (length is < 0 or > 1_000_000) throw new InvalidDataException("IL2CPP 字符串长度无效。");
        return Encoding.Unicode.GetString(_runtime.Read(address + 0x14, checked(length * 2)));
    }

    private static long Round(double value)
    {
        if (!double.IsFinite(value) || value < long.MinValue || value > long.MaxValue)
            throw new InvalidDataException("游戏返回了无效的数值。");
        return checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    public void Dispose()
    {
        _runtime.Dispose();
        CloseHandle(_handle);
        _process.Dispose();
    }

    private sealed record StatField(int StatType, string DisplayName, bool IsPercentage = false);
    private sealed record StatOverrideState(
        long ProcessStartTicks,
        ulong Player,
        ulong RemoteBase,
        IReadOnlyDictionary<int, double> Values);
    private sealed record LootVectorValues(float NoDrop, float Common, float Uncommon, float Rare, float Legendary);
    private sealed record LootVectorOriginal(ulong Address, float NoDrop);
    private sealed record EquipmentDropOverrideState(
        long ProcessStartTicks,
        ulong LootConfig,
        IReadOnlyList<LootVectorOriginal> Originals,
        int TargetPercent);
    private sealed record CodePatch(ulong Address, byte[] Original);
    private enum FloatingKind { Float, Double }

    private sealed class Emitter
    {
        private readonly List<byte> _bytes = [];
        internal int Position => _bytes.Count;
        internal void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        internal void MovRax(ulong value) { Emit(0x48, 0xB8); Emit(BitConverter.GetBytes(value)); }
        internal void MovRcx(ulong value) { Emit(0x48, 0xB9); Emit(BitConverter.GetBytes(value)); }
        internal void MovRdx(ulong value) { Emit(0x48, 0xBA); Emit(BitConverter.GetBytes(value)); }
        internal void MovR8(ulong value) { Emit(0x49, 0xB8); Emit(BitConverter.GetBytes(value)); }
        internal void MovR9(ulong value) { Emit(0x49, 0xB9); Emit(BitConverter.GetBytes(value)); }
        internal void MovXmm1(float value)
        {
            MovRax(BitConverter.SingleToUInt32Bits(value));
            Emit(0x66, 0x0F, 0x6E, 0xC8);
        }
        internal void MovXmm2(double value)
        {
            MovRax(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
            Emit(0x66, 0x48, 0x0F, 0x6E, 0xD0);
        }
        internal void CallRax() => Emit(0xFF, 0xD0);
        internal void Call(ulong target, ulong rcx, ulong rdx, ulong r8, ulong r9, ulong? stackArgument = null)
        {
            MovRcx(rcx); MovRdx(rdx); MovR8(r8); MovR9(r9);
            Emit(0x48, 0xC7, 0x44, 0x24, 0x20, 0, 0, 0, 0);
            if (stackArgument is not null)
            {
                MovRax(stackArgument.Value);
                Emit(0x48, 0x89, 0x44, 0x24, 0x20);
            }
            MovRax(target); CallRax();
        }
        internal void SetInt32(ulong address, int value)
        {
            MovRdx(address); Emit(0xC7, 0x02); Emit(BitConverter.GetBytes(value));
        }
        internal void StoreRax(ulong address)
        {
            MovRdx(address); Emit(0x48, 0x89, 0x02);
        }
        internal int NearConditionalJump(byte condition)
        {
            Emit(0x0F, condition, 0, 0, 0, 0);
            return Position - 4;
        }
        internal int NearJump()
        {
            Emit(0xE9, 0, 0, 0, 0);
            return Position - 4;
        }
        internal void Patch(int displacementOffset, int target)
        {
            var bytes = BitConverter.GetBytes(target - displacementOffset - 4);
            for (var index = 0; index < 4; index++) _bytes[displacementOffset + index] = bytes[index];
        }
        internal byte[] ToArray() => _bytes.ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr start, IntPtr parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
