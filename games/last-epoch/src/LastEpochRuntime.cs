using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameValueEditor.ModuleSdk;
using Microsoft.Win32.SafeHandles;

namespace GameValueEditor.Modules.LastEpoch;

internal sealed class LastEpochRuntime : IDisposable
{
    private const uint ProcessAccess =
        0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const int ExperienceHookDataOffset = 0xD0;
    private const int ExperienceHookMetadataOffset = 0xF0;
    private const int ItemDropHookDataOffset = 0xD0;
    private const int ItemDropHookMetadataOffset = 0xF0;
    private const int GoldQuantityHookDataOffset = 0xD0;
    private const int GoldQuantityHookMetadataOffset = 0xF0;
    private static readonly byte[] ExperienceGainOriginalBytes =
        [0x48, 0x89, 0x5C, 0x24, 0x20, 0x56, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x30];
    private static readonly byte[] ExperienceHookMagic = Encoding.ASCII.GetBytes("LEXPHK01");
    private static readonly byte[] ItemDropChanceOriginalBytes =
        [0xF3, 0x0F, 0x11, 0x46, 0x30, 0x45, 0x0F, 0x57, 0xE4, 0x41, 0x80, 0x7E, 0x60, 0x00];
    private static readonly byte[] ItemDropHookMagic = Encoding.ASCII.GetBytes("LEIDHK01");
    private static readonly byte[] GoldQuantityOriginalBytes =
        [0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x58];
    private static readonly byte[] GoldQuantityHookMagic = Encoding.ASCII.GetBytes("LEGQHK01");

    private static readonly (string Key, string Name, int Attribute, int Property)[] CoreAttributes =
    [
        ("core-strength", "力量", 0, 19),
        ("core-dexterity", "敏捷", 3, 22),
        ("core-intelligence", "智力", 2, 21),
        ("core-attunement", "协调", 4, 23),
        ("core-vitality", "活力", 1, 20)
    ];

    private static readonly (string Key, string Name, int Property, int Tags)[] IncreasedStats =
    [
        ("movement-speed-percent", "移动速度（%）", 9, 0),
        ("area-effect-percent", "效果范围（%）", 116, 0),
        ("cooldown-recovery-percent", "提高冷却恢复速度（%）", 70, 0),
        ("experience-gain-percent", "经验倍率（%）", 105, 0),
        ("item-drop-rate-percent", "总物品掉落率（%）", 104, 0),
        ("potion-drop-rate-percent", "药剂掉落率（%）", 47, 0)
    ];

    private const string MovementCooldownKey = "movement-skill-cooldown-recovery-percent";
    private const string MovementCooldownName = "移动技能冷却恢复速度（%）";
    private const string GoldQuantityKey = "gold-quantity-percent";
    private const string GoldQuantityName = "金币倍率（%）";

    private static readonly (string Id, string Name, string Group, ulong Offset)[] MaterialContainers =
    [
        ("rune-shattering", "符文·粉碎", "符文", 0x20),
        ("rune-refinement", "符文·精炼", "符文", 0x28),
        ("rune-removal", "符文·移除", "符文", 0x30),
        ("rune-cleansing", "符文·净化", "符文", 0x38),
        ("rune-shaping", "符文·塑形", "符文", 0x40),
        ("glyph-guardian", "雕文·守护", "雕文", 0x48),
        ("glyph-stability", "雕文·稳定", "雕文", 0x50),
        ("glyph-order", "雕文·秩序", "雕文", 0x58),
        ("glyph-greater-hope", "雕文·绝望", "雕文", 0x60),
        ("rune-ascendance", "符文·升华", "符文", 0x68),
        ("rune-eterra", "符文·伊泰拉", "符文", 0x70),
        ("rune-envy", "符文·嫉妒", "符文", 0x78),
        ("rune-weaving", "符文·编织", "符文", 0x80),
        ("rune-havoc", "符文·浩劫", "符文", 0x88),
        ("rune-redemption", "符文·救赎", "符文", 0x90),
        ("rune-evolution", "符文·进化", "符文", 0x98),
        ("rune-corruption", "符文·腐化", "符文", 0xA0)
    ];

    private readonly record struct ResourceDefinition(byte Type, byte SubType, string Name);
    private readonly record struct SavedResourceRecord(ulong Address, int Quantity);

    private readonly Process _process;
    private readonly SafeFileHandle _handle;
    private readonly ulong _moduleBase;
    private readonly ulong _moduleEnd;
    private readonly ulong _domainGet;
    private readonly ulong _threadAttach;
    private readonly ulong _threadDetach;
    private readonly ulong _domainGetAssembliesRva;
    private readonly ulong _assemblyGetImageRva;
    private readonly ulong _classFromNameRva;
    private readonly ulong _classGetFieldFromNameRva;
    private readonly ulong _classGetMethodFromNameRva;
    private readonly ulong _classGetNestedTypesRva;
    private readonly ulong _classGetNameRva;
    private readonly ulong _fieldGetOffsetRva;
    private readonly ulong _fieldStaticGetValueRva;
    private readonly ulong _objectNewRva;
    private readonly ulong _arrayNewSpecificRva;
    private readonly ulong _arrayClassGetRva;
    private readonly ulong _classGetElementClassRva;
    private readonly ulong _gcWbarrierSetFieldRva;
    private readonly Dictionary<string, ulong> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _methodInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _fieldOffsets = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _localizedValues;

    public LastEpochRuntime(int processId)
    {
        _process = Process.GetProcessById(processId);
        _handle = new SafeFileHandle(OpenProcess(ProcessAccess, false, processId), true);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Last Epoch 进程。请以管理员身份运行肝肾大圣。");
        var module = _process.Modules.Cast<ProcessModule>().FirstOrDefault(item =>
            item.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Last Epoch 的 GameAssembly.dll 尚未加载。");
        _moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
        _moduleEnd = checked(_moduleBase + unchecked((ulong)module.ModuleMemorySize));
        var exports = PortableExportResolver.Read(module.FileName);
        _domainGet = _moduleBase + exports.GetRva("il2cpp_domain_get");
        _threadAttach = _moduleBase + exports.GetRva("il2cpp_thread_attach");
        _threadDetach = _moduleBase + exports.GetRva("il2cpp_thread_detach");
        _domainGetAssembliesRva = exports.GetRva("il2cpp_domain_get_assemblies");
        _assemblyGetImageRva = exports.GetRva("il2cpp_assembly_get_image");
        _classFromNameRva = exports.GetRva("il2cpp_class_from_name");
        _classGetFieldFromNameRva = exports.GetRva("il2cpp_class_get_field_from_name");
        _classGetMethodFromNameRva = exports.GetRva("il2cpp_class_get_method_from_name");
        _classGetNestedTypesRva = exports.GetRva("il2cpp_class_get_nested_types");
        _classGetNameRva = exports.GetRva("il2cpp_class_get_name");
        _fieldGetOffsetRva = exports.GetRva("il2cpp_field_get_offset");
        _fieldStaticGetValueRva = exports.GetRva("il2cpp_field_static_get_value");
        _objectNewRva = exports.GetRva("il2cpp_object_new");
        _arrayNewSpecificRva = exports.GetRva("il2cpp_array_new_specific");
        _arrayClassGetRva = exports.GetRva("il2cpp_array_class_get");
        _classGetElementClassRva = exports.GetRva("il2cpp_class_get_element_class");
        _gcWbarrierSetFieldRva = exports.GetRva("il2cpp_gc_wbarrier_set_field");
    }

    public bool HasLoadedCharacter
    {
        get
        {
            var tracker = ResolveStaticFieldObject(string.Empty, "PlayerFinder", "localPlayerDataTracker");
            return tracker != 0 && ReadObjectReferenceField(tracker, "charData") != 0;
        }
    }

    public void ValidateCompatibility()
    {
        // Resolve symbols by IL2CPP semantic identity. A changed game build is accepted only
        // when every root and native method needed by the module can still be located safely.
        _ = ResolveStaticFieldInfo(string.Empty, "PlayerFinder", "localPlayerDataTracker");
        _ = ResolveStaticFieldInfo(string.Empty, "PlayerFinder", "playerActorComponent");
        _ = ResolveStaticFieldInfo(string.Empty, "PlayerFinder", "localTreeData");
        _ = ResolveStaticFieldInfo(string.Empty, "PlayerFinder", "localItemContainersManager");
        _ = ResolveStaticFieldInfo(string.Empty, "Localization", "_tableCache");
        _ = ResolveStaticFieldInfo(string.Empty, "SpecialisedAbilityManager", "abilityLevelRequirements");
        _ = ResolveNamedMethodRva(string.Empty, "ItemData", "SetForgingPotential", 1);
        _ = ResolveNamedMethodRva(string.Empty, "ItemData", "RefreshIDAndValues", 0);
        _ = ResolveNamedMethodRva(string.Empty, "ItemContainerEntry", "UpdateEntry", 0);
        _ = ResolveNamedMethodRva(string.Empty, "ItemList", "get", 0);
        _ = ResolveNamedMethodRva(string.Empty, "ItemList", "GetItemName", 2);
        _ = ResolveNamedMethodRva("UnityEngine", "Application", "get_version", 0);
        _ = ResolveNamedMethodRva("UnityEngine", "Application", "get_productName", 0);
        _ = ResolveNamedMethodRva("UnityEngine", "Application", "get_buildGUID", 0);
        _ = ResolveNamedMethodRva("UnityEngine", "Application", "get_unityVersion", 0);

        ValidateInstanceField("CharacterDataTracker", "charData");
        ValidateInstanceField("CharacterDataTracker", "characterSlotId");
        ValidateInstanceField("ItemContainersManager", "crafting");
        ValidateInstanceField("ItemContainersManager/CraftingContainers", "main");
        ValidateInstanceField("OneSlotItemContainer", "content");
        ValidateInstanceField("ItemContainerEntry", "data");
        ValidateInstanceField("ItemData", "individualID");
        ValidateInstanceField("ItemData", "forgingPotential");
        ValidateInstanceField("ItemData", "forgingPotentialType");
        ValidateInstanceField("ItemData", "legendaryPotential");
        ValidateInstanceField("ItemData", "weaversWill");
        ValidateInstanceField("ItemData", "weaversTouch");
        ValidateInstanceField("Actor", "stats");
        ValidateInstanceField("Actor", "chargeManager");
        ValidateInstanceField("Actor", "mutatorManager");
        ValidateInstanceField("Actor", "healthPotion");
        ValidateInstanceField("Stats", "stats");
        ValidateInstanceField("BaseStats", "statsNeedToBeUpdatedNextFrame");
        ValidateInstanceField("CharacterStats", "attributes");
        ValidateInstanceField("CharacterStats", "expTracker");
        ValidateInstanceField("ExperienceTracker", "<CurrentExperience>k__BackingField");
        ValidateInstanceField("ExperienceTracker", "<NextLevelExperience>k__BackingField");
        ValidateInstanceField("ExperienceTracker", "<CurrentLevel>k__BackingField");
        ValidateInstanceField("HealthPotion", "baseDropChance");
        ValidateInstanceField("HealthPotion", "dropChance");
        ValidateInstanceField("HealthPotion", "increasedPotionDropRate");
        ValidateInstanceField("AbilityStatsMutatorManager", "increasedCooldownRecoverySpeedForMovementSkills");
        ValidateInstanceField("ChargeManager", "charges");
        ValidateInstanceField("ChargeManager", "chargeRegen");
        ValidateInstanceField("ChargeManager", "maxCharges");
        ValidateInstanceField("ChargeManager", "abilities");
        ValidateInstanceField("ChargeManager", "increasedRecoverySpeed");
        ValidateInstanceField("Ability", "abilityName");
        ValidateInstanceField("Ability", "playerAbilityID");
        ValidateInstanceField("Ability", "chargesGainedPerSecond");
        ValidateInstanceField("Ability", "traversalSkill");
        _ = ResolveNamedMethodRva(string.Empty, "CharacterStats", "OnUpdateTick", 1);
        _ = ResolveNamedMethodRva(string.Empty, "CharacterStats", "AddStatModifier", 6);
        _ = ResolveNamedMethodRva(string.Empty, "BaseStats", "UpdateStats", 0);
        _ = ResolveNamedMethodRva(string.Empty, "Stats", "GetTotalIncreased", 4);
        _ = ResolveNamedMethodRva(string.Empty, "CharacterSheet", "UpdateSheet", 0);
        _ = ResolveNamedMethodRva(string.Empty, "ExperienceTracker", "GainExp", 3);
        _ = ResolveNamedMethodRva(string.Empty, "HealthPotion", "getDropChance", 0);
        _ = ResolveNamedMethodRva(string.Empty, "HealthPotion", "updatePotionStats", 0);
        _ = ResolveNamedMethodRva(string.Empty, "ItemDrop", "getItemDropChance", 1);
        _ = ResolveItemDropHookSite();
        _ = ResolveNamedMethodRva(string.Empty, "GroundItemManager", "dropGoldForPlayer", 4);
    }

    public LastEpochCooldownDiagnostics ReadCooldownDiagnostics()
    {
        var actor = GetPlayerActor();
        var stats = GetPlayerStats(actor);
        var chargeManager = ReadObjectReferenceField(actor, "chargeManager");
        if (chargeManager == 0) throw new InvalidOperationException("当前人物冷却管理器尚未加载。");

        var abilities = ReadReferenceListWithNulls(
            ReadObjectReferenceField(chargeManager, "abilities"),
            "人物技能冷却定义");
        var charges = ReadFloatList(ReadObjectReferenceField(chargeManager, "charges"), "人物技能充能");
        var chargeRegen = ReadFloatList(ReadObjectReferenceField(chargeManager, "chargeRegen"), "人物技能充能恢复");
        var maxCharges = ReadFloatList(ReadObjectReferenceField(chargeManager, "maxCharges"), "人物技能最大充能");
        if (abilities.Count != charges.Count || abilities.Count != chargeRegen.Count || abilities.Count != maxCharges.Count)
            throw new InvalidDataException(
                $"人物冷却列表长度不一致（技能 {abilities.Count}，充能 {charges.Count}，恢复 {chargeRegen.Count}，上限 {maxCharges.Count}）。");

        var abilityRows = new List<LastEpochAbilityCooldownDiagnostic>(abilities.Count);
        for (var index = 0; index < abilities.Count; index++)
        {
            var ability = abilities[index];
            if (ability == 0) continue;
            var regen = chargeRegen[index];
            var remaining = regen > 0 && charges[index] < 1
                ? Math.Max(0, (1 - charges[index]) / regen)
                : 0;
            abilityRows.Add(new(
                index,
                ReadManagedString(ReadObjectReferenceField(ability, "playerAbilityID")),
                ReadManagedString(ReadObjectReferenceField(ability, "abilityName")),
                ReadByte(ObjectFieldAddress(ability, "traversalSkill")) != 0,
                charges[index],
                regen,
                maxCharges[index],
                ReadSingle(ObjectFieldAddress(ability, "chargesGainedPerSecond")),
                remaining));
        }

        var cooldownStats = ReadReferenceList(GetStatsList(stats), "人物实时统计")
            .Where(stat => ReadByte(stat + 0x10) == 70)
            .Select(stat => new LastEpochCooldownStatDiagnostic(
                ReadInt32(stat + 0x14),
                ReadInt32(stat + 0x18),
                ReadSingle(stat + 0x1C),
                ReadSingle(stat + 0x20)))
            .ToList();
        var mutatorManager = ReadObjectReferenceField(actor, "mutatorManager");
        return new(
            ReadSingle(ObjectFieldAddress(chargeManager, "increasedRecoverySpeed")),
            mutatorManager == 0
                ? 0
                : ReadSingle(ObjectFieldAddress(mutatorManager, "increasedCooldownRecoverySpeedForMovementSkills")),
            ReadMatchingStats(stats, 70, 0).Sum(ReadAddedValue),
            cooldownStats,
            abilityRows);
    }

    public LastEpochCooldownDiagnostics ApplyGenericCooldownRecoveryTest(int targetPercent)
    {
        if (targetPercent is < -100 or > 100_000)
            throw new InvalidOperationException("百分比必须在 -100 到 100000 之间。");
        var actor = GetPlayerActor();
        WriteCooldownRecoveryPercent(actor, targetPercent);
        return ReadCooldownDiagnostics();
    }

    public LastEpochExperienceDiagnostics ReadExperienceDiagnostics()
    {
        var stats = GetPlayerStats(GetPlayerActor());
        var tracker = ReadObjectReferenceField(stats, "expTracker");
        if (tracker == 0) throw new InvalidOperationException("当前人物经验追踪器尚未加载。");
        var hookInstalled = TryGetExperienceGainHook(out var hook);
        return new(
            ReadInt32(ObjectFieldAddress(tracker, "<CurrentLevel>k__BackingField")),
            ReadInt64(ObjectFieldAddress(tracker, "<CurrentExperience>k__BackingField")),
            ReadInt64(ObjectFieldAddress(tracker, "<NextLevelExperience>k__BackingField")),
            ReadIncreasedStatPercent(stats, 105, 0),
            hookInstalled,
            hookInstalled ? ReadInt64(hook + ExperienceHookDataOffset) : 0,
            hookInstalled ? ReadInt64(hook + ExperienceHookDataOffset + 8) : 0,
            hookInstalled ? ReadInt64(hook + ExperienceHookDataOffset + 16) : 0);
    }

    public LastEpochPotionDropDiagnostics ReadPotionDropDiagnostics()
    {
        var actor = GetPlayerActor();
        var potion = ReadObjectReferenceField(actor, "healthPotion");
        if (potion == 0) throw new InvalidOperationException("当前人物药剂组件尚未加载。");
        return new(
            ReadSingle(ObjectFieldAddress(potion, "baseDropChance")),
            ReadSingle(ObjectFieldAddress(potion, "increasedPotionDropRate")),
            CallFloat(
                ResolveNamedMethodRva(string.Empty, "HealthPotion", "getDropChance", 0),
                potion));
    }

    public LastEpochItemDropDiagnostics ReadItemDropDiagnostics()
    {
        var installed = TryGetItemDropHook(out var stub);
        var multiplier = installed ? ReadSingle(stub + ItemDropHookDataOffset) : 1f;
        return new(
            checked((int)Math.Round((multiplier - 1f) * 100d, MidpointRounding.AwayFromZero)),
            installed,
            multiplier,
            installed ? ReadSingle(stub + ItemDropHookDataOffset + 4) : 0,
            installed ? ReadSingle(stub + ItemDropHookDataOffset + 8) : 0,
            installed ? ReadInt64(stub + ItemDropHookDataOffset + 16) : 0);
    }

    public LastEpochGoldQuantityDiagnostics ReadGoldQuantityDiagnostics()
    {
        var installed = TryGetGoldQuantityHook(out var stub);
        var multiplier = installed ? ReadSingle(stub + GoldQuantityHookDataOffset) : 1f;
        return new(
            checked((int)Math.Round((multiplier - 1f) * 100d, MidpointRounding.AwayFromZero)),
            installed,
            multiplier,
            installed ? ReadInt32(stub + GoldQuantityHookDataOffset + 4) : 0,
            installed ? ReadInt32(stub + GoldQuantityHookDataOffset + 8) : 0,
            installed ? ReadInt64(stub + GoldQuantityHookDataOffset + 16) : 0);
    }

    public static bool IsOfflineProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var handle = new SafeFileHandle(OpenProcess(0x0010 | 0x0400, false, processId), true);
            if (handle.IsInvalid) return false;
            var size = Marshal.SizeOf<ProcessBasicInformation>();
            if (NtQueryInformationProcess(handle, 0, out ProcessBasicInformation info, size, out _) != 0)
                return false;
            var processParameters = ReadUInt64(handle, unchecked((ulong)info.PebBaseAddress.ToInt64()) + 0x20);
            if (processParameters == 0) return false;
            var length = ReadUInt16(handle, processParameters + 0x70);
            var buffer = ReadUInt64(handle, processParameters + 0x78);
            if (length == 0 || buffer == 0 || length > 32766) return false;
            var bytes = ReadBytes(handle, buffer, length);
            return Encoding.Unicode.GetString(bytes).Contains("--offline", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public AdapterCharacterItem ReadCharacter()
    {
        var tracker = GetTracker();
        var data = GetCharacterData();
        var tree = RequirePlayerFinderRoot("localTreeData", "人物技能树");
        var name = ReadManagedString(ReadObjectReferenceField(data, "<CharacterName>k__BackingField"));
        var level = ReadInt32(ObjectFieldAddress(data, "<Level>k__BackingField"));
        var passive = ReadObjectReferenceField(tree, "passiveTree");
        var passivePoints = passive == 0
            ? 0
            : Math.Max(0, ReadUInt16(ObjectFieldAddress(passive, "pointsEarnt")) -
                          SumAllocatedPoints(ReadObjectReferenceField(passive, "nodes")));
        var skillTrees = ReadReferenceList(ReadObjectReferenceField(tree, "specialisedSkillTrees"), "专精技能树");
        var specialisationPoints = skillTrees.Count == 0
            ? 0
            : skillTrees.Select(item => Math.Max(0,
                ReadByte(ObjectFieldAddress(item, "level")) -
                SumAllocatedPoints(ReadObjectReferenceField(item, "nodes")))).Min();
        var id = $"{ReadInt32(ObjectFieldAddress(tracker, "characterSlotId"))}:{name}";
        var attributes = new List<AdapterCharacterAttribute>
        {
            new("skill-points", "剩余天赋点", passivePoints, passivePoints, 0, true, "写入角色存档"),
            new("specialisation-points", "剩余技能点", specialisationPoints, specialisationPoints, 0,
                skillTrees.Count > 0, skillTrees.Count > 0 ? "按技能经验等级写入角色存档" : "当前没有已专精技能")
        };
        var actor = GetPlayerActor();
        var stats = GetPlayerStats(actor);
        foreach (var core in CoreAttributes)
        {
            var value = ReadCoreAttributeValue(stats, core.Attribute, core.Property);
            attributes.Add(new(
                core.Key,
                core.Name,
                value,
                value,
                0,
                true,
                "仅当前游戏运行有效；重新进入角色后由游戏恢复"));
        }
        foreach (var increased in IncreasedStats)
        {
            var value = increased.Property switch
            {
                70 => ReadCooldownRecoveryPercent(actor),
                116 => ReadAreaEffectPercent(stats),
                47 => ReadPotionDropRatePercent(actor),
                104 => ReadItemDropRatePercent(),
                _ => ReadIncreasedStatPercent(stats, increased.Property, increased.Tags)
            };
            attributes.Add(new(
                increased.Key,
                increased.Name,
                value,
                value,
                0,
                true,
                increased.Key == "area-effect-percent"
                    ? "仅当前游戏运行有效；同时放大技能范围与部分技能视觉特效"
                    : "仅当前游戏运行有效；按百分比填写"));
        }
        var goldQuantity = ReadGoldQuantityPercent();
        attributes.Add(new(
            GoldQuantityKey,
            GoldQuantityName,
            goldQuantity,
            goldQuantity,
            0,
            true,
            "仅当前游戏运行有效；100% 表示金币堆数量变为 2 倍"));
        var movementCooldown = ReadMovementCooldownPercent(actor);
        attributes.Add(new(
            MovementCooldownKey,
            MovementCooldownName,
            movementCooldown,
            movementCooldown,
            0,
            true,
            "仅当前游戏运行有效；按百分比填写"));
        return new(
            id,
            string.IsNullOrWhiteSpace(name) ? "当前离线角色" : name,
            level,
            attributes);
    }

    public void WriteCharacterAttribute(string characterId, string key, int target)
    {
        var current = ReadCharacter();
        if (!string.Equals(current.CharacterId, characterId, StringComparison.Ordinal))
            throw new InvalidOperationException("当前角色已经变化，请刷新后重试。");
        var currentAttribute = current.Attributes.SingleOrDefault(item => item.Key == key)
            ?? throw new InvalidOperationException($"未知人物字段 {key}。");
        if (currentAttribute.RawValue == target)
        {
            if (key == "experience-gain-percent" && target != 0) EnsureExperienceGainHook();
            return;
        }
        var tree = RequirePlayerFinderRoot("localTreeData", "人物技能树");
        if (key == "skill-points")
        {
            if (target is < 0 or > ushort.MaxValue) throw new InvalidOperationException("剩余天赋点超出允许范围。");
            var passive = ReadObjectReferenceField(tree, "passiveTree");
            if (passive == 0) throw new InvalidOperationException("被动技能树尚未加载。");
            var earned = checked(target + SumAllocatedPoints(ReadObjectReferenceField(passive, "nodes")));
            if (earned > ushort.MaxValue) throw new InvalidOperationException("目标技能点过高。");
            Write(ObjectFieldAddress(passive, "pointsEarnt"), BitConverter.GetBytes(checked((ushort)earned)));
        }
        else if (key == "specialisation-points")
        {
            if (target is < 0 or > 20) throw new InvalidOperationException("剩余技能点必须在 0 到 20 之间。");
            var skills = ReadReferenceList(ReadObjectReferenceField(tree, "specialisedSkillTrees"), "专精技能树");
            if (skills.Count == 0) throw new InvalidOperationException("当前角色还没有已专精技能。");
            foreach (var skill in skills)
            {
                var allocated = SumAllocatedPoints(ReadObjectReferenceField(skill, "nodes"));
                var desiredLevel = target + allocated;
                if (desiredLevel is < 0 or > 20)
                    throw new InvalidOperationException("目标技能点与已分配节点合计不能超过技能等级上限 20。");
                var requiredXp = ReadSkillXpRequirement(desiredLevel);
                Write(ObjectFieldAddress(skill, "abilityXp"), BitConverter.GetBytes(requiredXp));
                Write(ObjectFieldAddress(skill, "level"), [checked((byte)desiredLevel)]);
            }
        }
        else if (CoreAttributes.FirstOrDefault(item => item.Key == key) is var core && !string.IsNullOrWhiteSpace(core.Key))
        {
            if (target is < 0 or > 100_000) throw new InvalidOperationException("基础属性必须在 0 到 100000 之间。");
            var actor = GetPlayerActor();
            var stats = GetPlayerStats(actor);
            WriteCoreAttribute(stats, core.Attribute, core.Property, target);
        }
        else if (IncreasedStats.FirstOrDefault(item => item.Key == key) is var increased && !string.IsNullOrWhiteSpace(increased.Key))
        {
            if (target is < -100 or > 100_000) throw new InvalidOperationException("百分比必须在 -100 到 100000 之间。");
            var actor = GetPlayerActor();
            var stats = GetPlayerStats(actor);
            WriteIncreasedStatPercent(actor, stats, increased.Property, increased.Tags, target);
        }
        else if (key == MovementCooldownKey)
        {
            if (target is < -100 or > 100_000) throw new InvalidOperationException("百分比必须在 -100 到 100000 之间。");
            WriteMovementCooldownPercent(GetPlayerActor(), target);
        }
        else if (key == GoldQuantityKey)
        {
            if (target is < -100 or > 100_000) throw new InvalidOperationException("百分比必须在 -100 到 100000 之间。");
            WriteGoldQuantityPercent(target);
        }
        else
        {
            throw new InvalidOperationException($"未知人物字段 {key}。");
        }
        if (key is "skill-points" or "specialisation-points") MarkCharacterDirty();
        else Thread.Sleep(100);
        var reread = ReadCharacter().Attributes.Single(item => item.Key == key).RawValue;
        if (Math.Abs(reread - target) > 1) throw new InvalidOperationException("游戏没有接受人物属性修改，已停止继续写入。");
    }

    public IReadOnlyList<AdapterEditorEntity> ReadEquipment()
    {
        var (_, data) = GetForgeMainItem();
        if (data == 0) return [];

        var individualId = ReadUInt32(ObjectFieldAddress(data, "individualID"));
        var uniqueId = ReadUInt16(ObjectFieldAddress(data, "uniqueID"));
        var weaversWill = ReadByte(ObjectFieldAddress(data, "weaversWill"));
        var weaversTouch = ReadByte(ObjectFieldAddress(data, "weaversTouch"));
        var forgingType = ReadInt32(ObjectFieldAddress(data, "forgingPotentialType"));
        var forgingName = forgingType switch
        {
            1 => "鲜血锻造潜能",
            2 => "寒冰锻造潜能",
            _ => "锻造潜能"
        };
        var name = ReadItemName(data, "熔炉装备");
        return
        [
            new(
                $"forge:{individualId.ToString(CultureInfo.InvariantCulture)}",
                name,
                "只修改当前放入熔炉主槽的这一件装备",
                [
                    new("forging-potential", forgingName,
                        ReadByte(ObjectFieldAddress(data, "forgingPotential")), 0, byte.MaxValue,
                        uniqueId == 0, uniqueId == 0 ? string.Empty : "该装备不使用锻造潜能"),
                    new("weavers-will", "编织者意志", weaversWill, 0, 28,
                        weaversWill > 0, weaversWill > 0 ? "允许 5 ~ 28" : "该装备没有编织者意志"),
                    new("weavers-touch", "编织者之触", weaversTouch, 0, 14,
                        weaversTouch > 0, weaversTouch > 0 ? "允许 5 ~ 14" : "该装备没有编织者之触"),
                    new("legendary-potential", "传奇潜能",
                        ReadByte(ObjectFieldAddress(data, "legendaryPotential")), 0, 4,
                        uniqueId != 0 && weaversWill == 0 && weaversTouch == 0,
                        uniqueId != 0 && weaversWill == 0 && weaversTouch == 0
                            ? string.Empty
                            : "该装备不使用传奇潜能")
                ])
        ];
    }

    public AdapterEditorEntity WriteEquipment(string entityId, string key, long target)
    {
        var (entry, data) = GetForgeMainItem();
        if (data == 0) throw new InvalidOperationException("请先把要修改的装备放入熔炉主槽。");
        var individualId = ReadUInt32(ObjectFieldAddress(data, "individualID"));
        var expectedId = $"forge:{individualId.ToString(CultureInfo.InvariantCulture)}";
        if (!string.Equals(entityId, expectedId, StringComparison.Ordinal))
            throw new InvalidOperationException("熔炉中的装备已经变化，请刷新后重新选择。");

        var uniqueId = ReadUInt16(ObjectFieldAddress(data, "uniqueID"));
        var currentWeaversWill = ReadByte(ObjectFieldAddress(data, "weaversWill"));
        var currentWeaversTouch = ReadByte(ObjectFieldAddress(data, "weaversTouch"));
        var refresh = ResolveNamedMethodRva(string.Empty, "ItemData", "RefreshIDAndValues", 0);
        switch (key)
        {
            case "forging-potential" when uniqueId == 0 && target is >= 0 and <= byte.MaxValue:
                Call(ResolveNamedMethodRva(string.Empty, "ItemData", "SetForgingPotential", 1),
                    data, unchecked((ulong)target));
                break;
            case "weavers-will" when currentWeaversWill > 0 && target is >= 5 and <= 28:
                Write(ObjectFieldAddress(data, "weaversWill"), [checked((byte)target)]);
                Call(refresh, data);
                break;
            case "weavers-touch" when currentWeaversTouch > 0 && target is >= 5 and <= 14:
                Write(ObjectFieldAddress(data, "weaversTouch"), [checked((byte)target)]);
                Call(refresh, data);
                break;
            case "legendary-potential" when uniqueId != 0 && currentWeaversWill == 0 &&
                                                     currentWeaversTouch == 0 && target is >= 0 and <= 4:
                Write(ObjectFieldAddress(data, "legendaryPotential"), [checked((byte)target)]);
                Call(refresh, data);
                break;
            default:
                throw new InvalidOperationException("该装备不使用这个潜能，或目标值超出游戏允许范围。");
        }
        Call(ResolveNamedMethodRva(string.Empty, "ItemContainerEntry", "UpdateEntry", 0), entry);
        MarkCharacterDirty();
        var updated = ReadEquipment().Single(item => item.EntityId == entityId);
        if (updated.Fields.Single(field => field.Key == key).Value != target)
            throw new InvalidOperationException("游戏没有接受装备潜能修改。");
        return updated;
    }

    public IReadOnlyList<AdapterEditorEntity> ReadMaterials()
    {
        var stash = GetStash();
        var counts = ReadShardCounts(ReadUInt64(stash + 0xA8));
        var entities = new List<AdapterEditorEntity>();
        var affixList = ResolveStaticFieldObject("LE.AssetManagement", "GlobalAssets", "_storage_MasterAffixesList");
        if (affixList == 0) throw new InvalidOperationException("词缀碎片定义尚未加载。");
        var allAffixes = ReadReferenceList(ReadUInt64(affixList + 0x78), "词缀碎片定义");
        if (allAffixes.Count == 0)
        {
            allAffixes = ReadReferenceArray(ReadUInt64(affixList + 0x58), "单属性词缀定义")
                .Concat(ReadReferenceArray(ReadUInt64(affixList + 0x60), "多属性词缀定义"))
                .ToList();
        }
        foreach (var affix in allAffixes)
        {
            if (!AffixHasShard(affix)) continue;
            var id = ReadInt32(affix + 0x44);
            var name = ReadLocalizedValue($"Item_Affix_{id}_DisplayName");
            if (string.IsNullOrWhiteSpace(name)) name = ReadManagedString(ReadUInt64(affix + 0x20));
            if (string.IsNullOrWhiteSpace(name)) name = ReadManagedString(ReadUInt64(affix + 0x18));
            if (string.IsNullOrWhiteSpace(name)) name = $"词缀碎片 {id}";
            counts.TryGetValue(id, out var quantity);
            entities.Add(MaterialEntity($"shard:{id}", name, "词缀碎片", quantity));
        }

        var materialCounts = ReadLocationPairCounts(ReadUInt64(stash + 0xB8), "已保存符文与雕文");
        foreach (var definition in ReadMaterialDefinitions())
        {
            materialCounts.TryGetValue((definition.Type, definition.SubType), out var saved);
            entities.Add(MaterialEntity(
                $"material:{definition.Type}:{definition.SubType}",
                definition.Name,
                "符文与雕文",
                saved.Quantity));
        }

        var keyCounts = ReadLocationPairCounts(ReadUInt64(stash + 0xC8), "已保存副本钥匙");
        foreach (var definition in ReadKeyDefinitions())
        {
            keyCounts.TryGetValue((definition.Type, definition.SubType), out var saved);
            entities.Add(MaterialEntity(
                $"key:{definition.Type}:{definition.SubType}",
                definition.Name,
                "副本钥匙",
                saved.Quantity));
        }
        return entities.OrderBy(item => item.Summary).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public AdapterEditorEntity WriteMaterial(string entityId, string key, long target)
    {
        if (key != "quantity" || target is < 0 or > int.MaxValue)
            throw new InvalidOperationException("资源数量超出允许范围。");
        var stash = GetStash();
        var liveUpdated = false;
        if (entityId.StartsWith("shard:", StringComparison.Ordinal))
        {
            if (!int.TryParse(entityId.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var shardId))
                throw new InvalidOperationException("词缀碎片身份无效。");
            var list = ReadUInt64(stash + 0xA8);
            if (list == 0) throw new InvalidOperationException("词缀碎片列表尚未加载。");
            var shard = ReadReferenceList(list, "已持有词缀碎片")
                .FirstOrDefault(item => ReadInt32(item + 0x10) == shardId);
            if (shard == 0)
            {
                if (target == 0) return ReadMaterials().Single(item => item.EntityId == entityId);
                shard = CreateManagedListElement(list);
                Write(shard + 0x10, BitConverter.GetBytes(shardId));
                AppendManagedReference(list, shard);
            }
            Write(shard + 0x14, BitConverter.GetBytes(checked((int)target)));
        }
        else if (TryParseLocationEntity(entityId, out var isKey, out var itemType, out var subType))
        {
            var list = ReadUInt64(stash + (isKey ? 0xC8UL : 0xB8UL));
            if (list == 0) throw new InvalidOperationException(isKey ? "副本钥匙列表尚未加载。" : "符文与雕文列表尚未加载。");
            var saved = FindLocationPair(list, itemType, subType);
            if (saved == 0)
            {
                if (target == 0) return ReadMaterials().Single(item => item.EntityId == entityId);
                saved = CreateLocationPair(list, itemType, subType, checked((int)target), isKey);
            }
            else
            {
                Write(saved + 0x2C, BitConverter.GetBytes(checked((int)target)));
            }
            if (isKey && target > 0) Write(stash + 0x118, [1]);
            liveUpdated = SyncLiveResourceQuantity(isKey, itemType, subType, checked((int)target));
        }
        else
        {
            throw new InvalidOperationException("资源身份无效。");
        }
        MarkEntityDirty(stash, "资源存档");
        var updated = ReadMaterials().Single(item => item.EntityId == entityId);
        if (updated.Fields[0].Value != target)
            throw new InvalidOperationException("游戏没有接受资源数量修改。");
        if (liveUpdated &&
            (!TryReadLiveResourceQuantity(entityId, out var liveQuantity, out _) || liveQuantity != target))
            throw new InvalidOperationException("资源存档已更新，但当前资源页没有接受同步值；请重新进入角色后刷新。");
        return updated;
    }

    private bool SyncLiveResourceQuantity(bool isKey, byte itemType, byte subType, int target)
    {
        if (isKey)
        {
            var keys = ReadUInt64(GetActiveMaterialStash() + 0x88);
            if (keys == 0) return false;
            var content = ReadUInt64(keys + 0x28);
            foreach (var entry in ReadReferenceList(content, "当前副本钥匙容器"))
            {
                var data = ReadUInt64(entry + 0x10);
                if (data == 0 || ReadByte(ObjectFieldAddress(data, "itemType")) != itemType ||
                    ReadUInt16(ObjectFieldAddress(data, "subType")) != subType) continue;
                Write(entry + 0x28, BitConverter.GetBytes(target));
                Write(keys + 0x14, [1, 1]);
                return true;
            }
            return false;
        }

        var materials = GetMaterialContainers();
        foreach (var definition in MaterialContainers)
        {
            var container = ReadUInt64(materials + definition.Offset);
            if (container == 0 || ReadInt32(container + 0x78) != subType) continue;
            var allowedTypes = ReadUInt64(container + 0x30);
            if (allowedTypes == 0 || ReadInt32(allowedTypes + 0x18) == 0 ||
                ReadInt32(allowedTypes + 0x20) != itemType) continue;
            var entry = ReadUInt64(container + 0x28);
            if (entry == 0) return false;
            Write(entry + 0x28, BitConverter.GetBytes(target));
            Write(container + 0x14, [1]);
            return true;
        }
        return false;
    }

    private bool TryReadLiveResourceQuantity(string entityId, out long quantity, out string reason)
    {
        quantity = 0;
        reason = string.Empty;
        if (!TryParseLocationEntity(entityId, out var isKey, out var itemType, out var subType))
        {
            reason = "资源身份无效";
            return false;
        }

        if (isKey)
        {
            var keys = ReadUInt64(GetActiveMaterialStash() + 0x88);
            var content = keys == 0 ? 0 : ReadUInt64(keys + 0x28);
            foreach (var entry in ReadReferenceList(content, "当前副本钥匙容器"))
            {
                var data = ReadUInt64(entry + 0x10);
                if (data == 0 || ReadByte(ObjectFieldAddress(data, "itemType")) != itemType ||
                    ReadUInt16(ObjectFieldAddress(data, "subType")) != subType) continue;
                quantity = Math.Max(0, ReadInt32(entry + 0x28));
                return true;
            }
            reason = "当前资源页尚未创建该钥匙槽位";
            return false;
        }

        var materials = GetMaterialContainers();
        foreach (var definition in MaterialContainers)
        {
            var container = ReadUInt64(materials + definition.Offset);
            if (container == 0 || ReadInt32(container + 0x78) != subType) continue;
            var allowedTypes = ReadUInt64(container + 0x30);
            if (allowedTypes == 0 || ReadInt32(allowedTypes + 0x18) == 0 ||
                ReadInt32(allowedTypes + 0x20) != itemType) continue;
            var entry = ReadUInt64(container + 0x28);
            if (entry == 0)
            {
                reason = "当前资源页尚未创建该材料槽位";
                return false;
            }
            quantity = Math.Max(0, ReadInt32(entry + 0x28));
            return true;
        }
        reason = "当前资源页没有匹配容器";
        return false;
    }

    public IReadOnlyList<AdapterEditorEntity> ReadMonolith()
    {
        var data = GetCharacterData();
        var result = new List<AdapterEditorEntity>
        {
            new("global", "角色异界总览", "不创建或解锁异界内容",
                [new("max-corruption", "最高腐化", ReadInt32(data + 0x158), 0, 1_000_000)])
        };
        foreach (var run in ReadReferenceList(ReadUInt64(data + 0x150), "异界时间线"))
        {
            var timeline = ReadInt32(run + 0x10);
            var difficulty = ReadInt32(run + 0x14);
            var web = ReadUInt64(run + 0x28);
            if (web == 0) continue;
            result.Add(new(
                $"timeline:{timeline}:{difficulty}",
                $"时间线 {timeline} · 难度 {difficulty}",
                difficulty > 0 ? "强化时间线" : "普通时间线",
                [
                    new("corruption", "腐化值", ReadInt32(web + 0x14), 0, 1_000_000),
                    new("stability", "稳定度", ReadInt32(run + 0x30), 0, 1_000_000),
                    new("depth", "深度", ReadInt32(run + 0x18), 0, 1_000_000),
                    new("gaze", "奥罗比斯凝视", ReadInt32(web + 0x1C), 0, 1_000_000)
                ]));
        }
        return result;
    }

    public AdapterEditorEntity WriteMonolith(string entityId, string key, long target)
    {
        if (target is < 0 or > 1_000_000) throw new InvalidOperationException("异界数值超出允许范围。");
        var data = GetCharacterData();
        if (entityId == "global" && key == "max-corruption")
        {
            Write(data + 0x158, BitConverter.GetBytes(checked((int)target)));
        }
        else
        {
            var run = FindMonolithRun(entityId);
            var web = ReadUInt64(run + 0x28);
            var address = key switch
            {
                "corruption" when web != 0 => web + 0x14,
                "stability" => run + 0x30,
                "depth" => run + 0x18,
                "gaze" when web != 0 => web + 0x1C,
                _ => throw new InvalidOperationException("异界字段无效。")
            };
            Write(address, BitConverter.GetBytes(checked((int)target)));
        }
        MarkCharacterDirty();
        var updated = ReadMonolith().Single(item => item.EntityId == entityId);
        if (updated.Fields.Single(item => item.Key == key).Value != target)
            throw new InvalidOperationException("游戏没有接受异界数值修改。");
        return updated;
    }

    public IReadOnlyList<AdapterEditorEntity> ReadWorld()
    {
        var result = new List<AdapterEditorEntity>();
        var camera = ResolveStaticFieldObject(string.Empty, "CameraManager", "instance");
        var virtualCamera = camera == 0 ? 0 : ReadUInt64(camera + 0x20);
        if (camera != 0 && virtualCamera != 0)
        {
            var fov = checked((long)Math.Round(ReadSingle(virtualCamera + 0xB8), MidpointRounding.AwayFromZero));
            result.Add(new(
                "camera",
                "游戏摄像头",
                "当前运行与场景",
                [new("field-of-view", "摄像头视野角度", fov, 20, 120, true, "场景切换或重启后由游戏恢复")]
            ));
        }

        return result;
    }

    public AdapterEditorEntity WriteWorld(string entityId, string key, long target)
    {
        if (entityId == "camera" && key == "field-of-view")
        {
            if (target is < 20 or > 120) throw new InvalidOperationException("摄像头视野角度必须在 20 到 120 之间。");
            var camera = ResolveStaticFieldObject(string.Empty, "CameraManager", "instance");
            var virtualCamera = camera == 0 ? 0 : ReadUInt64(camera + 0x20);
            if (virtualCamera == 0) throw new InvalidOperationException("当前场景尚未加载游戏摄像头。");
            Write(virtualCamera + 0xB8, BitConverter.GetBytes((float)target));
        }
        else
        {
            throw new InvalidOperationException("世界功能字段无效。");
        }

        var updated = ReadWorld().SingleOrDefault(item => item.EntityId == entityId)
            ?? throw new InvalidOperationException("修改后无法重新读取当前世界功能。");
        var value = updated.Fields.Single(item => item.Key == key).Value;
        if (entityId == "camera" && Math.Abs(value - target) > 1)
            throw new InvalidOperationException("游戏没有接受摄像头视野角度修改。");
        return updated;
    }

    public (string Version, string ProductName, string BuildGuid, string UnityVersion) ReadApplicationMetadata()
    {
        var version = ReadManagedString(Call(ResolveNamedMethodRva(
            "UnityEngine", "Application", "get_version", 0)));
        var rawProduct = ReadManagedString(Call(ResolveNamedMethodRva(
            "UnityEngine", "Application", "get_productName", 0)));
        var buildGuid = ReadManagedString(Call(ResolveNamedMethodRva(
            "UnityEngine", "Application", "get_buildGUID", 0)));
        var unityVersion = ReadManagedString(Call(ResolveNamedMethodRva(
            "UnityEngine", "Application", "get_unityVersion", 0)));
        return (
            string.IsNullOrWhiteSpace(version) ? "未提供" : version,
            rawProduct.Equals("Last Epoch", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rawProduct)
                ? "最后纪元"
                : rawProduct,
            string.IsNullOrWhiteSpace(buildGuid) ? "未提供" : buildGuid,
            string.IsNullOrWhiteSpace(unityVersion) ? "未提供" : unityVersion);
    }

    private AdapterEditorEntity MaterialEntity(string id, string name, string group, long count) =>
        new(id, name, group,
        [
            new("quantity", "数量", count, 0, int.MaxValue, true,
                "写入资源存档；已有资源同步当前资源页，首次创建的资源可能需要重新进入角色后显示")
        ]);

    private ulong GetPlayerActor() => RequirePlayerFinderRoot("playerActorComponent", "当前人物实体");

    private ulong GetPlayerStats(ulong actor)
    {
        var stats = ReadObjectReferenceField(actor, "stats");
        return stats != 0 ? stats : throw new InvalidOperationException("当前人物统计对象尚未加载。");
    }

    private ulong GetStatsList(ulong stats)
    {
        var offset = ResolveFieldOffset(ResolveClass(string.Empty, "Stats"), "stats", "Stats");
        var list = ReadUInt64(stats + offset);
        return list != 0 ? list : throw new InvalidOperationException("当前人物统计列表尚未加载。");
    }

    private int ReadCoreAttributeValue(ulong stats, int attribute, int property)
    {
        var attributesOffset = ResolveFieldOffset(
            ResolveClass(string.Empty, "CharacterStats"),
            "attributes",
            "CharacterStats");
        var dictionary = ReadUInt64(stats + attributesOffset);
        if (TryReadAttributeDictionaryValue(dictionary, attribute, out var value)) return value;

        // The dictionary is the final character-sheet value. The sum is retained only as a
        // compatibility fallback for a build that keeps the same Stats.Stat contract but changes
        // Dictionary entry layout.
        return checked((int)Math.Round(
            ReadMatchingStats(stats, property, 0).Sum(ReadAddedValue),
            MidpointRounding.AwayFromZero));
    }

    private bool TryReadAttributeDictionaryValue(ulong dictionary, int attribute, out int value)
    {
        value = 0;
        if (dictionary == 0) return false;
        var entries = ReadUInt64(dictionary + 0x18);
        var count = ReadInt32(dictionary + 0x20);
        if (entries == 0 || count is < 0 or > 64) return false;
        var capacity = ReadInt32(entries + 0x18);
        if (capacity < count || capacity > 256) return false;
        const ulong stride = 24;
        for (var index = 0; index < count; index++)
        {
            var entry = entries + 0x20UL + checked((ulong)index * stride);
            var key = ReadInt32(entry + 0x08);
            var pair = ReadUInt64(entry + 0x10);
            if (pair == 0 || key != attribute) continue;
            var pairValue = ObjectFieldAddress(pair, "value");
            value = ReadInt32(pairValue);
            return true;
        }
        return false;
    }

    private IReadOnlyList<ulong> ReadMatchingStats(
        ulong stats,
        int property,
        int tags,
        byte specialTag = 0,
        int extraTag = 0)
    {
        return ReadReferenceList(GetStatsList(stats), "人物实时统计")
            .Where(stat => ReadByte(stat + 0x10) == property &&
                           ReadByte(stat + 0x11) == specialTag &&
                           ReadInt32(stat + 0x14) == tags &&
                           ReadInt32(stat + 0x18) == extraTag)
            .ToList();
    }

    private int ReadIncreasedStatPercent(ulong stats, int property, int tags)
    {
        var value = CallFloat(
            ResolveNamedMethodRva(string.Empty, "Stats", "GetTotalIncreased", 4),
            stats,
            checked((ulong)property),
            checked((ulong)tags));
        return checked((int)Math.Round(value * 100d, MidpointRounding.AwayFromZero));
    }

    private float ReadAddedValue(ulong stat) => ReadSingle(stat + 0x1C);

    private void WriteCoreAttribute(ulong stats, int attribute, int property, int target)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = ReadCoreAttributeValue(stats, attribute, property);
            if (current == target) return;
            var requestedChange = target - current;
            RunAddStatModifierOnMainThread(stats, property, requestedChange, modificationType: 0, tags: 0);
            var changed = ReadCoreAttributeValue(stats, attribute, property);
            if (changed == target) return;
            var applied = changed - current;
            if (applied == 0) break;
            var correction = (target - changed) * (requestedChange / (float)applied);
            RunAddStatModifierOnMainThread(stats, property, correction, modificationType: 0, tags: 0);
        }
        if (ReadCoreAttributeValue(stats, attribute, property) != target)
            throw new InvalidOperationException("游戏没有把基础属性稳定到目标值，已停止继续写入。");
    }

    private void WriteIncreasedStatPercent(
        ulong actor,
        ulong stats,
        int property,
        int tags,
        int target)
    {
        if (property == 70)
        {
            WriteCooldownRecoveryPercent(actor, target);
            return;
        }

        if (property == 116)
        {
            WriteAreaEffectPercent(stats, target);
            return;
        }

        if (property == 47)
        {
            WritePotionDropRatePercent(actor, stats, target);
            return;
        }

        if (property == 105)
        {
            WriteExperienceGainPercent(stats, target);
            return;
        }

        if (property == 104)
        {
            WriteItemDropRatePercent(target);
            return;
        }

        var current = ReadIncreasedStatPercent(stats, property, tags);
        RunAddStatModifierOnMainThread(
            stats,
            property,
            (target - current) / 100f,
            modificationType: 1,
            tags);
    }

    private int ReadAreaEffectPercent(ulong stats)
    {
        return checked((int)Math.Round(
            ReadMatchingStats(stats, property: 116, tags: 0).Sum(ReadAddedValue) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WriteAreaEffectPercent(ulong stats, int target)
    {
        var current = ReadAreaEffectPercent(stats);
        RunAddStatModifierOnMainThread(
            stats,
            property: 116,
            changeValue: (target - current) / 100f,
            modificationType: 0,
            tags: 0);

        if (Math.Abs(ReadAreaEffectPercent(stats) - target) > 1)
            throw new InvalidOperationException("游戏没有接受效果范围统计的 addedValue 修改。");
    }

    private int ReadPotionDropRatePercent(ulong actor)
    {
        var potion = ReadObjectReferenceField(actor, "healthPotion");
        if (potion == 0) throw new InvalidOperationException("当前人物药剂组件尚未加载。");
        return checked((int)Math.Round(
            ReadSingle(ObjectFieldAddress(potion, "increasedPotionDropRate")) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WritePotionDropRatePercent(ulong actor, ulong stats, int target)
    {
        var potion = ReadObjectReferenceField(actor, "healthPotion");
        if (potion == 0) throw new InvalidOperationException("当前人物药剂组件尚未加载。");
        var current = ReadPotionDropRatePercent(actor);
        RunAddStatModifierOnMainThread(
            stats,
            property: 47,
            changeValue: (target - current) / 100f,
            modificationType: 0,
            tags: 0);
        RunRefreshPotionStatsOnMainThread(stats, potion);

        var applied = ReadPotionDropRatePercent(actor);
        if (Math.Abs(applied - target) > 1)
            throw new InvalidOperationException("游戏重建药剂统计后没有采用目标掉落率。");
        var baseChance = ReadSingle(ObjectFieldAddress(potion, "baseDropChance"));
        var expectedChance = baseChance * (1 + target / 100f);
        var actualChance = CallFloat(
            ResolveNamedMethodRva(string.Empty, "HealthPotion", "getDropChance", 0),
            potion);
        if (Math.Abs(actualChance - expectedChance) > Math.Max(0.0001f, Math.Abs(expectedChance) * 0.001f))
            throw new InvalidOperationException("药剂最终掉落概率没有按目标倍率重建。");
    }

    private int ReadItemDropRatePercent()
    {
        if (!TryGetItemDropHook(out var stub)) return 0;
        return checked((int)Math.Round(
            (ReadSingle(stub + ItemDropHookDataOffset) - 1f) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WriteItemDropRatePercent(int target)
    {
        var multiplier = 1f + target / 100f;
        if (TryGetItemDropHook(out var existingStub))
        {
            var data = new byte[24];
            BitConverter.GetBytes(multiplier).CopyTo(data, 0);
            Write(existingStub + ItemDropHookDataOffset, data);
            return;
        }
        if (target == 0) return;

        var site = ResolveItemDropHookSite();
        var current = Read(site, ItemDropChanceOriginalBytes.Length);
        if (!current.SequenceEqual(ItemDropChanceOriginalBytes))
            throw new InvalidOperationException("物品掉落概率结算点已被其他补丁修改，已拒绝叠加掉落率钩子。");

        var allocation = Allocate(0x100);
        var stub = unchecked((ulong)allocation.ToInt64());
        var installed = false;
        try
        {
            Write(stub, BuildItemDropHookPayload(stub, site, multiplier));
            var jump = new byte[ItemDropChanceOriginalBytes.Length];
            jump[0] = 0xFF;
            jump[1] = 0x25;
            BitConverter.GetBytes(stub).CopyTo(jump, 6);
            WriteExecutable(site, jump);
            installed = TryGetItemDropHook(out var installedStub) && installedStub == stub;
            if (!installed) throw new InvalidOperationException("物品掉落率钩子写入后校验失败。");
        }
        finally
        {
            if (!installed) VirtualFreeEx(_handle, allocation, 0, MemRelease);
        }
    }

    private static byte[] BuildItemDropHookPayload(
        ulong stub,
        ulong site,
        float multiplier)
    {
        var code = new Emitter();
        code.MovRax(stub + ItemDropHookDataOffset);
        code.Emit(0xF3, 0x0F, 0x11, 0x40, 0x04);
        code.Emit(0xF3, 0x0F, 0x59, 0x00);
        code.Emit(0xF3, 0x0F, 0x11, 0x40, 0x08);
        code.Emit(0x48, 0xFF, 0x40, 0x10);
        code.Emit(ItemDropChanceOriginalBytes);
        code.MovRax(site + unchecked((ulong)ItemDropChanceOriginalBytes.Length));
        code.Emit(0xFF, 0xE0);

        var generated = code.ToArray();
        if (generated.Length > ItemDropHookDataOffset)
            throw new InvalidOperationException("物品掉落率钩子超出安全代码缓冲区。");
        var payload = new byte[0x100];
        generated.CopyTo(payload, 0);
        BitConverter.GetBytes(multiplier).CopyTo(payload, ItemDropHookDataOffset);
        ItemDropHookMagic.CopyTo(payload, ItemDropHookMetadataOffset);
        BitConverter.GetBytes(site).CopyTo(payload, ItemDropHookMetadataOffset + 8);
        return payload;
    }

    private bool TryGetItemDropHook(out ulong stub)
    {
        stub = 0;
        var site = ResolveItemDropHookSite();
        var bytes = Read(site, ItemDropChanceOriginalBytes.Length);
        if (bytes[0] != 0xFF || bytes[1] != 0x25 || bytes[2] != 0 || bytes[3] != 0 ||
            bytes[4] != 0 || bytes[5] != 0)
            return false;
        stub = BitConverter.ToUInt64(bytes, 6);
        if (stub < 0x10000) return false;
        try
        {
            return Read(stub + ItemDropHookMetadataOffset, ItemDropHookMagic.Length)
                       .SequenceEqual(ItemDropHookMagic) &&
                   ReadUInt64(stub + ItemDropHookMetadataOffset + 8) == site;
        }
        catch
        {
            stub = 0;
            return false;
        }
    }

    private ulong ResolveItemDropHookSite()
    {
        var stateMachine = ResolveNestedClass(
            ResolveClass(string.Empty, "ItemDrop"),
            name => name.StartsWith("<DropItem>d__", StringComparison.Ordinal),
            "ItemDrop.DropItem 状态机");
        var moveNextInfo = ResolveMethodInfo(stateMachine, "MoveNext", 0, "ItemDrop.DropItem 状态机");
        var moveNext = ReadUInt64(moveNextInfo);
        if (moveNext < _moduleBase || moveNext >= _moduleEnd)
            throw new InvalidDataException("ItemDrop.DropItem 状态机入口不在 GameAssembly 安全范围内。");
        var chanceMethod = _moduleBase + ResolveNamedMethodRva(string.Empty, "ItemDrop", "getItemDropChance", 1);
        var bytes = Read(moveNext, 0x1000);
        var matches = new List<int>();
        for (var offset = 0; offset <= bytes.Length - 5 - ItemDropChanceOriginalBytes.Length; offset++)
        {
            if (bytes[offset] != 0xE8) continue;
            var target = unchecked((ulong)(checked((long)moveNext + offset + 5) +
                                                 BitConverter.ToInt32(bytes, offset + 1)));
            if (target != chanceMethod) continue;
            var following = bytes.AsSpan(offset + 5, ItemDropChanceOriginalBytes.Length);
            var isOriginal = following.SequenceEqual(ItemDropChanceOriginalBytes);
            var isInstalledJump = following[0] == 0xFF && following[1] == 0x25 &&
                                  following[2] == 0 && following[3] == 0 &&
                                  following[4] == 0 && following[5] == 0;
            if (isOriginal || isInstalledJump) matches.Add(offset + 5);
        }
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"无法唯一定位物品掉落概率最终结算点（候选 {matches.Count}），已拒绝安装掉落率钩子。");
        return checked(moveNext + unchecked((ulong)matches[0]));
    }

    private int ReadGoldQuantityPercent()
    {
        if (!TryGetGoldQuantityHook(out var stub)) return 0;
        return checked((int)Math.Round(
            (ReadSingle(stub + GoldQuantityHookDataOffset) - 1f) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WriteGoldQuantityPercent(int target)
    {
        var multiplier = 1f + target / 100f;
        if (TryGetGoldQuantityHook(out var existingStub))
        {
            var data = new byte[24];
            BitConverter.GetBytes(multiplier).CopyTo(data, 0);
            Write(existingStub + GoldQuantityHookDataOffset, data);
            return;
        }
        if (target == 0) return;

        var entry = _moduleBase + ResolveNamedMethodRva(
            string.Empty,
            "GroundItemManager",
            "dropGoldForPlayer",
            4);
        var current = Read(entry, GoldQuantityOriginalBytes.Length);
        if (!current.SequenceEqual(GoldQuantityOriginalBytes))
            throw new InvalidOperationException("金币落地入口已被其他补丁修改，已拒绝叠加金币数量倍率钩子。");

        var allocation = Allocate(0x100);
        var stub = unchecked((ulong)allocation.ToInt64());
        var installed = false;
        try
        {
            Write(stub, BuildGoldQuantityHookPayload(stub, entry, multiplier));
            var jump = Enumerable.Repeat((byte)0x90, GoldQuantityOriginalBytes.Length).ToArray();
            jump[0] = 0xFF;
            jump[1] = 0x25;
            jump[2] = jump[3] = jump[4] = jump[5] = 0;
            BitConverter.GetBytes(stub).CopyTo(jump, 6);
            WriteExecutable(entry, jump);
            installed = TryGetGoldQuantityHook(out var installedStub) && installedStub == stub;
            if (!installed) throw new InvalidOperationException("金币数量倍率钩子写入后校验失败。");
        }
        finally
        {
            if (!installed) VirtualFreeEx(_handle, allocation, 0, MemRelease);
        }
    }

    private static byte[] BuildGoldQuantityHookPayload(
        ulong stub,
        ulong entry,
        float multiplier)
    {
        var code = new Emitter();
        code.MovRax(stub + GoldQuantityHookDataOffset);
        code.Emit(0x44, 0x89, 0x40, 0x04);
        code.Emit(0x66, 0x41, 0x0F, 0x6E, 0xC0);
        code.Emit(0x0F, 0x5B, 0xC0);
        code.Emit(0xF3, 0x0F, 0x59, 0x00);
        code.Emit(0xF3, 0x44, 0x0F, 0x2C, 0xC0);
        code.Emit(0x44, 0x89, 0x40, 0x08);
        code.Emit(0x48, 0xFF, 0x40, 0x10);
        code.Emit(GoldQuantityOriginalBytes);
        code.MovRax(entry + unchecked((ulong)GoldQuantityOriginalBytes.Length));
        code.Emit(0xFF, 0xE0);

        var generated = code.ToArray();
        if (generated.Length > GoldQuantityHookDataOffset)
            throw new InvalidOperationException("金币数量倍率钩子超出安全代码缓冲区。");
        var payload = new byte[0x100];
        generated.CopyTo(payload, 0);
        BitConverter.GetBytes(multiplier).CopyTo(payload, GoldQuantityHookDataOffset);
        GoldQuantityHookMagic.CopyTo(payload, GoldQuantityHookMetadataOffset);
        BitConverter.GetBytes(entry).CopyTo(payload, GoldQuantityHookMetadataOffset + 8);
        return payload;
    }

    private bool TryGetGoldQuantityHook(out ulong stub)
    {
        stub = 0;
        var entry = _moduleBase + ResolveNamedMethodRva(
            string.Empty,
            "GroundItemManager",
            "dropGoldForPlayer",
            4);
        var bytes = Read(entry, GoldQuantityOriginalBytes.Length);
        if (bytes[0] != 0xFF || bytes[1] != 0x25 || bytes[2] != 0 || bytes[3] != 0 ||
            bytes[4] != 0 || bytes[5] != 0)
            return false;
        stub = BitConverter.ToUInt64(bytes, 6);
        if (stub < 0x10000) return false;
        try
        {
            return Read(stub + GoldQuantityHookMetadataOffset, GoldQuantityHookMagic.Length)
                       .SequenceEqual(GoldQuantityHookMagic) &&
                   ReadUInt64(stub + GoldQuantityHookMetadataOffset + 8) == entry;
        }
        catch
        {
            stub = 0;
            return false;
        }
    }

    private void WriteExperienceGainPercent(ulong stats, int target)
    {
        if (target != 0) EnsureExperienceGainHook();
        var current = ReadIncreasedStatPercent(stats, 105, 0);
        RunAddStatModifierOnMainThread(
            stats,
            property: 105,
            changeValue: (target - current) / 100f,
            modificationType: 1,
            tags: 0);
    }

    private void EnsureExperienceGainHook()
    {
        var entry = _moduleBase + ResolveNamedMethodRva(string.Empty, "ExperienceTracker", "GainExp", 3);
        var getTotalInfo = ResolveNamedMethodInfo(string.Empty, "Stats", "GetTotalIncreased", 4);
        var getTotalPointer = ReadUInt64(getTotalInfo);
        if (TryGetExperienceGainHook(out var existingStub))
        {
            WriteExecutable(
                existingStub,
                BuildExperienceGainHookPayload(existingStub, entry, getTotalInfo, getTotalPointer));
            return;
        }
        var current = Read(entry, ExperienceGainOriginalBytes.Length);
        if (!current.SequenceEqual(ExperienceGainOriginalBytes))
            throw new InvalidOperationException("经验结算入口已被其他补丁修改，已拒绝叠加经验倍率钩子。");

        var allocation = Allocate(0x100);
        var stub = unchecked((ulong)allocation.ToInt64());
        var installed = false;
        try
        {
            Write(stub, BuildExperienceGainHookPayload(stub, entry, getTotalInfo, getTotalPointer));

            var jump = new byte[ExperienceGainOriginalBytes.Length];
            jump[0] = 0xFF;
            jump[1] = 0x25;
            BitConverter.GetBytes(stub).CopyTo(jump, 6);
            WriteExecutable(entry, jump);
            installed = TryGetExperienceGainHook(out var installedStub) && installedStub == stub;
            if (!installed) throw new InvalidOperationException("经验倍率钩子写入后校验失败。");
        }
        finally
        {
            if (!installed) VirtualFreeEx(_handle, allocation, 0, MemRelease);
        }
    }

    private static byte[] BuildExperienceGainHookPayload(
        ulong stub,
        ulong entry,
        ulong getTotalInfo,
        ulong getTotalPointer)
    {
        var code = new Emitter();
        code.Emit(0x48, 0x83, 0xEC, 0x68);
        code.Emit(0x48, 0x89, 0x4C, 0x24, 0x40);
        code.Emit(0x48, 0x89, 0x54, 0x24, 0x48);
        code.Emit(0x4C, 0x89, 0x44, 0x24, 0x50);
        code.Emit(0x4C, 0x89, 0x4C, 0x24, 0x58);
        code.Emit(0x48, 0x8B, 0x49, 0x20);
        code.Emit(0x48, 0x85, 0xC9);
        var noStatsJump = code.EmitNearConditionalJump(0x84);
        code.MovEdx(105);
        code.Emit(0x45, 0x33, 0xC0);
        code.Emit(0x45, 0x33, 0xC9);
        code.MovDwordRspOffset(0x20, 0);
        code.MovRax(getTotalInfo);
        code.MovRspOffsetRax(0x28);
        code.MovRax(getTotalPointer);
        code.CallRax();
        code.MovEax(0x3F800000);
        code.MovdXmm1Eax();
        code.Emit(0xF3, 0x0F, 0x58, 0xC1);
        code.Emit(0x48, 0x8B, 0x44, 0x24, 0x48);
        code.Emit(0xF3, 0x0F, 0x5A, 0xC0);
        code.Emit(0xF2, 0x48, 0x0F, 0x2A, 0xC8);
        code.Emit(0xF2, 0x0F, 0x59, 0xC1);
        code.Emit(0xF2, 0x48, 0x0F, 0x2C, 0xD0);
        code.MovRax(stub + ExperienceHookDataOffset);
        code.Emit(0x48, 0x8B, 0x4C, 0x24, 0x48);
        code.Emit(0x48, 0x89, 0x08);
        code.Emit(0x48, 0x89, 0x50, 0x08);
        code.Emit(0x48, 0xFF, 0x40, 0x10);
        var restoreLabel = code.Position;
        code.PatchNearJump(noStatsJump, restoreLabel);
        code.Emit(0x48, 0x8B, 0x4C, 0x24, 0x40);
        code.Emit(0x4C, 0x8B, 0x44, 0x24, 0x50);
        code.Emit(0x4C, 0x8B, 0x4C, 0x24, 0x58);
        code.Emit(0x48, 0x83, 0xC4, 0x68);
        code.Emit(ExperienceGainOriginalBytes);
        code.MovRax(entry + unchecked((ulong)ExperienceGainOriginalBytes.Length));
        code.Emit(0xFF, 0xE0);

        var generated = code.ToArray();
        if (generated.Length > ExperienceHookDataOffset)
            throw new InvalidOperationException("经验倍率钩子超出安全代码缓冲区。");
        var payload = new byte[0x100];
        generated.CopyTo(payload, 0);
        ExperienceHookMagic.CopyTo(payload, ExperienceHookMetadataOffset);
        BitConverter.GetBytes(entry).CopyTo(payload, ExperienceHookMetadataOffset + 8);
        return payload;
    }

    private bool TryGetExperienceGainHook(out ulong stub)
    {
        stub = 0;
        var entry = _moduleBase + ResolveNamedMethodRva(string.Empty, "ExperienceTracker", "GainExp", 3);
        var bytes = Read(entry, ExperienceGainOriginalBytes.Length);
        if (bytes.Length != ExperienceGainOriginalBytes.Length ||
            bytes[0] != 0xFF || bytes[1] != 0x25 || bytes[2] != 0 || bytes[3] != 0 ||
            bytes[4] != 0 || bytes[5] != 0)
            return false;
        stub = BitConverter.ToUInt64(bytes, 6);
        if (stub < 0x10000) return false;
        try
        {
            return Read(stub + ExperienceHookMetadataOffset, ExperienceHookMagic.Length)
                       .SequenceEqual(ExperienceHookMagic) &&
                   ReadUInt64(stub + ExperienceHookMetadataOffset + 8) == entry;
        }
        catch (Win32Exception)
        {
            stub = 0;
            return false;
        }
    }

    private int ReadCooldownRecoveryPercent(ulong actor)
    {
        var stats = GetPlayerStats(actor);
        return checked((int)Math.Round(
            ReadMatchingStats(stats, 70, 0).Sum(ReadAddedValue) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WriteCooldownRecoveryPercent(ulong actor, int target)
    {
        if (target <= -100)
            throw new InvalidOperationException("提高冷却恢复速度必须大于 -100%，否则无法安全恢复原始技能冷却。");

        var stats = GetPlayerStats(actor);
        var manager = ReadObjectReferenceField(actor, "chargeManager");
        if (manager == 0) throw new InvalidOperationException("当前人物冷却管理器尚未加载。");

        var matchingStats = ReadMatchingStats(stats, 70, 0);
        var currentFraction = matchingStats.Sum(ReadAddedValue);
        var legacyIncreasedFraction = matchingStats.Sum(ReadIncreasedValue);
        var targetFraction = target / 100f;
        if (!float.IsFinite(currentFraction) || currentFraction <= -0.9999f)
            throw new InvalidDataException("当前冷却恢复倍率无法安全换算，已停止写入。");

        var regenList = ReadObjectReferenceField(manager, "chargeRegen");
        var regenValues = ReadFloatList(regenList, "人物技能充能恢复");
        if (regenValues.Count is 0 or > 32)
            throw new InvalidDataException($"人物技能充能恢复记录数异常（{regenValues.Count}），已停止写入。");
        var abilities = ReadReferenceListWithNulls(
            ReadObjectReferenceField(manager, "abilities"),
            "人物技能冷却定义");
        if (abilities.Count != regenValues.Count)
            throw new InvalidDataException("人物技能定义与恢复列表长度不一致，已停止写入。");
        var baseRegenValues = abilities
            .Select(ability => ability == 0
                ? 0f
                : ReadSingle(ObjectFieldAddress(ability, "chargesGainedPerSecond")))
            .ToArray();
        // Earlier local builds wrote property 70 as increasedValue, but the 1.5.1
        // cooldown consumer calls GetTotalAddedForAbility and reads addedValue.
        // Remove that exact generic-tag residue before writing the correct mod type.
        if (Math.Abs(legacyIncreasedFraction) > 0.0001f)
        {
            RunAddStatModifierOnMainThread(
                stats,
                property: 70,
                changeValue: -legacyIncreasedFraction,
                modificationType: 1,
                tags: 0);
        }

        var change = targetFraction - currentFraction;
        if (Math.Abs(change) > 0.0001f)
        {
            RunAddStatModifierOnMainThread(
                stats,
                property: 70,
                changeValue: change,
                modificationType: 0,
                tags: 0);
        }
        if (regenValues.All(value => value <= 0))
            throw new InvalidOperationException("当前技能栏没有可验证的冷却技能，已停止写入。");

        RunRefreshCooldownOnMainThread(stats, manager);

        var appliedFraction = ReadMatchingStats(stats, 70, 0).Sum(ReadAddedValue);
        if (Math.Abs(appliedFraction - targetFraction) > 0.0001f)
            throw new InvalidOperationException("游戏没有接受冷却恢复统计的 addedValue 修改。");
        var appliedValues = ReadFloatList(regenList, "人物技能充能恢复");
        var endpointChanged = false;
        for (var index = 0; index < regenValues.Count; index++)
        {
            if (regenValues[index] <= 0) continue;
            var expected = regenValues[index] + baseRegenValues[index] * change;
            if (Math.Abs(appliedValues[index] - expected) > Math.Max(0.0001f, Math.Abs(expected) * 0.001f))
                throw new InvalidOperationException("游戏重建了技能冷却缓存，目标倍率没有稳定生效。");
            endpointChanged |= Math.Abs(appliedValues[index] - regenValues[index]) > 0.0001f;
        }
        if (Math.Abs(change) > 0.0001f && !endpointChanged)
            throw new InvalidOperationException("实际技能冷却恢复缓存没有发生变化。");
    }

    private float ReadIncreasedValue(ulong stat) => ReadSingle(stat + 0x20);

    private int ReadMovementCooldownPercent(ulong actor)
    {
        const int abilityProperty = 58;
        const int evadeAbilityId = 0x30E;
        const byte movementCooldownPropertyIndex = 9;
        var stats = GetPlayerStats(actor);
        return checked((int)Math.Round(
            ReadMatchingStats(
                    stats,
                    abilityProperty,
                    evadeAbilityId,
                    movementCooldownPropertyIndex)
                .Sum(ReadAddedValue) * 100d,
            MidpointRounding.AwayFromZero));
    }

    private void WriteMovementCooldownPercent(ulong actor, int target)
    {
        const int abilityProperty = 58;
        const int evadeAbilityId = 0x30E;
        const byte movementCooldownPropertyIndex = 9;
        var stats = GetPlayerStats(actor);
        var manager = ReadObjectReferenceField(actor, "mutatorManager");
        if (manager == 0) throw new InvalidOperationException("当前人物技能倍率管理器尚未加载。");
        var chargeManager = ReadObjectReferenceField(actor, "chargeManager");
        if (chargeManager == 0) throw new InvalidOperationException("当前人物冷却管理器尚未加载。");
        var regenList = ReadObjectReferenceField(chargeManager, "chargeRegen");
        var before = ReadFloatList(regenList, "人物技能充能恢复");
        var current = ReadMatchingStats(
                stats,
                abilityProperty,
                evadeAbilityId,
                movementCooldownPropertyIndex)
            .Sum(ReadAddedValue);
        var targetFraction = target / 100f;
        var change = targetFraction - current;
        RunAddStatModifierOnMainThread(
            stats,
            abilityProperty,
            change,
            modificationType: 0,
            tags: evadeAbilityId,
            specialTag: movementCooldownPropertyIndex);
        RunRefreshMovementCooldownOnMainThread(stats, manager, chargeManager);

        var applied = ReadMatchingStats(
                stats,
                abilityProperty,
                evadeAbilityId,
                movementCooldownPropertyIndex)
            .Sum(ReadAddedValue);
        if (Math.Abs(applied - targetFraction) > 0.0001f)
            throw new InvalidOperationException("游戏没有接受移动技能冷却恢复的上游能力统计修改。");
        var managerValue = ReadSingle(ObjectFieldAddress(manager, "increasedCooldownRecoverySpeedForMovementSkills"));
        if (Math.Abs(managerValue - targetFraction) > 0.0001f)
            throw new InvalidOperationException("游戏重建移动技能统计后没有采用目标值。");
        var after = ReadFloatList(regenList, "人物技能充能恢复");
        if (after.Count != before.Count)
            throw new InvalidDataException("移动冷却刷新前后的技能恢复列表长度不一致。");
        var direction = Math.Sign(change);
        if (direction != 0 && !before.Zip(after).Any(pair =>
                pair.First > 0 && Math.Sign(pair.Second - pair.First) == direction))
            throw new InvalidOperationException("移动技能冷却统计已更新，但实际技能冷却缓存没有采用新值。");
    }

    private void RunAddStatModifierOnMainThread(
        ulong stats,
        int property,
        float changeValue,
        int modificationType,
        int tags,
        byte specialTag = 0,
        int extraTag = 0)
    {
        if (Math.Abs(changeValue) < 0.0001f) return;
        if (property is < 0 or > byte.MaxValue) throw new InvalidDataException("人物统计属性身份无效。");
        var methodInfo = ResolveNamedMethodInfo(string.Empty, "CharacterStats", "AddStatModifier", 6);
        var methodPointer = ReadUInt64(methodInfo);
        var updateInfo = ResolveNamedMethodInfo(string.Empty, "BaseStats", "UpdateStats", 0);
        var updatePointer = ReadUInt64(updateInfo);
        RunOnCharacterStatsTick(stats, (code, stackSize) =>
        {
            code.MovRcx(stats);
            code.MovEdx(checked((uint)property));
            code.MovEax(unchecked((uint)BitConverter.SingleToInt32Bits(changeValue)));
            code.MovdXmm2Eax();
            code.MovR9d(checked((uint)modificationType));
            code.MovDwordRspOffset(0x20, unchecked((uint)tags));
            code.MovDwordRspOffset(0x28, specialTag);
            code.MovDwordRspOffset(0x30, unchecked((uint)extraTag));
            code.MovRax(methodInfo);
            code.MovRspOffsetRax(0x38);
            code.MovRax(methodPointer);
            code.CallRax();
            code.MovRcx(stats);
            code.MovRdx(updateInfo);
            code.MovRax(updatePointer);
            code.CallRax();
        });
    }

    private void RunRefreshCooldownOnMainThread(ulong stats, ulong manager)
    {
        var refreshInfo = ResolveNamedMethodInfo(string.Empty, "ChargeManager", "RefreshAllChargeInfo", 0);
        var refreshPointer = ReadUInt64(refreshInfo);
        var sheet = ResolveStaticFieldObject(string.Empty, "CharacterSheet", "instance");
        var updateInfo = sheet == 0 ? 0 : ResolveNamedMethodInfo(string.Empty, "CharacterSheet", "UpdateSheet", 0);
        var updatePointer = updateInfo == 0 ? 0 : ReadUInt64(updateInfo);
        RunOnCharacterStatsTick(stats, (code, _) =>
        {
            code.MovRcx(manager);
            code.MovRdx(refreshInfo);
            code.MovRax(refreshPointer);
            code.CallRax();
            if (sheet == 0) return;
            code.MovRcx(sheet);
            code.MovRdx(updateInfo);
            code.MovRax(updatePointer);
            code.CallRax();
        }, operationAfterOriginal: true);
    }

    private void RunRefreshMovementCooldownOnMainThread(
        ulong stats,
        ulong abilityManager,
        ulong chargeManager)
    {
        var abilityUpdateInfo = ResolveNamedMethodInfo(
            string.Empty,
            "AbilityStatsMutatorManager",
            "UpdateAbilityStats",
            0);
        var abilityUpdatePointer = ReadUInt64(abilityUpdateInfo);
        var chargeRefreshInfo = ResolveNamedMethodInfo(string.Empty, "ChargeManager", "RefreshAllChargeInfo", 0);
        var chargeRefreshPointer = ReadUInt64(chargeRefreshInfo);
        var sheet = ResolveStaticFieldObject(string.Empty, "CharacterSheet", "instance");
        var sheetUpdateInfo = sheet == 0 ? 0 : ResolveNamedMethodInfo(string.Empty, "CharacterSheet", "UpdateSheet", 0);
        var sheetUpdatePointer = sheetUpdateInfo == 0 ? 0 : ReadUInt64(sheetUpdateInfo);
        RunOnCharacterStatsTick(stats, (code, _) =>
        {
            code.MovRcx(abilityManager);
            code.MovRdx(abilityUpdateInfo);
            code.MovRax(abilityUpdatePointer);
            code.CallRax();
            code.MovRcx(chargeManager);
            code.MovRdx(chargeRefreshInfo);
            code.MovRax(chargeRefreshPointer);
            code.CallRax();
            if (sheet == 0) return;
            code.MovRcx(sheet);
            code.MovRdx(sheetUpdateInfo);
            code.MovRax(sheetUpdatePointer);
            code.CallRax();
        }, operationAfterOriginal: true);
    }

    private void RunRefreshPotionStatsOnMainThread(ulong stats, ulong potion)
    {
        var updateInfo = ResolveNamedMethodInfo(string.Empty, "HealthPotion", "updatePotionStats", 0);
        var updatePointer = ReadUInt64(updateInfo);
        RunOnCharacterStatsTick(stats, (code, _) =>
        {
            code.MovRcx(potion);
            code.MovRdx(updateInfo);
            code.MovRax(updatePointer);
            code.CallRax();
        }, operationAfterOriginal: true);
    }

    private void RunOnCharacterStatsTick(
        ulong stats,
        Action<Emitter, int> emitOperation,
        bool operationAfterOriginal = false)
    {
        const int stackSize = 0x88;
        var (slotAddress, originalPointer) = ResolveVirtualMethodSlot(
            stats,
            string.Empty,
            "CharacterStats",
            "OnUpdateTick",
            1);
        var statusAddress = Allocate(4);
        var codeAddress = Allocate(1024);
        var status = unchecked((ulong)statusAddress.ToInt64());
        var trampoline = unchecked((ulong)codeAddress.ToInt64());
        var hookInstalled = false;
        var completed = false;
        try
        {
            Write(status, new byte[4]);
            var code = new Emitter();
            code.Emit(0x48, 0x81, 0xEC); code.Emit(BitConverter.GetBytes(stackSize));
            code.Emit(0x48, 0x89, 0x8C, 0x24, 0x60, 0x00, 0x00, 0x00);
            code.Emit(0x48, 0x89, 0x94, 0x24, 0x68, 0x00, 0x00, 0x00);
            code.Emit(0x4C, 0x89, 0x84, 0x24, 0x70, 0x00, 0x00, 0x00);
            code.Emit(0x4C, 0x89, 0x8C, 0x24, 0x78, 0x00, 0x00, 0x00);
            code.Emit(0xF3, 0x0F, 0x11, 0x8C, 0x24, 0x80, 0x00, 0x00, 0x00);
            code.MovRax(stats);
            code.Emit(0x48, 0x39, 0xC1);
            var otherInstanceJump = code.EmitNearConditionalJump(0x85);

            // Restore the original vtable entry before executing any game logic. The hook is
            // therefore one-shot even if a later native call takes an unexpected path.
            code.MovRax(slotAddress);
            code.MovRdx(originalPointer);
            code.Emit(0x48, 0x89, 0x10);

            if (!operationAfterOriginal) emitOperation(code, stackSize);
            EmitOriginalCharacterStatsTick(code, originalPointer);
            if (operationAfterOriginal) emitOperation(code, stackSize);
            code.MovRax(status);
            code.Emit(0xC7, 0x00, 0x01, 0x00, 0x00, 0x00);
            var completedJump = code.EmitNearJump();

            var otherInstanceLabel = code.Position;
            code.PatchNearJump(otherInstanceJump, otherInstanceLabel);
            EmitOriginalCharacterStatsTick(code, originalPointer);

            var completedLabel = code.Position;
            code.PatchNearJump(completedJump, completedLabel);
            code.Emit(0x48, 0x81, 0xC4); code.Emit(BitConverter.GetBytes(stackSize));
            code.Emit(0xC3);

            var bytes = code.ToArray();
            if (bytes.Length > 1024) throw new InvalidOperationException("人物属性主线程操作超出安全代码缓冲区。");
            Write(trampoline, bytes);
            if (ReadUInt64(slotAddress) != originalPointer)
                throw new InvalidOperationException("人物统计虚表在修改前已经变化，已取消本次操作。");
            Write(slotAddress, BitConverter.GetBytes(trampoline));
            hookInstalled = true;

            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (ReadInt32(status) == 1)
                {
                    completed = true;
                    break;
                }
                Thread.Sleep(10);
            }
            if (!completed)
                throw new TimeoutException("Last Epoch 没有在五秒内执行人物属性主线程操作。请确认已进入角色场景后重试。");
            Thread.Sleep(50);
        }
        finally
        {
            if (hookInstalled && ReadUInt64(slotAddress) == trampoline)
                Write(slotAddress, BitConverter.GetBytes(originalPointer));
            if (completed)
            {
                VirtualFreeEx(_handle, statusAddress, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
            // A timeout can race a game thread that already fetched the trampoline pointer.
            // Keeping these two tiny allocations until process exit is safer than freeing code
            // that may still be executing.
        }
    }

    private static void EmitOriginalCharacterStatsTick(Emitter code, ulong originalPointer)
    {
        code.Emit(0x48, 0x8B, 0x8C, 0x24, 0x60, 0x00, 0x00, 0x00);
        code.Emit(0x48, 0x8B, 0x94, 0x24, 0x68, 0x00, 0x00, 0x00);
        code.Emit(0x4C, 0x8B, 0x84, 0x24, 0x70, 0x00, 0x00, 0x00);
        code.Emit(0x4C, 0x8B, 0x8C, 0x24, 0x78, 0x00, 0x00, 0x00);
        code.Emit(0xF3, 0x0F, 0x10, 0x8C, 0x24, 0x80, 0x00, 0x00, 0x00);
        code.MovRax(originalPointer);
        code.CallRax();
    }

    private (ulong SlotAddress, ulong MethodPointer) ResolveVirtualMethodSlot(
        ulong instance,
        string @namespace,
        string className,
        string methodName,
        int parameterCount)
    {
        var runtimeClass = ReadUInt64(instance);
        if (runtimeClass == 0) throw new InvalidDataException("人物统计运行时类型无效。");
        var methodPointer = _moduleBase + ResolveNamedMethodRva(@namespace, className, methodName, parameterCount);
        var bytes = Read(runtimeClass, 0x800);
        var pattern = BitConverter.GetBytes(methodPointer);
        var matches = new List<int>();
        for (var offset = 0; offset <= bytes.Length - 16; offset += 8)
        {
            if (bytes.AsSpan(offset, 8).SequenceEqual(pattern) && BitConverter.ToUInt64(bytes, offset + 8) != 0)
                matches.Add(offset);
        }
        if (matches.Count != 1)
            throw new InvalidDataException(
                $"无法唯一定位人物统计虚方法 {methodName}（候选 {matches.Count}），已拒绝安装主线程操作。");
        return (checked(runtimeClass + (ulong)matches[0]), methodPointer);
    }

    private ulong FindMonolithRun(string entityId)
    {
        var parts = entityId.Split(':');
        if (parts.Length != 3 || parts[0] != "timeline" ||
            !int.TryParse(parts[1], out var timeline) || !int.TryParse(parts[2], out var difficulty))
            throw new InvalidOperationException("异界时间线身份无效。");
        return ReadReferenceList(ReadUInt64(GetCharacterData() + 0x150), "异界时间线")
            .FirstOrDefault(run => ReadInt32(run + 0x10) == timeline && ReadInt32(run + 0x14) == difficulty) is var found && found != 0
            ? found
            : throw new InvalidOperationException("当前存档已找不到该异界时间线。");
    }

    private int SumAllocatedPoints(ulong nodes) =>
        ReadReferenceList(nodes, "技能节点").Sum(node => (int)ReadByte(node + 0x11));

    private int ReadSkillXpRequirement(int level)
    {
        if (level < 2) return 0;
        if (level > 20) throw new InvalidOperationException("技能等级超出当前构建的上限。");
        var table = ResolveStaticFieldObject(string.Empty, "SpecialisedAbilityManager", "abilityLevelRequirements");
        var index = level - 2;
        if (table == 0 || ReadInt32(table + 0x18) <= index)
            throw new InvalidOperationException("技能经验表尚未加载。");
        return ReadInt32(table + 0x20UL + unchecked((ulong)index * 4UL));
    }

    private Dictionary<int, int> ReadShardCounts(ulong list)
    {
        var result = new Dictionary<int, int>();
        foreach (var shard in ReadReferenceList(list, "已持有词缀碎片"))
            result[ReadInt32(shard + 0x10)] = ReadInt32(shard + 0x14);
        return result;
    }

    private ulong GetStash()
    {
        // Offline mode no longer initializes PlayerFinder.globalData in this build.
        // The active material-stash controller keeps the same authoritative Stash
        // entity that its UI and save loop use, so resolve it from the live container.
        var stash = ReadUInt64(GetActiveMaterialStash() + 0x98);
        return stash != 0 ? stash : throw new InvalidOperationException("离线仓储数据尚未加载。");
    }

    private IReadOnlyList<ResourceDefinition> ReadMaterialDefinitions()
    {
        var materials = GetMaterialContainers();
        var result = new List<ResourceDefinition>(MaterialContainers.Length);
        foreach (var definition in MaterialContainers)
        {
            var container = ReadUInt64(materials + definition.Offset);
            if (container == 0) continue;
            var allowedTypes = ReadUInt64(container + 0x30);
            var type = allowedTypes == 0 || ReadInt32(allowedTypes + 0x18) == 0
                ? -1
                : ReadInt32(allowedTypes + 0x20);
            var subType = ReadInt32(container + 0x78);
            if (type is < 0 or > byte.MaxValue || subType is < 0 or > byte.MaxValue)
                throw new InvalidDataException($"{definition.Name} 的资源身份超出当前构建允许范围。");
            result.Add(new(checked((byte)type), checked((byte)subType), definition.Name));
        }
        return result;
    }

    private IReadOnlyList<ResourceDefinition> ReadKeyDefinitions()
    {
        const byte keyType = 104;
        var itemList = ResolveStaticFieldObject("LE.AssetManagement", "GlobalAssets", "_storage_MasterItemsList");
        if (itemList == 0) throw new InvalidOperationException("物品定义尚未加载。");
        var baseItems = ReadReferenceArray(ReadUInt64(itemList + 0x28), "非装备物品定义");
        var keyBase = baseItems.FirstOrDefault(item => ReadInt32(item + 0x28) == keyType);
        if (keyBase == 0) throw new InvalidOperationException("副本钥匙定义尚未加载。");
        var result = new List<ResourceDefinition>();
        foreach (var subItem in ReadReferenceList(ReadUInt64(keyBase + 0x50), "副本钥匙定义"))
        {
            var subType = ReadInt32(subItem + 0x20);
            if (subType is < 0 or > byte.MaxValue) continue;
            var name = ReadLocalizedValue($"Item_SubType_Name_{keyType}_{subType}");
            if (string.IsNullOrWhiteSpace(name)) name = ReadManagedString(ReadUInt64(subItem + 0x18));
            if (string.IsNullOrWhiteSpace(name)) name = ReadManagedString(ReadUInt64(subItem + 0x10));
            if (string.IsNullOrWhiteSpace(name)) name = $"副本钥匙 {subType}";
            result.Add(new(keyType, checked((byte)subType), name));
        }
        return result.OrderBy(item => item.SubType).ToList();
    }

    private Dictionary<(byte Type, byte SubType), SavedResourceRecord> ReadLocationPairCounts(ulong list, string description)
    {
        var result = new Dictionary<(byte Type, byte SubType), SavedResourceRecord>();
        foreach (var pair in ReadReferenceList(list, description))
        {
            if (!TryReadLocationPairIdentity(pair, out var type, out var subType)) continue;
            result[(type, subType)] = new(pair, Math.Max(0, ReadInt32(pair + 0x2C)));
        }
        return result;
    }

    private bool TryReadLocationPairIdentity(ulong pair, out byte type, out byte subType)
    {
        type = 0;
        subType = 0;
        var data = pair == 0 ? 0 : ReadUInt64(pair + 0x18);
        if (data == 0 || ReadInt32(data + 0x18) < 7) return false;
        type = ReadByte(data + 0x25);
        subType = ReadByte(data + 0x26);
        return true;
    }

    private ulong FindLocationPair(ulong list, byte type, byte subType) =>
        ReadReferenceList(list, "已保存资源")
            .FirstOrDefault(pair => TryReadLocationPairIdentity(pair, out var foundType, out var foundSubType) &&
                                    foundType == type && foundSubType == subType);

    private static bool TryParseLocationEntity(string entityId, out bool isKey, out byte type, out byte subType)
    {
        isKey = false;
        type = 0;
        subType = 0;
        var parts = entityId.Split(':');
        if (parts.Length != 3 || parts[0] is not ("material" or "key") ||
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out type) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out subType))
            return false;
        isKey = parts[0] == "key";
        return isKey ? type == 104 : type is 102 or 103;
    }

    private ulong CreateLocationPair(ulong list, byte type, byte subType, int quantity, bool isKey)
    {
        var pair = CreateManagedListElement(list);
        var individualId = RandomNumberGenerator.GetInt32(16, int.MaxValue);
        var data = CreateManagedByteArray(
        [
            6,
            checked((byte)((individualId >> 24) & 0xFF)),
            checked((byte)((individualId >> 16) & 0xFF)),
            checked((byte)((individualId >> 8) & 0xFF)),
            checked((byte)(individualId & 0xFF)),
            type,
            subType
        ]);
        SetManagedReference(pair, pair + 0x18, data);
        Write(pair + 0x20, BitConverter.GetBytes(isKey ? 0 : -4));
        Write(pair + 0x24, BitConverter.GetBytes(isKey ? subType : -4));
        Write(pair + 0x2C, BitConverter.GetBytes(quantity));
        Write(pair + 0x30, BitConverter.GetBytes(checked((ushort)(isKey ? 100 : 0))));
        Write(pair + 0x32, [2]);
        AppendManagedReference(list, pair);
        return pair;
    }

    private ulong CreateManagedListElement(ulong list)
    {
        var array = list == 0 ? 0 : ReadUInt64(list + 0x10);
        var arrayClass = array == 0 ? 0 : ReadUInt64(array);
        var elementClass = arrayClass == 0 ? 0 : Call(_classGetElementClassRva, arrayClass);
        var value = elementClass == 0 ? 0 : Call(_objectNewRva, elementClass);
        return value != 0 ? value : throw new InvalidOperationException("无法为缺失资源创建安全的游戏记录。");
    }

    private ulong CreateManagedByteArray(byte[] value)
    {
        var byteClass = ResolveClass("System", "Byte");
        var arrayClass = Call(_arrayClassGetRva, byteClass, 1);
        var array = arrayClass == 0 ? 0 : Call(_arrayNewSpecificRva, arrayClass, checked((ulong)value.Length));
        if (array == 0 || ReadInt32(array + 0x18) != value.Length)
            throw new InvalidOperationException("无法为缺失资源创建序列化数据。");
        Write(array + 0x20, value);
        return array;
    }

    private void AppendManagedReference(ulong list, ulong value)
    {
        var array = ReadUInt64(list + 0x10);
        var count = ReadInt32(list + 0x18);
        var capacity = array == 0 ? 0 : ReadInt32(array + 0x18);
        if (count is < 0 or > 100_000 || capacity is < 0 or > 100_000 || count > capacity)
            throw new InvalidDataException("资源列表结构无效，已拒绝创建记录。");
        if (count == capacity)
        {
            if (array == 0) throw new InvalidDataException("资源列表缺少托管数组类型，已拒绝创建记录。");
            var arrayClass = ReadUInt64(array);
            var newCapacity = Math.Max(4, checked(count * 2));
            var expanded = Call(_arrayNewSpecificRva, arrayClass, checked((ulong)newCapacity));
            if (expanded == 0 || ReadInt32(expanded + 0x18) != newCapacity)
                throw new InvalidOperationException("无法扩展资源列表容量。");
            for (var index = 0; index < count; index++)
            {
                var current = ReadUInt64(array + 0x20UL + checked((ulong)index * 8UL));
                if (current != 0) SetManagedReference(expanded, expanded + 0x20UL + checked((ulong)index * 8UL), current);
            }
            SetManagedReference(list, list + 0x10, expanded);
            array = expanded;
        }
        SetManagedReference(array, array + 0x20UL + checked((ulong)count * 8UL), value);
        Write(list + 0x18, BitConverter.GetBytes(count + 1));
        Write(list + 0x1C, BitConverter.GetBytes(ReadInt32(list + 0x1C) + 1));
    }

    private void SetManagedReference(ulong owner, ulong fieldAddress, ulong value)
    {
        Call(_gcWbarrierSetFieldRva, owner, fieldAddress, value);
        if (ReadUInt64(fieldAddress) != value)
            throw new InvalidOperationException("游戏运行时拒绝保存新建资源引用。");
    }

    private bool AffixHasShard(ulong affix)
    {
        if (ReadByte(affix + 0x4C) != 0) return false;
        var special = ReadInt32(affix + 0x50);
        if (special is not (0 or 3)) return false;
        var canRollOn = ReadUInt64(affix + 0x78);
        if (canRollOn == 0) return false;
        var count = ReadInt32(canRollOn + 0x18);
        if (count != 1) return true;
        var array = ReadUInt64(canRollOn + 0x10);
        return array != 0 && ReadInt32(array + 0x20) != 0x29;
    }

    private ulong GetMaterialContainers()
    {
        var materials = ReadUInt64(GetActiveMaterialStash() + 0x90);
        return materials != 0 ? materials : throw new InvalidOperationException("材料容器尚未加载。");
    }

    private ulong GetActiveMaterialStash()
    {
        var manager = RequirePlayerFinderRoot("localItemContainersManager", "物品容器");
        var stashHolder = ReadUInt64(manager + 0xA8);
        if (stashHolder == 0) throw new InvalidOperationException("材料存储尚未加载。");
        var stashContainers = ReadUInt64(stashHolder + 0x18);
        var currentIndex = ReadInt32(stashHolder + 0x20);
        if (stashContainers == 0 || currentIndex < 0 || currentIndex >= ReadInt32(stashContainers + 0x18))
            throw new InvalidDataException("当前材料存储索引无效。");
        var activeStash = ReadUInt64(stashContainers + 0x20UL + checked((ulong)currentIndex * 8UL));
        return activeStash != 0 ? activeStash : throw new InvalidOperationException("当前材料存储尚未加载。");
    }

    private ulong ResolveStaticFieldObject(string @namespace, string className, string fieldName)
    {
        var field = ResolveStaticFieldInfo(@namespace, className, fieldName);
        var value = Allocate(8);
        try
        {
            Write(unchecked((ulong)value.ToInt64()), new byte[8]);
            Call(_fieldStaticGetValueRva, field, unchecked((ulong)value.ToInt64()));
            return ReadUInt64(unchecked((ulong)value.ToInt64()));
        }
        finally
        {
            VirtualFreeEx(_handle, value, 0, MemRelease);
        }
    }

    private ulong ResolveStaticFieldInfo(string @namespace, string className, string fieldName) =>
        ResolveFieldInfo(ResolveClass(@namespace, className), fieldName, $"{@namespace}.{className}");

    private void ValidateInstanceField(string className, string fieldName)
    {
        var klass = ResolveClass(string.Empty, className);
        _ = ResolveFieldOffset(klass, fieldName, className);
    }

    private ulong ResolveFieldInfo(ulong klass, string fieldName, string ownerDescription)
    {
        using var fieldText = AllocateUtf8(fieldName);
        var field = Call(_classGetFieldFromNameRva, klass, unchecked((ulong)fieldText.Address.ToInt64()));
        return field != 0
            ? field
            : throw new InvalidOperationException($"无法定位 Last Epoch 字段 {ownerDescription}.{fieldName}。");
    }

    private ulong ResolveFieldOffset(ulong klass, string fieldName, string ownerDescription)
    {
        var cacheKey = $"{klass:X16}|{fieldName}";
        if (_fieldOffsets.TryGetValue(cacheKey, out var cached)) return cached;
        var field = ResolveFieldInfo(klass, fieldName, ownerDescription);
        var offset = Call(_fieldGetOffsetRva, field);
        if (offset is < 0x10 or > 0x10000)
            throw new InvalidDataException($"Last Epoch 字段 {ownerDescription}.{fieldName} 的偏移超出安全范围。");
        _fieldOffsets[cacheKey] = offset;
        return offset;
    }

    private ulong ObjectFieldAddress(ulong instance, string fieldName)
    {
        if (instance == 0) throw new InvalidOperationException($"读取字段 {fieldName} 时对象尚未加载。");
        var klass = ReadUInt64(instance);
        if (klass == 0) throw new InvalidDataException($"读取字段 {fieldName} 时对象类型无效。");
        return checked(instance + ResolveFieldOffset(klass, fieldName, $"0x{klass:X}"));
    }

    private ulong ReadObjectReferenceField(ulong instance, string fieldName) =>
        ReadUInt64(ObjectFieldAddress(instance, fieldName));

    private ulong ResolveNamedMethodRva(
        string @namespace,
        string className,
        string methodName,
        int parameterCount)
    {
        var cacheKey = $"{@namespace}|{className}|{methodName}|{parameterCount}";
        if (_methods.TryGetValue(cacheKey, out var cached)) return cached;
        var methodInfo = ResolveNamedMethodInfo(@namespace, className, methodName, parameterCount);
        var methodPointer = ReadUInt64(methodInfo);
        if (methodPointer < _moduleBase || methodPointer >= _moduleEnd)
            throw new InvalidDataException($"Last Epoch 方法 {className}.{methodName}/{parameterCount} 不在 GameAssembly 安全范围内。");
        var rva = methodPointer - _moduleBase;
        _methods[cacheKey] = rva;
        return rva;
    }

    private ulong ResolveNamedMethodInfo(
        string @namespace,
        string className,
        string methodName,
        int parameterCount)
    {
        var cacheKey = $"{@namespace}|{className}|{methodName}|{parameterCount}";
        if (_methodInfos.TryGetValue(cacheKey, out var cached)) return cached;
        var klass = ResolveClass(@namespace, className);
        var methodInfo = ResolveMethodInfo(klass, methodName, parameterCount, className);
        _methodInfos[cacheKey] = methodInfo;
        return methodInfo;
    }

    private ulong ResolveMethodInfo(
        ulong klass,
        string methodName,
        int parameterCount,
        string ownerDescription)
    {
        using var methodText = AllocateUtf8(methodName);
        var methodInfo = Call(
            _classGetMethodFromNameRva,
            klass,
            unchecked((ulong)methodText.Address.ToInt64()),
            unchecked((ulong)parameterCount));
        if (methodInfo == 0)
            throw new InvalidOperationException($"无法定位 Last Epoch 方法 {ownerDescription}.{methodName}/{parameterCount}。");
        return methodInfo;
    }

    private ulong ResolveNestedClass(
        ulong declaringClass,
        Func<string, bool> predicate,
        string description)
    {
        var iterator = Allocate(8);
        try
        {
            Write(unchecked((ulong)iterator.ToInt64()), new byte[8]);
            var matches = new List<ulong>();
            for (var index = 0; index < 256; index++)
            {
                var nested = Call(
                    _classGetNestedTypesRva,
                    declaringClass,
                    unchecked((ulong)iterator.ToInt64()));
                if (nested == 0) break;
                var namePointer = Call(_classGetNameRva, nested);
                if (namePointer != 0 && predicate(ReadUtf8String(namePointer))) matches.Add(nested);
            }
            if (matches.Count != 1)
                throw new InvalidDataException($"无法唯一定位 {description}（候选 {matches.Count}）。");
            return matches[0];
        }
        finally
        {
            VirtualFreeEx(_handle, iterator, 0, MemRelease);
        }
    }

    private ulong ResolveClass(string @namespace, string className)
    {
        var cacheKey = $"{@namespace}|{className}";
        if (_classes.TryGetValue(cacheKey, out var cached)) return cached;
        using var namespaceText = AllocateUtf8(@namespace);
        using var classText = AllocateUtf8(className);
        var countAddress = Allocate(8);
        try
        {
            Write(unchecked((ulong)countAddress.ToInt64()), new byte[8]);
            var domain = Call(_domainGet - _moduleBase);
            var assemblies = Call(_domainGetAssembliesRva, domain, unchecked((ulong)countAddress.ToInt64()));
            var count = ReadUInt64(unchecked((ulong)countAddress.ToInt64()));
            if (assemblies == 0 || count is 0 or > 1024)
                throw new InvalidOperationException("无法枚举 Last Epoch IL2CPP 程序集。");
            for (ulong index = 0; index < count; index++)
            {
                var assembly = ReadUInt64(assemblies + index * 8);
                var image = assembly == 0 ? 0 : Call(_assemblyGetImageRva, assembly);
                var klass = image == 0 ? 0 : Call(
                    _classFromNameRva,
                    image,
                    unchecked((ulong)namespaceText.Address.ToInt64()),
                    unchecked((ulong)classText.Address.ToInt64()));
                if (klass == 0) continue;
                _classes[cacheKey] = klass;
                return klass;
            }
        }
        finally
        {
            VirtualFreeEx(_handle, countAddress, 0, MemRelease);
        }
        throw new InvalidOperationException($"无法定位 Last Epoch 类型 {className}。");
    }

    private RemoteAllocation AllocateUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        var address = Allocate(bytes.Length);
        Write(unchecked((ulong)address.ToInt64()), bytes);
        return new RemoteAllocation(_handle, address);
    }

    private string ReadItemName(ulong data, string fallback)
    {
        var itemType = ReadByte(ObjectFieldAddress(data, "itemType"));
        var subType = ReadUInt16(ObjectFieldAddress(data, "subType"));
        var uniqueId = ReadUInt16(ObjectFieldAddress(data, "uniqueID"));
        var name = uniqueId == 0 ? string.Empty : ReadLocalizedValue($"Unique_Name_{uniqueId}");
        if (string.IsNullOrWhiteSpace(name))
            name = ReadLocalizedValue($"Item_SubType_Name_{itemType}_{subType}");
        if (string.IsNullOrWhiteSpace(name))
        {
            var itemList = Call(ResolveNamedMethodRva(string.Empty, "ItemList", "get", 0));
            if (itemList != 0)
            {
                name = ReadManagedString(Call(
                    ResolveNamedMethodRva(string.Empty, "ItemList", "GetItemName", 2),
                    itemList,
                    itemType,
                    subType));
            }
        }
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private string ReadLocalizedValue(string key)
    {
        try
        {
            _localizedValues ??= ReadLocalizationTable();
            return _localizedValues.TryGetValue(key, out var value) ? value : string.Empty;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            // Localization is a presentation enhancement. A changed dictionary layout must not
            // disable the stable numeric resource identity or its editor.
        }
        return string.Empty;
    }

    private IReadOnlyDictionary<string, string> ReadLocalizationTable()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dictionary = ResolveStaticFieldObject(string.Empty, "Localization", "_tableCache");
        if (dictionary == 0) return result;
        var entries = ReadUInt64(dictionary + 0x18);
        var count = ReadInt32(dictionary + 0x20);
        if (entries == 0 || count is < 0 or > 200_000) return result;
        var capacity = ReadInt32(entries + 0x18);
        if (capacity < count || capacity > 500_000) return result;
        const ulong stride = 24;
        for (var index = 0; index < count; index++)
        {
            var entry = entries + 0x20UL + checked((ulong)index * stride);
            var storedKey = ReadManagedString(ReadUInt64(entry + 0x08));
            if (!IsRelevantLocalizationKey(storedKey)) continue;
            var value = ReadManagedString(ReadUInt64(entry + 0x10));
            if (!string.IsNullOrWhiteSpace(value)) result[storedKey] = value;
        }
        return result;
    }

    private static bool IsRelevantLocalizationKey(string key) =>
        key.StartsWith("Item_SubType_Name_", StringComparison.Ordinal) ||
        key.StartsWith("Unique_Name_", StringComparison.Ordinal) ||
        key.StartsWith("Item_Affix_", StringComparison.Ordinal) && key.EndsWith("_DisplayName", StringComparison.Ordinal);

    private (ulong Entry, ulong Data) GetForgeMainItem()
    {
        var manager = RequirePlayerFinderRoot("localItemContainersManager", "物品容器");
        var crafting = ReadObjectReferenceField(manager, "crafting");
        if (crafting == 0) throw new InvalidOperationException("熔炉容器尚未加载。");
        var main = ReadObjectReferenceField(crafting, "main");
        if (main == 0) throw new InvalidOperationException("熔炉主槽尚未加载。");
        var entry = ReadObjectReferenceField(main, "content");
        var data = entry == 0 ? 0 : ReadObjectReferenceField(entry, "data");
        return (entry, data);
    }

    private ulong GetTracker() => RequirePlayerFinderRoot("localPlayerDataTracker", "人物存档跟踪器");
    private ulong GetCharacterData()
    {
        var data = ReadObjectReferenceField(GetTracker(), "charData");
        return data != 0 ? data : throw new InvalidOperationException("当前人物存档对象尚未加载。");
    }

    private ulong RequirePlayerFinderRoot(string fieldName, string description)
    {
        var value = ResolveStaticFieldObject(string.Empty, "PlayerFinder", fieldName);
        return value != 0 ? value : throw new InvalidOperationException($"{description}尚未加载，请进入角色存档后刷新。");
    }

    private void MarkCharacterDirty()
    {
        var characterData = GetCharacterData();
        MarkEntityDirty(characterData, "人物存档");
    }

    private void MarkEntityDirty(ulong entity, string description)
    {
        var flags = ReadUInt64(entity + 0x38);
        if (flags == 0) throw new InvalidOperationException($"{description}状态尚未加载，无法请求游戏自动保存。");

        // EntityFlags.SetDirty(true) ultimately updates only these two fields. Writing them directly
        // avoids calling serialization or Unity APIs from an injected thread; the game's own
        // DataStoreCache update loop observes the flag and performs the save on its normal path.
        var currentFlags = ReadInt32(flags + 0x10);
        if ((currentFlags & ~7) != 0)
            throw new InvalidDataException($"{description}状态结构与受支持构建不一致，已拒绝请求自动保存。");
        var previousDateData = ReadUInt64(flags + 0x20);
        var previousTicks = previousDateData & 0x3FFF_FFFF_FFFF_FFFFUL;
        if (previousTicks > unchecked((ulong)DateTime.MaxValue.Ticks))
            throw new InvalidDataException($"{description}脏标记时间结构无效，已拒绝请求自动保存。");

        var utcDateData = unchecked((ulong)DateTime.UtcNow.Ticks) | 0x4000_0000_0000_0000UL;
        Write(flags + 0x20, BitConverter.GetBytes(utcDateData));
        Write(flags + 0x10, BitConverter.GetBytes(currentFlags | 1));
        if ((ReadInt32(flags + 0x10) & 1) == 0 || ReadUInt64(flags + 0x20) != utcDateData)
            throw new InvalidOperationException($"游戏没有接受{description}脏标记。");
    }

    private IReadOnlyList<ulong> ReadReferenceList(ulong list, string description)
    {
        if (list == 0) return [];
        var array = ReadUInt64(list + 0x10);
        var count = ReadInt32(list + 0x18);
        if (array == 0 || count is < 0 or > 100_000)
            throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
        var result = new List<ulong>(count);
        for (var index = 0; index < count; index++)
        {
            var value = ReadUInt64(array + 0x20UL + (ulong)index * 8UL);
            if (value != 0) result.Add(value);
        }
        return result;
    }

    private IReadOnlyList<ulong> ReadReferenceListWithNulls(ulong list, string description)
    {
        if (list == 0) return [];
        var array = ReadUInt64(list + 0x10);
        var count = ReadInt32(list + 0x18);
        if (array == 0 || count is < 0 or > 100_000)
            throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
        var result = new ulong[count];
        for (var index = 0; index < count; index++)
            result[index] = ReadUInt64(array + 0x20UL + checked((ulong)index * 8UL));
        return result;
    }

    private IReadOnlyList<float> ReadFloatList(ulong list, string description)
    {
        if (list == 0) return [];
        var array = ReadUInt64(list + 0x10);
        var count = ReadInt32(list + 0x18);
        if (array == 0 || count is < 0 or > 1024)
            throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
        var result = new float[count];
        for (var index = 0; index < count; index++)
            result[index] = ReadSingle(array + 0x20UL + checked((ulong)index * 4UL));
        return result;
    }

    private IReadOnlyList<ulong> ReadReferenceArray(ulong array, string description)
    {
        if (array == 0) return [];
        var count = ReadInt32(array + 0x18);
        if (count is < 0 or > 100_000)
            throw new InvalidDataException($"{description}结构无效（记录数 {count}）。");
        var result = new List<ulong>(count);
        for (var index = 0; index < count; index++)
        {
            var value = ReadUInt64(array + 0x20UL + checked((ulong)index * 8UL));
            if (value != 0) result.Add(value);
        }
        return result;
    }

    private string ReadManagedString(ulong address)
    {
        if (address == 0) return string.Empty;
        var length = ReadInt32(address + 0x10);
        if (length is < 0 or > 4096) return string.Empty;
        return Encoding.Unicode.GetString(Read(address + 0x14, length * 2));
    }

    private string ReadUtf8String(ulong address)
    {
        if (address == 0) return string.Empty;
        var bytes = Read(address, 256);
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private ulong Call(ulong rva, ulong arg1 = 0, ulong arg2 = 0, ulong arg3 = 0, ulong arg4 = 0)
    {
        var result = Allocate(8);
        var codeAddress = Allocate(256);
        var remoteThreadMayStillRun = false;
        try
        {
            var code = new Emitter();
            code.Emit(0x53, 0x48, 0x83, 0xEC, 0x30);
            code.MovRax(_domainGet); code.CallRax();
            code.Emit(0x48, 0x89, 0xC1);
            code.MovRax(_threadAttach); code.CallRax();
            code.Emit(0x48, 0x89, 0xC3);
            code.MovRcx(arg1); code.MovRdx(arg2); code.MovR8(arg3); code.MovR9(arg4);
            code.Emit(0x48, 0xC7, 0x44, 0x24, 0x20, 0, 0, 0, 0);
            code.Emit(0x48, 0xC7, 0x44, 0x24, 0x28, 0, 0, 0, 0);
            code.MovRax(_moduleBase + rva); code.CallRax();
            code.MovRdx(unchecked((ulong)result.ToInt64())); code.Emit(0x48, 0x89, 0x02);
            code.Emit(0x48, 0x89, 0xD9);
            code.MovRax(_threadDetach); code.CallRax();
            code.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x30, 0x5B, 0xC3);
            Write(unchecked((ulong)codeAddress.ToInt64()), code.ToArray());
            remoteThreadMayStillRun = !RunRemoteThread(codeAddress);
            if (remoteThreadMayStillRun)
                throw new TimeoutException("Last Epoch 没有在五秒内确认完成游戏原生调用；为保护游戏，已保留该次临时内存。请重新进入存档后再试。");
            return ReadUInt64(unchecked((ulong)result.ToInt64()));
        }
        finally
        {
            // A timed-out remote thread may still execute this buffer. Freeing it here turns a
            // recoverable timeout into an access violation in the target game. A failed call may
            // leak a few hundred bytes until the process exits, which is the safer failure mode.
            if (!remoteThreadMayStillRun)
            {
                VirtualFreeEx(_handle, result, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
        }
    }

    private float CallFloat(ulong rva, ulong arg1 = 0, ulong arg2 = 0, ulong arg3 = 0, ulong arg4 = 0)
    {
        var result = Allocate(4);
        var codeAddress = Allocate(256);
        var remoteThreadMayStillRun = false;
        try
        {
            var code = new Emitter();
            code.Emit(0x53, 0x48, 0x83, 0xEC, 0x30);
            code.MovRax(_domainGet); code.CallRax();
            code.Emit(0x48, 0x89, 0xC1);
            code.MovRax(_threadAttach); code.CallRax();
            code.Emit(0x48, 0x89, 0xC3);
            code.MovRcx(arg1); code.MovRdx(arg2); code.MovR8(arg3); code.MovR9(arg4);
            code.Emit(0x48, 0xC7, 0x44, 0x24, 0x20, 0, 0, 0, 0);
            code.Emit(0x48, 0xC7, 0x44, 0x24, 0x28, 0, 0, 0, 0);
            code.MovRax(_moduleBase + rva); code.CallRax();
            code.Emit(0x66, 0x0F, 0x7E, 0xC0);
            code.MovRdx(unchecked((ulong)result.ToInt64())); code.Emit(0x89, 0x02);
            code.Emit(0x48, 0x89, 0xD9);
            code.MovRax(_threadDetach); code.CallRax();
            code.Emit(0x33, 0xC0, 0x48, 0x83, 0xC4, 0x30, 0x5B, 0xC3);
            Write(unchecked((ulong)codeAddress.ToInt64()), code.ToArray());
            remoteThreadMayStillRun = !RunRemoteThread(codeAddress);
            if (remoteThreadMayStillRun)
                throw new TimeoutException("Last Epoch 没有在五秒内确认完成游戏原生调用；为保护游戏，已保留该次临时内存。请重新进入存档后再试。");
            return ReadSingle(unchecked((ulong)result.ToInt64()));
        }
        finally
        {
            if (!remoteThreadMayStillRun)
            {
                VirtualFreeEx(_handle, result, 0, MemRelease);
                VirtualFreeEx(_handle, codeAddress, 0, MemRelease);
            }
        }
    }

    private bool RunRemoteThread(IntPtr codeAddress)
    {
        var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, codeAddress, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Last Epoch IL2CPP 调用线程。");
        try
        {
            var wait = WaitForSingleObject(thread, 5000);
            // Any non-signalled result means the target thread may still be executing. The caller
            // must retain its remote code/result buffers in that case.
            return wait == WaitObject0;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    private IntPtr Allocate(int size)
    {
        var address = VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, MemCommitReserve, PageExecuteReadWrite);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配 Last Epoch 临时内存。");
        return address;
    }

    private byte ReadByte(ulong address) => Read(address, 1)[0];
    private ushort ReadUInt16(ulong address) => BitConverter.ToUInt16(Read(address, 2));
    private uint ReadUInt32(ulong address) => BitConverter.ToUInt32(Read(address, 4));
    private int ReadInt32(ulong address) => BitConverter.ToInt32(Read(address, 4));
    private long ReadInt64(ulong address) => BitConverter.ToInt64(Read(address, 8));
    private float ReadSingle(ulong address) => BitConverter.ToSingle(Read(address, 4));
    private ulong ReadUInt64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    private byte[] Read(ulong address, int count) => ReadBytes(_handle, address, count);
    private void Write(ulong address, byte[] bytes)
    {
        if (!WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (nuint)bytes.Length, out var written) ||
            written != (nuint)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"写入 Last Epoch 内存失败：0x{address:X}。");
    }

    private void WriteExecutable(ulong address, byte[] bytes)
    {
        if (!VirtualProtectEx(
                _handle,
                (IntPtr)(long)address,
                (nuint)bytes.Length,
                PageExecuteReadWrite,
                out var originalProtection))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法解锁 Last Epoch 代码页：0x{address:X}。");
        try
        {
            Write(address, bytes);
            if (!FlushInstructionCache(_handle, (IntPtr)(long)address, (nuint)bytes.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法刷新 Last Epoch 指令缓存。");
        }
        finally
        {
            _ = VirtualProtectEx(
                _handle,
                (IntPtr)(long)address,
                (nuint)bytes.Length,
                originalProtection,
                out _);
        }
    }

    private static byte[] ReadBytes(SafeFileHandle handle, ulong address, int count)
    {
        var bytes = new byte[count];
        if (!ReadProcessMemory(handle, (IntPtr)(long)address, bytes, (nuint)count, out var read) ||
            read != (nuint)count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"读取 Last Epoch 内存失败：0x{address:X}。");
        return bytes;
    }

    private static ushort ReadUInt16(SafeFileHandle handle, ulong address) =>
        BitConverter.ToUInt16(ReadBytes(handle, address, 2));
    private static ulong ReadUInt64(SafeFileHandle handle, ulong address) =>
        BitConverter.ToUInt64(ReadBytes(handle, address, 8));

    public void Dispose()
    {
        _handle.Dispose();
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    private sealed class Emitter
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Emit(params byte[] bytes) => _bytes.AddRange(bytes);
        public void MovRax(ulong value) { Emit(0x48, 0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovRcx(ulong value) { Emit(0x48, 0xB9); Emit(BitConverter.GetBytes(value)); }
        public void MovRdx(ulong value) { Emit(0x48, 0xBA); Emit(BitConverter.GetBytes(value)); }
        public void MovR8(ulong value) { Emit(0x49, 0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovR9(ulong value) { Emit(0x49, 0xB9); Emit(BitConverter.GetBytes(value)); }
        public void MovEax(uint value) { Emit(0xB8); Emit(BitConverter.GetBytes(value)); }
        public void MovEdx(uint value) { Emit(0xBA); Emit(BitConverter.GetBytes(value)); }
        public void MovR9d(uint value) { Emit(0x41, 0xB9); Emit(BitConverter.GetBytes(value)); }
        public void MovdXmm1Eax() => Emit(0x66, 0x0F, 0x6E, 0xC8);
        public void MovdXmm2Eax() => Emit(0x66, 0x0F, 0x6E, 0xD0);
        public void MovRspOffsetRax(byte offset) => Emit(0x48, 0x89, 0x44, 0x24, offset);
        public void MovDwordRspOffset(byte offset, uint value)
        {
            Emit(0xC7, 0x44, 0x24, offset);
            Emit(BitConverter.GetBytes(value));
        }
        public int EmitNearConditionalJump(byte condition)
        {
            Emit(0x0F, condition);
            var displacement = Position;
            Emit(0, 0, 0, 0);
            return displacement;
        }
        public int EmitNearJump()
        {
            Emit(0xE9);
            var displacement = Position;
            Emit(0, 0, 0, 0);
            return displacement;
        }
        public void PatchNearJump(int displacementOffset, int targetOffset)
        {
            var displacement = checked(targetOffset - (displacementOffset + 4));
            var bytes = BitConverter.GetBytes(displacement);
            for (var index = 0; index < bytes.Length; index++) _bytes[displacementOffset + index] = bytes[index];
        }
        public void CallRax() => Emit(0xFF, 0xD0);
        public byte[] ToArray() => _bytes.ToArray();
    }

    private sealed class RemoteAllocation(SafeFileHandle handle, IntPtr address) : IDisposable
    {
        public IntPtr Address { get; } = address;
        public void Dispose() => VirtualFreeEx(handle, Address, 0, MemRelease);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(
        SafeFileHandle process,
        IntPtr address,
        nuint size,
        uint newProtection,
        out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(SafeFileHandle process, IntPtr address, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeFileHandle process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(SafeFileHandle process, IntPtr address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(SafeFileHandle process, IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(SafeFileHandle process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(SafeFileHandle process, IntPtr attributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeFileHandle process, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);
}

internal static class PortableExportResolver
{
    public static ExportMap Read(string path) => Parse(File.ReadAllBytes(path));

    private static ExportMap Parse(byte[] bytes)
    {
        int I32(int offset) => BitConverter.ToInt32(bytes, offset);
        uint U32(int offset) => BitConverter.ToUInt32(bytes, offset);
        ushort U16(int offset) => BitConverter.ToUInt16(bytes, offset);
        var pe = I32(0x3C);
        if (U32(pe) != 0x00004550) throw new InvalidDataException("GameAssembly.dll 不是有效 PE 文件。");
        var sections = U16(pe + 6);
        var optionalSize = U16(pe + 20);
        var optional = pe + 24;
        var directory = U16(optional) switch
        {
            0x20B => optional + 112,
            0x10B => optional + 96,
            _ => throw new InvalidDataException("GameAssembly.dll PE 格式不受支持。")
        };
        var exportRva = U32(directory);
        var sectionOffset = optional + optionalSize;
        var maps = new List<(uint Va, uint Size, uint Raw)>();
        for (var index = 0; index < sections; index++)
        {
            var row = sectionOffset + index * 40;
            maps.Add((U32(row + 12), Math.Max(U32(row + 8), U32(row + 16)), U32(row + 20)));
        }
        int Offset(uint rva)
        {
            var section = maps.FirstOrDefault(item => rva >= item.Va && rva < item.Va + item.Size);
            if (section.Size == 0) throw new InvalidDataException($"无法映射 PE RVA 0x{rva:X}。");
            return checked((int)(section.Raw + rva - section.Va));
        }
        string Ascii(uint rva)
        {
            var start = Offset(rva);
            var end = start;
            while (end < bytes.Length && bytes[end] != 0) end++;
            return Encoding.ASCII.GetString(bytes, start, end - start);
        }
        var export = Offset(exportRva);
        var count = U32(export + 24);
        var functions = U32(export + 28);
        var names = U32(export + 32);
        var ordinals = U32(export + 36);
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (uint index = 0; index < count; index++)
        {
            var name = Ascii(U32(Offset(names) + checked((int)index * 4)));
            var ordinal = U16(Offset(ordinals) + checked((int)index * 2));
            result[name] = U32(Offset(functions) + ordinal * 4);
        }
        return new ExportMap(result);
    }

    internal sealed class ExportMap(IReadOnlyDictionary<string, uint> values)
    {
        public uint GetRva(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"GameAssembly.dll 缺少导出 {name}。");
    }
}
