using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed record MaterialRow(string Id, string Name, string Category, long? Value, long? StoredValue,
    long Minimum, long Maximum, bool CanWrite, string Status)
{
    public string Quantity => Value?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string SavedQuantity => StoredValue?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string Range => $"{Minimum:N0} ～ {Maximum:N0}";
    public string FieldKey => ModuleFieldKey.Create(ExpeditionGameAdapter.MaterialsId, Id, "quantity");
}

internal sealed record WheelGroups(double Ordinary, double Immortal, double Mythic, double Protection, double SacredProtection);
internal sealed record WheelPrize(WheelGroups GroupPercent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, double>? TypeWeights = null);
internal sealed record WheelBonus(double DoublePercent, double MarqueePercent);
internal sealed record WheelProfile(WheelPrize Wheel, WheelBonus WheelBonus)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static readonly string[] WeightKeys = ["gear", "costume", "badge", "gem", "material", "q3", "q4", "q5", "q6"];
    public static WheelProfile Default => new(new(new(92.5, 5, 1, 1, 0.5)), new(2, 1));
    public string ToJson() { Validate(); return JsonSerializer.Serialize(this, JsonOptions); }
    internal static WheelProfile FromSessionSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || settings.EnumerateObject().Any(property => property.Name is not ("wheel" or "wheelBonus")) ||
            settings.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("转盘会话设置未确认。");
        var complete = System.Text.Json.Nodes.JsonNode.Parse(Default.ToJson())!.AsObject();
        foreach (var property in settings.EnumerateObject()) complete[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
        return Parse(complete.ToJsonString());
    }
    public static WheelProfile Parse(string json)
    {
        if (json.Length > 8192) throw new InvalidOperationException("概率配置过大。");
        using var document = JsonDocument.Parse(json);
        RejectDuplicates(document.RootElement);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("wheel", out var wheel) || wheel.ValueKind != JsonValueKind.Object ||
            !wheel.TryGetProperty("groupPercent", out var groups) || groups.ValueKind != JsonValueKind.Object ||
            !new[] { "ordinary", "immortal", "mythic", "protection", "sacredProtection" }.All(key => groups.TryGetProperty(key, out _)) ||
            !root.TryGetProperty("wheelBonus", out var bonus) || bonus.ValueKind != JsonValueKind.Object ||
            !bonus.TryGetProperty("doublePercent", out _) || !bonus.TryGetProperty("marqueePercent", out _))
            throw new InvalidOperationException("请填写完整的奖格和额外奖励概率。");
        var result = JsonSerializer.Deserialize<WheelProfile>(json, JsonOptions)
            ?? throw new InvalidOperationException("概率配置不能为空。");
        result.Validate(); return result;
    }
    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidOperationException("概率配置含重复字段。");
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) throw new InvalidOperationException("概率配置不能包含数组。");
    }
    public void Validate()
    {
        if (Wheel?.GroupPercent is not { } group || WheelBonus is not { } bonus) throw new InvalidOperationException("请填写完整的奖格和额外奖励概率。");
        var values = new[] { group.Ordinary, group.Immortal, group.Mythic, group.Protection, group.SacredProtection };
        if (values.Any(value => !double.IsFinite(value) || value < 0 || value > 100) || Math.Abs(values.Sum() - 100) > 1e-8)
            throw new InvalidOperationException("五类奖格概率必须在 0～100% 内，合计为 100%。");
        if (!double.IsFinite(bonus.DoublePercent) || !double.IsFinite(bonus.MarqueePercent) || bonus.DoublePercent < 0 || bonus.MarqueePercent < 0 ||
            bonus.DoublePercent > 100 || bonus.MarqueePercent > 100 || bonus.DoublePercent + bonus.MarqueePercent > 100)
            throw new InvalidOperationException("两种额外奖励概率必须在 0～100% 内，合计不能超过 100%。");
        if (Wheel.TypeWeights is { } weights && (weights.Count == 0 || weights.Any(pair => !WeightKeys.Contains(pair.Key, StringComparer.Ordinal) ||
            !double.IsFinite(pair.Value) || pair.Value < 0 || pair.Value > 100))) throw new InvalidOperationException("类内权重只能使用已定义项目的 0～100 数值。");
    }
}

internal sealed record WheelRow(int Index, string Reward, string Group, string OriginalPercent, string Percent);
internal sealed record GameSession(int ProcessId, long StartTicks, string ExecutablePath, string ArchivePath, string ProfilePath,
    string LauncherPath = "", int LauncherProcessId = 0, long LauncherStartTicks = 0);
internal sealed record SessionRead(GameSession Session, JsonElement Observation, string ConnectionWarning = "");
internal sealed record MaterialsSnapshot(SessionRead Receipt, IReadOnlyList<MaterialRow> Rows);
internal sealed record WheelSnapshot(SessionRead Receipt, WheelProfile Profile, bool Installed, bool Ready,
    string Status, IReadOnlyList<WheelRow> Rows);

internal interface IExpeditionClient
{
    bool Supports(GameProcessContext process, GameBuildIdentity build);
    bool CanAttachDirectly(GameProcessContext process);
    string ConnectionWarning { get; }
    MaterialsSnapshot ReadMaterials(GameProcessContext process);
    MaterialRow WriteMaterial(GameProcessContext process, MaterialsSnapshot read, string id, long target);
    WheelSnapshot ReadWheel(GameProcessContext process);
    void ConfigureWheel(GameProcessContext process, WheelSnapshot read, WheelProfile profile);
    void ResetWheel(GameProcessContext process, WheelSnapshot read);
    DropsSnapshot ReadDrops(GameProcessContext process);
    void ConfigureDrops(GameProcessContext process, DropsSnapshot read, DropProfile profile);
    void ResetDrops(GameProcessContext process, DropsSnapshot read);
}
