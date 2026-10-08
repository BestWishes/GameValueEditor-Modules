using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed record DropOption(string Group, string Path, string Name, double Default, double Minimum = 0, double Maximum = 100, string Help = "最终概率（%），不改变原生数量和保底规则。");
internal sealed class DropProfile
{
    internal static readonly string[] Gear = ["w:14000", "w:14001", "w:14002", "w:14003", "a:14000", "a:14001", "a:14002", "a:14003"];
    internal static readonly string[] Immortals = ["w:903", "w:895", "a:1464", "a:1465", "w:883", "w:916", "a:1460", "a:1455", "w:906", "w:913", "a:1453", "a:1467"];
    internal static readonly string[] Mythics = ["w:10007", "a:10008", "w:10008"];
    private static readonly Dictionary<string, string> CowNames = new()
    {
        ["w:14000"] = "牧星裂角剑", ["w:14001"] = "赤穗斩蹄斧", ["w:14002"] = "月乳秘纹杖", ["w:14003"] = "阡陌逐影弓",
        ["a:14000"] = "黑斑牧野甲", ["a:14001"] = "金穗踏歌靴", ["a:14002"] = "牦纹巡原盔", ["a:14003"] = "白角缚星腕",
        ["w:903"] = "阿波罗之锤", ["w:895"] = "王室守护者", ["a:1464"] = "勇士之心", ["a:1465"] = "遗迹之光项链",
        ["w:883"] = "欧皇", ["w:916"] = "狂想之笛", ["a:1460"] = "护卫之甲", ["a:1455"] = "冰霜外衣",
        ["w:906"] = "血滴子", ["w:913"] = "黑色灵魂石长杖", ["a:1453"] = "血月精华", ["a:1467"] = "血神皮",
        ["w:10007"] = "赫拉迪姆汉堡", ["a:10008"] = "神圣庇护所", ["w:10008"] = "奶牛王战戟"
    };
    internal static readonly DropOption[] Options =
    [
        new("总加成", "equipmentBonusPercent", "掉落率额外加成", 0, -100, 100, "在游戏原掉落词条总加成上增减百分点；不修改装备。"),
        new("总加成", "upgradeBonusPercent", "掉落装备升级率额外加成", 0, -100, 100, "在原徽章/时装升级率上增减百分点，最终限制在 0～100%。"),
        new("总加成", "goldBonusPercent", "金币掉落率额外加成", 0, -100, 1000, "游戏该词条实际是金币收益加成；按基础金币增减百分点，不是金币出现概率。"),
        new("战斗规则", "ordinaryEquipmentPercent", "普通装备掉落", 38, Help: "覆盖远征普通装备最终随机概率，优先于上面的掉落率额外加成。"),
        new("战斗规则", "abyssImmortalPercent", "深渊不朽装备", 5, Help: "仅覆盖随机掉落判定，保留原生保底。0% 不会关闭保底奖励。"),
        new("战斗规则", "guardianTicketPercent", "守关获得深渊挑战券", 50),
        new("战斗规则", "flameImmortalPercent", "火焰王座不朽专属装备", 5),
        new("战斗规则", "flameMythicPercent", "火焰王座神话专属装备", 0.5),
        new("矿区", "mine.rewardPercent", "有效普攻触发采矿", 1, Help: "仅原生允许采矿的普通命中，保留技能/noMining 筛选。"),
        new("矿区", "mine.stonePercent", "采矿奖励为强化石", 60, Help: "剩余概率为矿石；原生通常 60%，矿工 105 五星为 50%。数量仍由游戏决定。"),
        new("矿石品质", "mine.qualityPercent.blue", "蓝色矿石", 70, Help: "四种矿石品质合计必须 100%；这是获得矿石后的条件概率。"),
        new("矿石品质", "mine.qualityPercent.purple", "紫色矿石", 25),
        new("矿石品质", "mine.qualityPercent.immortal", "不朽矿石", 4),
        new("矿石品质", "mine.qualityPercent.mythic", "神话矿石", 1),
        new("奶牛关", "cow.ordinaryEquipmentPercent", "普通奶牛装备奖励", 100, Help: "原生必掉，可调整是否获得这份原生奖励；金币/强化石不受影响。"),
        new("奶牛关", "cow.eliteEquipmentPercent", "精英奶牛普通装备奖励", 100),
        new("奶牛关", "cow.eliteImmortalPercent", "精英奶牛不朽装备奖励", 100),
        ..Mythics.Select(key => new DropOption("奶牛关", "cow.mythicPercent." + key, "奶牛王神话 · " + CowNames[key], 0.5, Help: "三件神话各自独立判定，不要求合计 100%。")),
        ..Gear.Select(key => new DropOption("奶牛奖池权重", "cow.gearWeights." + key, "普通奖池 · " + CowNames[key], 1, Help: "相对权重：未设置为 1；0 为排除。不能全部为 0，不增加奖励数量。")),
        ..Immortals.Select(key => new DropOption("奶牛奖池权重", "cow.immortalWeights." + key, "不朽奖池 · " + CowNames[key], 1, Help: "相对权重：未设置为 1；0 为排除。不能全部为 0，不增加奖励数量。"))
    ];
    private readonly Dictionary<string, double> _values;
    internal DropProfile(IEnumerable<KeyValuePair<string, double>> values) { _values = new(values, StringComparer.Ordinal); Validate(); }
    internal static DropProfile Empty => new([]);
    internal bool IsEmpty => _values.Count == 0;
    internal bool TryGet(string path, out double value) => _values.TryGetValue(path, out value);
    internal void Validate()
    {
        foreach (var pair in _values)
        {
            var option = Options.SingleOrDefault(item => item.Path == pair.Key);
            if (option is null || !double.IsFinite(pair.Value) || pair.Value < option.Minimum || pair.Value > option.Maximum)
                throw new InvalidOperationException("掉落配置含未知项目或数值超出范围。");
        }
        var quality = Options.Where(item => item.Group == "矿石品质").ToArray();
        if (quality.Any(item => _values.ContainsKey(item.Path)) &&
            (!quality.All(item => _values.ContainsKey(item.Path)) || Math.Abs(quality.Sum(item => _values[item.Path]) - 100) > 1e-8))
            throw new InvalidOperationException("四种矿石品质必须一起启用，且合计 100%。");
        foreach (var prefix in new[] { "cow.gearWeights.", "cow.immortalWeights." })
            if (Options.Where(item => item.Path.StartsWith(prefix, StringComparison.Ordinal)).Sum(item => _values.GetValueOrDefault(item.Path, 1)) <= 0)
                throw new InvalidOperationException("奶牛奖励的同一奖池不能全部排除。");
    }
    internal string ToJson()
    {
        Validate(); var drops = new JsonObject();
        foreach (var pair in _values)
        {
            var parts = pair.Key.Split('.'); var node = drops;
            foreach (var part in parts[..^1]) { node[part] ??= new JsonObject(); node = node[part]!.AsObject(); }
            node[parts[^1]] = pair.Value;
        }
        return IsEmpty ? "{}" : new JsonObject { ["drops"] = drops }.ToJsonString();
    }
    internal static DropProfile Parse(string json)
    {
        if (json.Length > 8192) throw new InvalidOperationException("掉落配置过大。");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("掉落配置必须是对象。");
        if (!root.EnumerateObject().Any()) return Empty;
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "drops") throw new InvalidOperationException("掉落配置只能包含掉落项目。");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        void Read(JsonElement node, string prefix)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.EnumerateObject().Any()) throw new InvalidOperationException("掉落配置分组不能为空。");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in node.EnumerateObject())
            {
                if (!keys.Add(item.Name) || item.Name.Contains('.')) throw new InvalidOperationException("掉落配置含重复或非法字段。");
                var path = prefix.Length == 0 ? item.Name : prefix + "." + item.Name;
                if (Options.Any(option => option.Path == path))
                {
                    if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetDouble(out var number)) throw new InvalidOperationException("掉落配置必须使用有限数值。");
                    values.Add(path, number);
                }
                else if (Options.Any(option => option.Path.StartsWith(path + ".", StringComparison.Ordinal))) Read(item.Value, path);
                else throw new InvalidOperationException("掉落配置含未知字段。");
            }
        }
        Read(properties[0].Value, ""); return new(values);
    }
    internal static string Format(double number) => number.ToString("0.####", CultureInfo.InvariantCulture);
}

internal sealed record DropsSnapshot(SessionRead Receipt, DropProfile Profile, bool Installed, bool Ready, string Status, IReadOnlyDictionary<string, string> NativeValues);
