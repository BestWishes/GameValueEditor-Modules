using System.Globalization;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;

namespace GameValueEditor.Modules.Deskrawl;

public sealed class DeskrawlGameAdapter :
    IInventoryGameAdapter,
    IEntityEditorsGameAdapter,
    ICoordinatedGameEditorPageProvider,
    IGameCompatibilityDiagnosticsProvider,
    IGameVersionMetadataProvider,
    IGameEditorFieldPolicyProvider
{
    internal const string CharacterEditorId = "game.deskrawl.character-attributes";
    internal const string EquipmentEditorId = "game.deskrawl.equipment-generation";
    internal const string DropRulesEditorId = "game.deskrawl.drop-rules";
    internal const string FishingEditorId = "game.deskrawl.fishing-loot";
    internal const string MaterialsEditorId = "game.deskrawl.materials";
    internal const string InventoryItemsEditorId = "game.deskrawl.inventory-items";
    internal const string CharacterEntityId = "current-character";
    internal const string EquipmentEntityId = "generation-rules";
    internal const string DropRulesEntityId = "drop-rules";
    internal const string FishingEntityId = "fishing-rules";
    internal const string StackableEntityId = "stackable-items";
    private static readonly object WriteGate = new();

    private static readonly HashSet<string> SessionFieldKeys =
    [
        "strength", "dexterity", "intelligence",
        "gold-find", "item-find-rate", "hp-potion-find", "material-find", "xp-gain"
    ];

    public string Id => "game.deskrawl";
    public string DisplayName => "Deskrawl（桌面破坏神）专属修改模块";
    public string Description => "人物、装备生成、掉落规则、钓鱼、材料与可堆叠背包物品编辑；持久字段使用游戏自身保存流程。";
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(CharacterEditorId, "人物属性", GameEditorKind.Custom, 200,
            "金币、当前经验、天赋点、独立巅峰点、基础主属性与三维属性。"),
        new(EquipmentEditorId, "装备生成", GameEditorKind.Custom, 240,
            "控制新生成装备的随机词条是否直接采用合法区间最高值。", true),
        new(DropRulesEditorId, "掉落规则", GameEditorKind.Custom, 250,
            "控制怪物每次掉落尝试是否产出装备，以及离线可交易特殊物品的候选权重倍率。", true),
        new(FishingEditorId, "钓鱼", GameEditorKind.Custom, 260,
            "覆盖钓鱼最终成功率；当前构建每次成功固定获得 1 个物品。", true),
        new(MaterialsEditorId, "材料", GameEditorKind.Custom, 300,
            "从游戏完整 ItemData 定义表列出全部材料；名称由游戏中文本地化系统提供。"),
        new(InventoryItemsEditorId, "背包物品", GameEditorKind.Custom, 320,
            "修改材料以外的可堆叠物品总数；新增、消耗与保存都走游戏自身背包路径。")
    ];

    public IGameEditorPage CreateEditorPage(string editorId, GameEditorPageContext context) => editorId switch
    {
        CharacterEditorId => new EntityEditorPage(this, context, CharacterEditorId,
            "金币、经验、天赋点、巅峰点和基础主属性会请求游戏保存；金币发现、物品发现、获得经验值、生命药水发现等按游戏面板百分比显示，并在本次运行覆盖最终属性消费者。"),
        EquipmentEditorId => new DeskrawlEquipmentGenerationPage(this, context),
        DropRulesEditorId => new DeskrawlDropRulesPage(this, context),
        FishingEditorId => new DeskrawlFishingPage(this, context),
        MaterialsEditorId => new InventoryEditorPage(this, context,
            "列出游戏当前构建定义的全部材料，包括数量为 0 的材料；修改通过 Inventory 自身的新增/消耗路径并请求保存。"),
        InventoryItemsEditorId => new InventoryEditorPage(ReadStackableInventory, context,
            "只列出当前拥有且最大堆叠数大于 1 的非材料物品；写入通过游戏背包新增/消耗路径并请求保存。"),
        _ => throw new InvalidOperationException($"桌面破坏神模块没有页面：{editorId}。")
    };

    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint)
    {
        // Fingerprints record analyzed builds; only live, uniquely resolved structure
        // decides compatibility. Every operation also validates its own capability.
        if (!DeskrawlRuntime.IsDeskrawl(process)) return false;
        try
        {
            using var runtime = new DeskrawlRuntime(process);
            runtime.ValidateCore();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadApplicationMetadata();
    }

    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(
        GameProcessContext process,
        GameBuildIdentity fingerprint)
    {
        var result = new List<GameCompatibilityDiagnostic>();
        if (!DeskrawlRuntime.IsDeskrawl(process))
        {
            return [new("游戏身份", GameCompatibilityDiagnosticStatus.Failed, "当前进程不是 Deskrawl（桌面破坏神）。")];
        }

        var knownBuild = DeskrawlBuild.IsKnown(fingerprint);
        result.Add(new("当前构建", knownBuild ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Information,
            knownBuild
                ? "文件指纹命中已分析构建；仍会逐项验证运行时语义结构。"
                : "文件指纹未登记；继续执行本地运行时验证，各功能只在所需结构验证通过后开放。"));

        try
        {
            using var runtime = new DeskrawlRuntime(process);
            AddCapability(result, "IL2CPP 核心", runtime.ValidateCore);
            AddCapability(result, "人物属性", runtime.ValidateCharacter);
            AddCapability(result, "装备词条最高值", runtime.ValidateEquipmentGeneration);
            AddCapability(result, "掉落规则", runtime.ValidateDropRules);
            AddCapability(result, "钓鱼", runtime.ValidateFishing);
            AddCapability(result, "完整材料定义", runtime.ValidateMaterials);
            AddCapability(result, "可堆叠背包物品", runtime.ValidateStackableItems);
            AddCapability(result, "游戏保存路径", runtime.ValidateSave);
        }
        catch (Exception error)
        {
            result.Add(new("运行时连接", GameCompatibilityDiagnosticStatus.Failed, error.Message));
        }
        result.Add(new("功能级降级", GameCompatibilityDiagnosticStatus.Information,
            "人物、装备生成、掉落规则、钓鱼、材料、背包物品和保存结构独立检查；失败路径会安全拒绝操作并保留其余功能。"));
        return result;
    }

    private static void AddCapability(
        ICollection<GameCompatibilityDiagnostic> items,
        string name,
        Action validate)
    {
        try
        {
            validate();
            items.Add(new(name, GameCompatibilityDiagnosticStatus.Passed, "当前运行时语义结构验证通过。"));
        }
        catch (Exception error)
        {
            items.Add(new(name, GameCompatibilityDiagnosticStatus.Warning, error.Message));
        }
    }

    public GameEditorFieldPolicy? GetFieldPolicy(string editorId, string entityId, string fieldId)
    {
        if (string.Equals(editorId, EquipmentEditorId, StringComparison.Ordinal) ||
            string.Equals(editorId, DropRulesEditorId, StringComparison.Ordinal) ||
            string.Equals(editorId, FishingEditorId, StringComparison.Ordinal))
            return new GameEditorFieldPolicy(SessionOnly: true, CanLock: false);
        if (!string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal) ||
            !string.Equals(entityId, CharacterEntityId, StringComparison.Ordinal)) return null;
        return SessionFieldKeys.Contains(fieldId)
            ? new GameEditorFieldPolicy(SessionOnly: true, CanLock: false)
            : new GameEditorFieldPolicy(SessionOnly: false, CanLock: false);
    }

    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey)
    {
        if (ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var attributeKey) &&
            string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal))
        {
            var entity = ReadCharacter(process);
            if (!string.Equals(entity.EntityId, entityId, StringComparison.Ordinal))
                throw new InvalidOperationException("当前人物身份已变化，请刷新后重试。");
            var field = entity.Fields.Single(item => string.Equals(item.Key, attributeKey, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, field.ValueDisplay, field.Status);
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, InventoryItemsEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, StackableEntityId, StringComparison.Ordinal))
        {
            using var runtime = new DeskrawlRuntime(process);
            var item = runtime.ReadStackableItems(includeZero: true).SingleOrDefault(row =>
                string.Equals(row.AssetName, attributeKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"当前游戏定义中没有可堆叠背包物品“{attributeKey}”。");
            return new AdapterFieldValue(fieldKey, item.Count.ToString(CultureInfo.InvariantCulture),
                "已按稳定资产标识重新定位 · 当前背包实时总数");
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, DropRulesEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, DropRulesEntityId, StringComparison.Ordinal))
        {
            using var runtime = new DeskrawlRuntime(process);
            var settings = runtime.ReadDropRules();
            return attributeKey switch
            {
                "equipment-drop-rate" => new AdapterFieldValue(fieldKey,
                    (settings.EquipmentDropRateOverride ?? -1).ToString(CultureInfo.InvariantCulture),
                    settings.EquipmentDropRateOverride is null
                        ? "使用各类怪物原始掉落权重"
                        : "已覆盖普通、精英、小头目、头目和神话头目的每次装备掉落尝试"),
                "offline-tradable-drop-multiplier" => new AdapterFieldValue(fieldKey,
                    settings.EffectiveOfflineTradableDropMultiplierPercent.ToString(CultureInfo.InvariantCulture),
                    settings.OfflineTradableDropMultiplierOverride is null
                        ? "游戏当前离线可交易特殊掉落权重倍率"
                        : "已覆盖稀有鱼及部分特殊物品候选权重倍率"),
                _ => throw new InvalidOperationException($"掉落规则“{attributeKey}”不存在。")
            };
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, FishingEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, FishingEntityId, StringComparison.Ordinal))
        {
            using var runtime = new DeskrawlRuntime(process);
            var settings = runtime.ReadFishingSettings();
            if (!string.Equals(attributeKey, "success-rate", StringComparison.Ordinal))
                throw new InvalidOperationException($"钓鱼设置“{attributeKey}”不存在。");
            return new AdapterFieldValue(fieldKey,
                (settings.SuccessRateOverride ?? -1).ToString(CultureInfo.InvariantCulture),
                settings.SuccessRateOverride is null ? "使用游戏原始成功率公式" : "已覆盖最终钓鱼成功率");
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, EquipmentEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, EquipmentEntityId, StringComparison.Ordinal) &&
            string.Equals(attributeKey, "maximum-rolls", StringComparison.Ordinal))
        {
            using var runtime = new DeskrawlRuntime(process);
            return new AdapterFieldValue(fieldKey, runtime.IsEquipmentMaximumEnabled() ? "1" : "0",
                "仅影响之后生成的随机装备词条；关闭或离开页面会恢复游戏原逻辑");
        }

        using var materialRuntime = new DeskrawlRuntime(process);
        var material = materialRuntime.ReadMaterials().SingleOrDefault(item =>
            string.Equals(item.AssetName, fieldKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"当前游戏定义中没有材料“{fieldKey}”。");
        return new AdapterFieldValue(fieldKey, material.Count.ToString(CultureInfo.InvariantCulture),
            "已从完整材料定义表定位 · 当前背包实时数量");
    }

    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue)
    {
        if (!long.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
            throw new InvalidOperationException("目标值必须是整数。");

        if (ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var attributeKey) &&
            string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal))
        {
            var updated = WriteEditorField(process, editorId, entityId, attributeKey, target);
            var field = updated.Fields.Single(item => string.Equals(item.Key, attributeKey, StringComparison.Ordinal));
            return new AdapterFieldValue(fieldKey, field.ValueDisplay, field.Status);
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, InventoryItemsEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, StackableEntityId, StringComparison.Ordinal))
        {
            if (target is < 0 or > int.MaxValue)
                throw new InvalidOperationException($"背包物品数量必须在 0 到 {int.MaxValue} 之间。");
            lock (WriteGate)
            {
                using var runtime = new DeskrawlRuntime(process);
                var updated = runtime.WriteStackableItem(attributeKey, checked((int)target));
                return new AdapterFieldValue(fieldKey, updated.Count.ToString(CultureInfo.InvariantCulture),
                    "已通过游戏背包路径写入、回读并请求保存");
            }
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, DropRulesEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, DropRulesEntityId, StringComparison.Ordinal))
        {
            lock (WriteGate)
            {
                using var runtime = new DeskrawlRuntime(process);
                var settings = runtime.WriteDropRule(attributeKey, target == -1 ? null : checked((int)target));
                var value = attributeKey == "equipment-drop-rate"
                    ? settings.EquipmentDropRateOverride ?? -1
                    : settings.EffectiveOfflineTradableDropMultiplierPercent;
                return new AdapterFieldValue(fieldKey, value.ToString(CultureInfo.InvariantCulture),
                    target == -1 ? "已恢复游戏原始掉落规则" : "本次运行覆盖已写入并回读");
            }
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, FishingEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, FishingEntityId, StringComparison.Ordinal))
        {
            lock (WriteGate)
            {
                using var runtime = new DeskrawlRuntime(process);
                var settings = runtime.WriteFishingSetting(attributeKey, target == -1 ? null : checked((int)target));
                var value = settings.SuccessRateOverride ?? -1;
                return new AdapterFieldValue(fieldKey, value.ToString(CultureInfo.InvariantCulture),
                    target == -1 ? "已恢复游戏原始逻辑" : "本次运行覆盖已写入并回读");
            }
        }

        if (ModuleFieldKey.TryParse(fieldKey, out editorId, out entityId, out attributeKey) &&
            string.Equals(editorId, EquipmentEditorId, StringComparison.Ordinal) &&
            string.Equals(entityId, EquipmentEntityId, StringComparison.Ordinal) &&
            string.Equals(attributeKey, "maximum-rolls", StringComparison.Ordinal))
        {
            if (target is not 0 and not 1) throw new InvalidOperationException("装备词条最高值开关只接受 0 或 1。");
            lock (WriteGate)
            {
                using var runtime = new DeskrawlRuntime(process);
                var enabled = runtime.SetEquipmentMaximum(target == 1);
                return new AdapterFieldValue(fieldKey, enabled ? "1" : "0",
                    enabled ? "新生成装备的随机词条将取合法最高值" : "已恢复游戏原始随机区间");
            }
        }

        if (target is < 0 or > int.MaxValue)
            throw new InvalidOperationException($"材料数量必须在 0 到 {int.MaxValue} 之间。");
        lock (WriteGate)
        {
            using var runtime = new DeskrawlRuntime(process);
            var updated = runtime.WriteMaterial(fieldKey, checked((int)target));
            return new AdapterFieldValue(fieldKey, updated.Count.ToString(CultureInfo.InvariantCulture),
                "已通过游戏材料背包路径写入、回读并请求保存");
        }
    }

    public IReadOnlyList<AdapterInventoryItem> ReadInventory(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadMaterials()
            .Select(item => new AdapterInventoryItem(item.AssetName, item.DisplayName, item.Count))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    internal IReadOnlyList<AdapterInventoryItem> ReadStackableInventory(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadStackableItems()
            .Select(item => new AdapterInventoryItem(
                ModuleFieldKey.Create(InventoryItemsEditorId, StackableEntityId, item.AssetName),
                item.DisplayName,
                item.Count))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    internal DeskrawlFishingSettings ReadFishingSettings(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadFishingSettings();
    }

    internal DeskrawlDropRulesSettings ReadDropRules(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadDropRules();
    }

    internal bool ReadEquipmentMaximum(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.IsEquipmentMaximumEnabled();
    }

    internal void RestoreFishingSettings(GameProcessContext process)
    {
        lock (WriteGate)
        {
            using var runtime = new DeskrawlRuntime(process);
            runtime.RestoreFishingSettings();
        }
    }

    internal void RestoreDropRules(GameProcessContext process)
    {
        lock (WriteGate)
        {
            using var runtime = new DeskrawlRuntime(process);
            runtime.RestoreDropRules();
        }
    }

    internal void RestoreEquipmentMaximum(GameProcessContext process)
    {
        lock (WriteGate)
        {
            using var runtime = new DeskrawlRuntime(process);
            runtime.RestoreEquipmentMaximum();
        }
    }

    public bool SupportsEntityEditor(GameProcessContext process, string editorId)
    {
        if (!string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal)) return false;
        try
        {
            using var runtime = new DeskrawlRuntime(process);
            runtime.ValidateCharacter();
            return runtime.HasLoadedCharacter;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(GameProcessContext process, string editorId)
    {
        if (!string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal)) return [];
        return [ReadCharacter(process)];
    }

    private static AdapterEditorEntity ReadCharacter(GameProcessContext process)
    {
        using var runtime = new DeskrawlRuntime(process);
        return runtime.ReadCharacter();
    }

    public AdapterEditorEntity WriteEditorField(
        GameProcessContext process,
        string editorId,
        string entityId,
        string fieldKey,
        long targetValue)
    {
        if (!string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal) ||
            !string.Equals(entityId, CharacterEntityId, StringComparison.Ordinal))
            throw new InvalidOperationException("当前人物字段身份无效，请刷新后重试。");
        lock (WriteGate)
        {
            using var runtime = new DeskrawlRuntime(process);
            return runtime.WriteCharacterField(fieldKey, targetValue);
        }
    }

}

internal static class DeskrawlBuild
{
    internal const string Executable = "7E3D03F93C507F9109611DC17414EE8F730BA730AED09D28AD0E4689E5726BEE";
    internal const string GameAssembly = "F8221364BE8101C60D76BE3E031F2CD5BE376A7FB6B9B73A7CFB45E2A52A9FAB";
    internal const string Metadata = "F5CF7BA1BBD13CB099EA231AC89ED772F10C9F28D329F5DAB9BC805A3809B802";

    internal static bool IsKnown(GameBuildIdentity identity) =>
        identity.ExecutableSha256.Equals(Executable, StringComparison.OrdinalIgnoreCase) &&
        identity.GameAssemblySha256.Equals(GameAssembly, StringComparison.OrdinalIgnoreCase) &&
        identity.MetadataSha256.Equals(Metadata, StringComparison.OrdinalIgnoreCase);
}
