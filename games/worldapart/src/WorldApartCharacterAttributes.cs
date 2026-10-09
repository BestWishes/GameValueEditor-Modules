using System.Globalization;
using System.IO;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.WorldApart;

public sealed partial class WorldApartGameAdapter
{
    private static readonly IReadOnlyDictionary<int, string> BasicAttributeNames = new Dictionary<int, string>
    {
        [5] = "幸运",
        [6] = "魅力",
        [7] = "移动速度",
        [8] = "寿命"
    };

    private static readonly IReadOnlyDictionary<int, string> CombatAttributeNames = new Dictionary<int, string>
    {
        [1] = "当前生命",
        [2] = "攻击",
        [3] = "防御",
        [4] = "生命上限",
        [11] = "生命百分比",
        [12] = "攻击百分比",
        [13] = "防御百分比",
        [21] = "速度",
        [22] = "灵力上限",
        [23] = "韧性",
        [24] = "韧性上限",
        [25] = "灵力",
        [26] = "共鸣",
        [27] = "共鸣上限",
        [28] = "护盾",
        [51] = "金系伤害加成",
        [52] = "木系伤害加成",
        [53] = "水系伤害加成",
        [54] = "火系伤害加成",
        [55] = "土系伤害加成",
        [101] = "伤害加成",
        [102] = "伤害减免",
        [103] = "吸血效率",
        [104] = "治疗效率",
        [108] = "破韧效率",
        [111] = "暴击率",
        [112] = "暴击伤害",
        [113] = "暴击抵抗",
        [114] = "暴伤减免",
        [115] = "命中率",
        [116] = "闪避率",
        [117] = "无视防御",
        [118] = "无视防御抵抗",
        [119] = "追击概率",
        [120] = "反击概率",
        [121] = "反击伤害加成",
        [122] = "反击伤害抵抗",
        [200] = "精力",
        [201] = "精力上限"
    };

    private static readonly IReadOnlyDictionary<int, string> SpiritRootNames = new Dictionary<int, string>
    {
        [1] = "金灵根",
        [2] = "木灵根",
        [3] = "水灵根",
        [4] = "火灵根",
        [5] = "土灵根"
    };

    public bool SupportsCharacterAttributes(GameProcessContext process) =>
        GameValueEditor.Modules.Runtime.Il2CppRuntimeResolver.IsNamedGame(process, "WorldApart", "不问凡尘");

    public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process)
    {
        if (!SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前 WorldApart 构建尚未支持人物属性编辑。");
        lock (WriteGate)
        {
            using var session = new Session(process);
            return [session.ReadCharacter()];
        }
    }

    public AdapterCharacterItem WriteCharacterAttribute(
        GameProcessContext process,
        string characterId,
        string attributeKey,
        int targetValue)
    {
        if (targetValue < 0) throw new InvalidOperationException("人物属性不能小于 0。");
        if (!string.Equals(characterId, "player", StringComparison.Ordinal))
            throw new InvalidOperationException($"当前 WorldApart 模块不支持人物实体“{characterId}”。");
        if (!SupportsCharacterAttributes(process))
            throw new InvalidOperationException("当前 WorldApart 构建尚未支持人物属性编辑。");

        lock (WriteGate)
        {
            using var session = new Session(process);
            return session.WriteCharacterAttribute(attributeKey, targetValue);
        }
    }

    private sealed partial class Session
    {
        public AdapterCharacterItem ReadCharacter()
        {
            RefreshRoots();
            return ReadCharacterSnapshot().Item;
        }

        public AdapterCharacterItem WriteCharacterAttribute(string attributeKey, int targetValue)
        {
            RefreshRoots();
            var before = ReadCharacterSnapshot();
            if (!before.Locations.TryGetValue(attributeKey, out var location))
                throw new InvalidOperationException($"当前存档没有找到可修改属性“{attributeKey}”。");

            MainThreadRequest request;
            switch (location.Kind)
            {
                case CharacterAttributeKind.DirectInt:
                    request = new MainThreadRequest(
                        [new MemoryWrite(location.ValueAddress, targetValue)],
                        MainThreadOperation.None,
                        location.Owner,
                        location.AttributeId,
                        targetValue);
                    break;
                case CharacterAttributeKind.Interact:
                    request = new MainThreadRequest(
                        [],
                        MainThreadOperation.SetInteractAttribute,
                        _player,
                        location.AttributeId,
                        targetValue);
                    break;
                case CharacterAttributeKind.CombatBase:
                    request = new MainThreadRequest(
                        [],
                        MainThreadOperation.SetCombatBaseAttribute,
                        location.Owner,
                        location.AttributeId,
                        targetValue);
                    break;
                case CharacterAttributeKind.CombatRole:
                    request = new MainThreadRequest(
                        [new MemoryWrite(location.ValueAddress, BitConverter.SingleToInt32Bits(targetValue))],
                        MainThreadOperation.None,
                        location.Owner,
                        location.AttributeId,
                        targetValue);
                    break;
                case CharacterAttributeKind.SpiritRoot:
                    var spiritRootDictionary = _runtime.Reference(location.Owner, "<SpiritRootPoints>k__BackingField", "System.Collections.Generic.Dictionary<System.Int32,System.Int32>");
                    request = new MainThreadRequest(
                        [],
                        MainThreadOperation.SetSpiritRoot,
                        location.Owner,
                        location.AttributeId,
                        targetValue,
                        ResolveMethodFromObject(spiritRootDictionary, "set_Item", 2),
                        spiritRootDictionary);
                    break;
                default:
                    throw new InvalidOperationException($"不支持的人物属性写入方式：{location.Kind}。");
            }

            ExecuteOnMainThread(request);
            Thread.Sleep(150);
            RefreshRoots();
            var updated = ReadCharacterSnapshot().Item;
            var attribute = updated.Attributes.SingleOrDefault(candidate =>
                string.Equals(candidate.Key, attributeKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("写入后属性不再存在，请刷新后重试。");
            if (attribute.RawValue != targetValue)
                throw new InvalidOperationException(
                    $"人物属性回读失败：预期 {targetValue}，实际 {attribute.RawValue}。");
            return updated;
        }

        private CharacterSnapshot ReadCharacterSnapshot()
        {
            var attributes = new List<AdapterCharacterAttribute>();
            var locations = new Dictionary<string, CharacterAttributeLocation>(StringComparer.Ordinal);
            var playerName = ReadManagedString(_runtime.Reference(_player, "playerName", "System.String"));
            if (string.IsNullOrWhiteSpace(playerName)) playerName = "当前玩家";

            var talent = _runtime.Reference(_player, "talentPath", "Game.Model.Player.Components.TalentPathModel");
            if (talent != 0)
            {
                AddDirectAttribute(attributes, locations,
                    "talent.path-points", "资源 · 道途点", _runtime.Address(talent, "<PathPoints>k__BackingField", "System.Int32"), talent, ReadInt32(_runtime.Address(talent, "<PathPoints>k__BackingField", "System.Int32")));
                AddDirectAttribute(attributes, locations,
                    "talent.spirit-point-remain", "资源 · 灵根点", _runtime.Address(talent, "<SpiritPointRemain>k__BackingField", "System.Int32"), talent, ReadInt32(_runtime.Address(talent, "<SpiritPointRemain>k__BackingField", "System.Int32")));

                var roots = ReadIntDictionary(_runtime.Reference(talent, "<SpiritRootPoints>k__BackingField", "System.Collections.Generic.Dictionary<System.Int32,System.Int32>"), "五行灵根字典");
                foreach (var pair in SpiritRootNames.OrderBy(pair => pair.Key))
                {
                    var value = roots.TryGetValue(pair.Key, out var stored) ? stored.Value : 0;
                    var key = $"spirit-root:{pair.Key}";
                    attributes.Add(new AdapterCharacterAttribute(
                        key, $"五行灵根 · {pair.Value}", value, value, 0));
                    locations[key] = new CharacterAttributeLocation(
                        CharacterAttributeKind.SpiritRoot, stored?.ValueAddress ?? 0, talent, pair.Key);
                }
            }

            var interact = ReadIntDictionary(_runtime.Reference(_player, "interactAttributes", "System.Collections.Generic.Dictionary<LubanDatas.TbInteractAttributeId,System.Int32>"), "探索属性字典");
            var interactNames = ReadAllInteractAttributeNames();
            foreach (var pair in interactNames.OrderBy(pair => pair.Key))
            {
                var value = interact.TryGetValue(pair.Key, out var stored) ? stored.Value : 0;
                var key = $"interact:{pair.Key}";
                attributes.Add(new AdapterCharacterAttribute(
                    key, $"探索属性 · {pair.Value}", value, value, 0));
                locations[key] = new CharacterAttributeLocation(
                    CharacterAttributeKind.Interact, stored?.ValueAddress ?? 0, _player, pair.Key);
            }

            var combat = _runtime.Reference(_player, "combat", "Game.Model.Player.Components.CombatModel");
            var layer = 0;
            if (combat != 0)
            {
                layer = ReadInt32(_runtime.Address(combat, "<CurrentLayerId>k__BackingField", "LubanDatas.TbCultivateLayerId"));
                var baseAttributes = ReadFloatDictionary(_runtime.Reference(combat, "<BaseAttrs>k__BackingField", "System.Collections.Generic.Dictionary<Game.Model.Player.Components.CombatAttrId,System.Single>"), "战斗基础属性字典");
                var growthAttributes = ReadFloatDictionary(_runtime.Reference(combat, "<GrowthAttrs>k__BackingField", "System.Collections.Generic.Dictionary<Game.Model.Player.Components.CombatAttrId,System.Single>"), "战斗成长属性字典");

                AddDirectAttribute(attributes, locations,
                    "cultivation.spirit-qi", "资源 · 灵气", _runtime.Address(combat, "<CultivateReserveExp>k__BackingField", "System.Int32"), combat, ReadInt32(_runtime.Address(combat, "<CultivateReserveExp>k__BackingField", "System.Int32")));
                AddDirectAttribute(attributes, locations,
                    "cultivation.exp", "基础属性 · 修为", _runtime.Address(combat, "_CultivateExp", "System.Int32"), combat, ReadInt32(_runtime.Address(combat, "_CultivateExp", "System.Int32")));
                AddCurrentBasicAttributeIfPresent(attributes, locations, baseAttributes, combat, 200, "精力");
                AddBaseBasicAttributeIfPresent(attributes, locations, baseAttributes, growthAttributes, combat, 201, "精力上限");
                AddCurrentBasicAttributeIfPresent(attributes, locations, baseAttributes, combat, 25, "灵力");
                AddBaseBasicAttributeIfPresent(attributes, locations, baseAttributes, growthAttributes, combat, 22, "灵力上限");

                foreach (var pair in baseAttributes.OrderBy(pair => pair.Key))
                {
                    if (pair.Key is 200 or 201 or 25 or 22) continue;
                    var isBasic = BasicAttributeNames.TryGetValue(pair.Key, out var basicName);
                    var displayName = isBasic
                        ? basicName!
                        : CombatAttributeNames.GetValueOrDefault(pair.Key, $"属性 #{pair.Key}");
                    var prefix = isBasic ? "基础属性" : "战斗属性";
                    var key = $"combat.base:{pair.Key}";
                    var raw = ToDisplayInt(pair.Value.Value);
                    // The game's aggregate getter touches runtime systems that are main-thread-only.
                    // Read the persisted base/growth dictionaries here; UI refresh after a write is
                    // performed by the game's own setter on the main thread.
                    var total = raw;
                    var growthValue = growthAttributes.TryGetValue(pair.Key, out var growthEntry) ? growthEntry.Value : 0f;
                    attributes.Add(new AdapterCharacterAttribute(
                        key, $"{prefix} · {displayName}", raw, total, growthValue));
                    locations[key] = new CharacterAttributeLocation(
                        CharacterAttributeKind.CombatBase, pair.Value.ValueAddress, combat, pair.Key);
                }
            }

            return new CharacterSnapshot(
                new AdapterCharacterItem("player", playerName, layer, attributes),
                locations);
        }

        private static void AddDirectAttribute(
            ICollection<AdapterCharacterAttribute> attributes,
            IDictionary<string, CharacterAttributeLocation> locations,
            string key,
            string displayName,
            ulong valueAddress,
            ulong owner,
            int value)
        {
            attributes.Add(new AdapterCharacterAttribute(key, displayName, value, value, 0));
            locations[key] = new CharacterAttributeLocation(
                CharacterAttributeKind.DirectInt, valueAddress, owner, 0);
        }

        private void AddCurrentBasicAttributeIfPresent(
            ICollection<AdapterCharacterAttribute> attributes,
            IDictionary<string, CharacterAttributeLocation> locations,
            IReadOnlyDictionary<int, DictionaryEntry<float>> baseAttributes,
            ulong combat,
            int attributeId,
            string displayName)
        {
            if (!baseAttributes.ContainsKey(attributeId)) return;
            var key = $"combat.role:{attributeId}";
            var value = ToDisplayInt(baseAttributes[attributeId].Value);
            attributes.Add(new AdapterCharacterAttribute(key, $"基础属性 · {displayName}", value, value, 0));
            locations[key] = new CharacterAttributeLocation(
                CharacterAttributeKind.CombatRole,
                baseAttributes[attributeId].ValueAddress,
                combat,
                attributeId);
        }

        private void AddBaseBasicAttributeIfPresent(
            ICollection<AdapterCharacterAttribute> attributes,
            IDictionary<string, CharacterAttributeLocation> locations,
            IReadOnlyDictionary<int, DictionaryEntry<float>> baseAttributes,
            IReadOnlyDictionary<int, DictionaryEntry<float>> growthAttributes,
            ulong combat,
            int attributeId,
            string displayName)
        {
            if (!baseAttributes.TryGetValue(attributeId, out var entry)) return;
            var key = $"combat.base:{attributeId}";
            var value = ToDisplayInt(entry.Value);
            var growthValue = growthAttributes.TryGetValue(attributeId, out var growthEntry) ? growthEntry.Value : 0f;
            attributes.Add(new AdapterCharacterAttribute(
                key, $"基础属性 · {displayName}", value, value, growthValue));
            locations[key] = new CharacterAttributeLocation(
                CharacterAttributeKind.CombatBase,
                entry.ValueAddress,
                combat,
                attributeId);
        }

        private Dictionary<int, string> ReadAllInteractAttributeNames()
        {
            var result = new Dictionary<int, string>();
            var table = _runtime.Reference(_tables, "<TbInteractAttribute>k__BackingField", "LubanDatas.TbInteractAttribute");
            if (table == 0) return result;
            var configs = ReadReferenceList(_runtime.Reference(table, "_dataList", "System.Collections.Generic.List<LubanDatas.data.InteractAttribute>"), "探索属性配置表");
            foreach (var config in configs)
            {
                var id = _runtime.ReadInt(_runtime.Address(config, "<id>k__BackingField", "LubanDatas.TbInteractAttributeId"));
                var value = CallPointerFunction(_moduleBase + _layout.L10nTextGetValue, _runtime.Address(config, "<name>k__BackingField", "LubanDatas.L10nText"));
                var text = ReadManagedString(value);
                result[id] = string.IsNullOrWhiteSpace(text) ? $"探索属性 #{id}" : text;
            }
            return result;
        }

        private static int ToDisplayInt(float value)
        {
            if (!float.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
                throw new InvalidDataException($"游戏属性值 {value.ToString(CultureInfo.InvariantCulture)} 无法显示为整数。");
            return checked((int)MathF.Round(value, MidpointRounding.AwayFromZero));
        }

        static partial void EmitCharacterOperation(Emitter code, MainThreadRequest request, ulong moduleBase, BuildLayout layout)
        {
            switch (request.Operation)
            {
                case MainThreadOperation.SetInteractAttribute:
                    code.MovRcx(request.Owner);
                    code.MovEdx(request.AttributeId);
                    code.MovR8d(request.TargetValue);
                    code.Emit(0x45, 0x33, 0xC9);
                    code.MovRax(moduleBase + layout.PlayerSetInteractAttributeValue); code.CallRax();
                    break;
                case MainThreadOperation.SetCombatBaseAttribute:
                    EmitCombatValueCall(code, request, moduleBase + layout.CombatSetBaseAttribute);
                    break;
                case MainThreadOperation.SetSpiritRoot:
                    code.MovRcx(request.DictionaryOwner);
                    code.MovEdx(request.AttributeId);
                    code.MovR8d(request.TargetValue);
                    code.MovR9(request.MethodInfo);
                    code.MovRax(request.MethodInfo); code.Emit(0x48, 0x8B, 0x00); code.CallRax();
                    code.MovRcx(request.Owner); code.Emit(0x33, 0xD2);
                    code.MovRax(moduleBase + layout.TalentMarkSpiritRootDirty); code.CallRax();
                    code.MovRcx(request.Owner); code.Emit(0x33, 0xD2);
                    code.MovRax(moduleBase + layout.TalentRebuildSpiritRoot); code.CallRax();
                    break;
            }
        }

        private static void EmitCombatValueCall(Emitter code, MainThreadRequest request, ulong target)
        {
            code.MovRcx(request.Owner);
            code.MovEdx(request.AttributeId);
            code.MovEax(BitConverter.SingleToInt32Bits(request.TargetValue));
            code.Emit(0x66, 0x0F, 0x6E, 0xD0);
            code.Emit(0x45, 0x33, 0xC9);
            code.MovRax(target); code.CallRax();
        }
    }

    private sealed record CharacterSnapshot(
        AdapterCharacterItem Item,
        IReadOnlyDictionary<string, CharacterAttributeLocation> Locations);

    private sealed record CharacterAttributeLocation(
        CharacterAttributeKind Kind,
        ulong ValueAddress,
        ulong Owner,
        int AttributeId);

    private enum CharacterAttributeKind
    {
        DirectInt,
        Interact,
        CombatBase,
        CombatRole,
        SpiritRoot
    }
}
