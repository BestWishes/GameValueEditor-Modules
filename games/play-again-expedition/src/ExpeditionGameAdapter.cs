using System.Globalization;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

public sealed class ExpeditionGameAdapter : ICoordinatedGameEditorPageProvider,
    IGameCompatibilityDiagnosticsProvider, IGameEditorFieldPolicyProvider, IGameVersionMetadataProvider
{
    internal const string MaterialsId = "game.play-again-expedition.materials";
    internal const string WheelId = "game.play-again-expedition.lucky-wheel";
    internal const string DropsId = "game.play-again-expedition.drops";
    internal const string WheelEntity = "session";
    internal const string WheelField = "configuration";
    internal static readonly string WheelKey = ModuleFieldKey.Create(WheelId, WheelEntity, WheelField);
    internal static readonly string DropsKey = ModuleFieldKey.Create(DropsId, WheelEntity, WheelField);
    private static readonly object WriteGate = new();
    private readonly IExpeditionClient _client;
    public ExpeditionGameAdapter() : this(new ExpeditionClient()) { }
    internal ExpeditionGameAdapter(IExpeditionClient client) => _client = client;
    public string Id => "game.play-again-expedition";
    public string DisplayName => "再刷一把：远征专属修改模块";
    public string Description => "材料数量、大转盘与掉落规则；直接接入正常启动的原游戏，无需启动参数或重开。";
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(MaterialsId, "材料", GameEditorKind.Custom, 100, "修改 29 项材料数量并调用游戏自身保存流程。"),
        new(WheelId, "大转盘", GameEditorKind.Custom, 200, "调整奖格与额外奖励概率，重启游戏恢复原规则。", true),
        new(DropsId, "掉落", GameEditorKind.Custom, 300, "总加成、战斗规则、矿区与奶牛关；只影响当前会话。", true)
    ];
    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint) => _client.Supports(process, fingerprint);
    public IGameEditorPage CreateEditorPage(string editorId, GameEditorPageContext context) => editorId switch
    {
        MaterialsId => new MaterialsPage(this, context),
        WheelId => new WheelPage(this, context),
        DropsId => new DropsPage(this, context),
        _ => throw new InvalidOperationException("当前模块没有这个内容页面。")
    };
    internal MaterialsSnapshot ReadMaterials(GameProcessContext process) => _client.ReadMaterials(process);
    internal WheelSnapshot ReadWheel(GameProcessContext process) => _client.ReadWheel(process);
    internal DropsSnapshot ReadDrops(GameProcessContext process) => _client.ReadDrops(process);
    public GameEditorFieldPolicy? GetFieldPolicy(string editorId, string entityId, string fieldId) => editorId switch
    {
        MaterialsId when fieldId == "quantity" => new(false, false),
        WheelId when entityId == WheelEntity && fieldId == WheelField => new(true, false),
        DropsId when entityId == WheelEntity && fieldId == WheelField => new(true, false),
        _ => null
    };
    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey)
    {
        Parse(fieldKey, out var editor, out var entity, out var field);
        if (editor == MaterialsId && field == "quantity")
        {
            var read = ReadMaterials(process);
            var row = read.Rows.SingleOrDefault(row => row.Id == entity) ?? throw new InvalidOperationException("未知材料。");
            return new(fieldKey, row.Quantity, row.Status + read.Receipt.ConnectionWarning);
        }
        if (editor == WheelId && entity == WheelEntity && field == WheelField)
        {
            var wheel = ReadWheel(process); return new(fieldKey, wheel.Profile.ToJson(), wheel.Status + wheel.Receipt.ConnectionWarning);
        }
        if (editor == DropsId && entity == WheelEntity && field == WheelField)
        { var drops = ReadDrops(process); return new(fieldKey, drops.Profile.ToJson(), drops.Status + drops.Receipt.ConnectionWarning); }
        throw new InvalidOperationException("模块字段语义键无效。");
    }
    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue)
    {
        Parse(fieldKey, out var editor, out var entity, out var field);
        // Reject invalid input before reading process/network or creating a claim.
        if (editor == MaterialsId && field == "quantity")
        {
            if (!long.TryParse(displayValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) || target < 0 || target > 1000000000)
                throw new InvalidOperationException("材料数量必须是允许范围内的非负整数。");
            lock (WriteGate)
            {
                var snapshot = ReadMaterials(process);
                var row = _client.WriteMaterial(process, snapshot, entity, target);
                return new(fieldKey, row.Quantity, row.Status);
            }
        }
        if (editor == WheelId && entity == WheelEntity && field == WheelField)
        {
            var profile = displayValue == "restore" ? null : WheelProfile.Parse(displayValue);
            lock (WriteGate)
            {
                var snapshot = ReadWheel(process);
                if (profile is null) _client.ResetWheel(process, snapshot);
                else _client.ConfigureWheel(process, snapshot, profile);
                return new(fieldKey, (profile ?? WheelProfile.Default).ToJson(), (profile is null ? "已恢复游戏原概率" : "概率已应用，仅当前游戏会话有效") + _client.ConnectionWarning);
            }
        }
        if (editor == DropsId && entity == WheelEntity && field == WheelField)
        {
            var profile = displayValue == "restore" ? null : DropProfile.Parse(displayValue);
            if (profile?.IsEmpty == true) throw new InvalidOperationException("请启用至少一项，或使用恢复原规则。");
            lock (WriteGate)
            {
                var snapshot = ReadDrops(process);
                if (profile is null) _client.ResetDrops(process, snapshot); else _client.ConfigureDrops(process, snapshot, profile);
                return new(fieldKey, (profile ?? DropProfile.Empty).ToJson(), (profile is null ? "已恢复掉落原规则；大转盘设置不变" : "掉落规则已应用，仅当前游戏会话有效") + _client.ConnectionWarning);
            }
        }
        throw new InvalidOperationException("模块字段语义键无效。");
    }
    private static void Parse(string key, out string editor, out string entity, out string field)
    { if (!ModuleFieldKey.TryParse(key, out editor, out entity, out field)) throw new InvalidOperationException("请使用有效的模块语义字段键。"); }
    public GameDeclaredVersionInfo ReadGameVersionMetadata(GameProcessContext process)
    {
        var session = OriginalGameSession.Resolve(process, false);
        return new(ArchiveMetadata.ReadVersion(session.ArchivePath), "再刷一把：远征", string.Empty);
    }
    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(GameProcessContext process, GameBuildIdentity fingerprint)
    {
        var supported = Supports(process, fingerprint);
        var endpoint = supported && _client.CanAttachDirectly(process);
        return
        [
            new("游戏名称与当前运行实例", supported ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Failed,
                supported ? "已按名称定位当前远征实例；不使用旧版本文件指纹限制小更新。材料和概率在刷新时按当前接口定位。" : "没有找到选中进程所属的唯一远征实例，请连接正在运行的游戏。"),
            new("运行时直接接入入口", endpoint ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Warning,
                endpoint ? "原游戏提供已验证的接入入口；刷新时自动接入，无需启动参数。此诊断未启用接口或执行游戏脚本。" : "运行时入口不可用、权限不足或端口 9229 被占用，已拒绝猜测入口；不会连接或关闭其它程序。"),
            new("内容页面", GameCompatibilityDiagnosticStatus.Information, "材料：原生保存；大转盘、掉落：独立会话规则。诊断没有抽奖、修改数量、保存或读取存档内容。")
        ];
    }
}
