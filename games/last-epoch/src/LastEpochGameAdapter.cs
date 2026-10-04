using System.Globalization;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LastEpoch;

public sealed record LastEpochCooldownStatDiagnostic(
    int Tags,
    int SecondaryTags,
    float AddedValue,
    float IncreasedValue);

public sealed record LastEpochAbilityCooldownDiagnostic(
    int Index,
    string AbilityId,
    string AbilityName,
    bool IsTraversal,
    float Charge,
    float ChargeRegenPerSecond,
    float MaxCharges,
    float BaseChargeRegenPerSecond,
    float EstimatedRemainingCooldownSeconds);

public sealed record LastEpochCooldownDiagnostics(
    float ManagerIncreasedRecoverySpeed,
    float MovementSkillIncreasedRecoverySpeed,
    float GenericCooldownAddedTotal,
    IReadOnlyList<LastEpochCooldownStatDiagnostic> GenericCooldownStats,
    IReadOnlyList<LastEpochAbilityCooldownDiagnostic> Abilities);

public sealed record LastEpochExperienceDiagnostics(
    int Level,
    long CurrentExperience,
    long NextLevelExperience,
    int IncreasedExperiencePercent,
    bool GainHookInstalled,
    long LastOriginalCharacterExperience,
    long LastAppliedCharacterExperience,
    long ObservedGainCount);

public sealed record LastEpochPotionDropDiagnostics(
    float BaseDropChance,
    float IncreasedPotionDropRate,
    float FinalDropChance);

public sealed record LastEpochItemDropDiagnostics(
    int IncreasedDropRatePercent,
    bool HookInstalled,
    float ChanceMultiplier,
    float LastOriginalChance,
    float LastAppliedChance,
    long ObservedRollCount);

public sealed record LastEpochGoldQuantityDiagnostics(
    int IncreasedGoldQuantityPercent,
    bool HookInstalled,
    float QuantityMultiplier,
    int LastOriginalQuantity,
    int LastAppliedQuantity,
    long ObservedDropCount);

public sealed class LastEpochGameAdapter :
    ICharacterAttributesGameAdapter,
    IEntityEditorsGameAdapter,
    IGameVersionMetadataProvider,
    IGameEditorPageProvider,
    IGameEditorFieldPolicyProvider
{
    internal const string CharacterEditorId = "game.last-epoch.character-attributes";
    internal const string EquipmentEditorId = "game.last-epoch.equipment";
    internal const string MaterialsEditorId = "game.last-epoch.materials";
    internal const string MonolithEditorId = "game.last-epoch.monolith";
    internal const string WorldEditorId = "game.last-epoch.world";

    public string Id => "game.last-epoch";
    public string DisplayName => "Last Epoch 离线专属修改模块";
    public string Description => "人物属性、装备潜能、完整资源槽位、异界进度与世界功能。";
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(CharacterEditorId, "人物属性", GameEditorKind.MasterDetail, 200, "剩余天赋点、剩余技能点、基础属性与实用倍率。"),
        new(EquipmentEditorId, "装备编辑", GameEditorKind.MasterDetail, 300, "只修改熔炉主槽当前装备的潜能。"),
        new(MaterialsEditorId, "资源", GameEditorKind.MasterDetail, 400, "全部词缀碎片、符文、雕文和副本钥匙；未拥有的资源显示为 0。"),
        new(MonolithEditorId, "异界进度", GameEditorKind.MasterDetail, 500, "最高腐化与已有时间线进度。"),
        new(WorldEditorId, "世界功能", GameEditorKind.MasterDetail, 600, "调整当前摄像头视野大小。", true)
    ];
    public IReadOnlyList<GameEditorPageRegistration> EditorPages { get; } =
    [
        new(CharacterEditorId, GameEditorPageRole.CharacterAttributes),
        new(EquipmentEditorId, GameEditorPageRole.Entity),
        new(MaterialsEditorId, GameEditorPageRole.Entity),
        new(MonolithEditorId, GameEditorPageRole.Entity,
            "当前角色尚无异界时间线记录；进入异界后刷新即可显示。"),
        new(WorldEditorId, GameEditorPageRole.Entity)
    ];

    public GameEditorFieldPolicy? GetFieldPolicy(string editorId, string entityId, string fieldId)
    {
        if (!string.Equals(editorId, CharacterEditorId, StringComparison.Ordinal)) return null;
        return fieldId is "skill-points" or "specialisation-points"
            ? new(false, true)
            : new(true, false);
    }

    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint)
    {
        if (!process.ProcessName.Equals("Last Epoch", StringComparison.OrdinalIgnoreCase) ||
            !LastEpochRuntime.IsOfflineProcess(process.ProcessId))
            return false;
        try
        {
            using var runtime = new LastEpochRuntime(process.ProcessId);
            runtime.ValidateCompatibility();
            return true;
        }
        catch
        {
            // A new build is accepted only after the runtime can resolve and validate the
            // required IL2CPP symbols. Unknown or structurally incompatible builds fail closed.
            return false;
        }
    }

    public bool SupportsCharacterAttributes(GameProcessContext process)
    {
        try
        {
            using var runtime = Open(process);
            runtime.ValidateCompatibility();
            return runtime.HasLoadedCharacter;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AdapterCharacterItem> ReadCharacters(GameProcessContext process)
    {
        using var runtime = Open(process);
        return [runtime.ReadCharacter()];
    }

    public LastEpochCooldownDiagnostics ReadCooldownDiagnostics(GameProcessContext process)
    {
        using var runtime = Open(process);
        return runtime.ReadCooldownDiagnostics();
    }

    public LastEpochCooldownDiagnostics ApplyGenericCooldownRecoveryTest(
        GameProcessContext process,
        int targetPercent)
    {
        using var runtime = Open(process);
        return runtime.ApplyGenericCooldownRecoveryTest(targetPercent);
    }

    public LastEpochExperienceDiagnostics ReadExperienceDiagnostics(GameProcessContext process)
    {
        using var runtime = Open(process);
        return runtime.ReadExperienceDiagnostics();
    }

    public LastEpochPotionDropDiagnostics ReadPotionDropDiagnostics(GameProcessContext process)
    {
        using var runtime = Open(process);
        return runtime.ReadPotionDropDiagnostics();
    }

    public LastEpochItemDropDiagnostics ReadItemDropDiagnostics(GameProcessContext process)
    {
        using var runtime = Open(process);
        return runtime.ReadItemDropDiagnostics();
    }

    public LastEpochGoldQuantityDiagnostics ReadGoldQuantityDiagnostics(GameProcessContext process)
    {
        using var runtime = Open(process);
        return runtime.ReadGoldQuantityDiagnostics();
    }

    public AdapterCharacterItem WriteCharacterAttribute(
        GameProcessContext process,
        string characterId,
        string attributeKey,
        int targetValue)
    {
        using var runtime = Open(process);
        runtime.WriteCharacterAttribute(characterId, attributeKey, targetValue);
        return runtime.ReadCharacter();
    }

    public bool SupportsEntityEditor(GameProcessContext process, string editorId)
    {
        if (editorId is not (EquipmentEditorId or MaterialsEditorId or MonolithEditorId or WorldEditorId)) return false;
        try
        {
            using var runtime = Open(process);
            runtime.ValidateCompatibility();
            return runtime.HasLoadedCharacter;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(GameProcessContext process, string editorId)
    {
        using var runtime = Open(process);
        return editorId switch
        {
            EquipmentEditorId => runtime.ReadEquipment(),
            MaterialsEditorId => runtime.ReadMaterials(),
            MonolithEditorId => runtime.ReadMonolith(),
            WorldEditorId => runtime.ReadWorld(),
            _ => throw new InvalidOperationException($"Last Epoch 不支持编辑器 {editorId}。")
        };
    }

    public AdapterEditorEntity WriteEditorField(
        GameProcessContext process,
        string editorId,
        string entityId,
        string fieldKey,
        long targetValue)
    {
        using var runtime = Open(process);
        return editorId switch
        {
            EquipmentEditorId => runtime.WriteEquipment(entityId, fieldKey, targetValue),
            MaterialsEditorId => runtime.WriteMaterial(entityId, fieldKey, targetValue),
            MonolithEditorId => runtime.WriteMonolith(entityId, fieldKey, targetValue),
            WorldEditorId => runtime.WriteWorld(entityId, fieldKey, targetValue),
            _ => throw new InvalidOperationException($"Last Epoch 不支持编辑器 {editorId}。")
        };
    }

    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey)
    {
        if (!ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var key))
            throw new InvalidOperationException("Last Epoch 专属字段键无效。");
        if (editorId == CharacterEditorId)
        {
            var character = ReadCharacters(process).Single(item => item.CharacterId == entityId);
            var field = character.Attributes.Single(item => item.Key == key);
            return new(fieldKey, field.RawValueDisplay, "已通过人物点数对象重新定位");
        }
        var entity = ReadEditorEntities(process, editorId).Single(item => item.EntityId == entityId);
        var value = entity.Fields.Single(item => item.Key == key);
        return new(fieldKey, value.ValueDisplay, "已通过游戏专属语义键重新定位");
    }

    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue)
    {
        if (!long.TryParse(displayValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
            throw new InvalidOperationException("目标值必须是整数。");
        if (!ModuleFieldKey.TryParse(fieldKey, out var editorId, out var entityId, out var key))
            throw new InvalidOperationException("Last Epoch 专属字段键无效。");

        if (editorId == CharacterEditorId)
        {
            if (target is < -100 or > 100_000)
                throw new InvalidOperationException("人物属性数值超出允许范围。");
            var result = WriteCharacterAttribute(process, entityId, key, checked((int)target));
            var field = result.Attributes.Single(item => item.Key == key);
            return new(fieldKey, field.RawValueDisplay, "已写入并请求游戏自动保存");
        }
        var entity = WriteEditorField(process, editorId, entityId, key, target);
        var fieldValue = entity.Fields.Single(item => item.Key == key);
        return new(fieldKey, fieldValue.ValueDisplay, "已写入并请求游戏自动保存");
    }

    public GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process)
    {
        using var runtime = Open(process);
        var metadata = runtime.ReadApplicationMetadata();
        return new(metadata.Version, metadata.ProductName, metadata.BuildGuid);
    }

    private static LastEpochRuntime Open(GameProcessContext process)
    {
        if (!LastEpochRuntime.IsOfflineProcess(process.ProcessId))
            throw new InvalidOperationException("Last Epoch 专属模块只支持通过“完全离线模式”启动的游戏进程。");
        return new LastEpochRuntime(process.ProcessId);
    }
}
