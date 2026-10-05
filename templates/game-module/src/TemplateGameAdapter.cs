using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.__CLASS__;

public sealed class __CLASS__GameAdapter :
    IGameAdapter,
    IEntityEditorsGameAdapter,
    IGameEditorPageProvider,
    IGameCompatibilityDiagnosticsProvider
{
    private const string MainEditorId = "__MODULE_ID__.main";

    public string Id => "__MODULE_ID__";
    public string DisplayName => "__MODULE_DISPLAY_NAME__";
    public string Description => "为 __GAME_DISPLAY_NAME__ 提供经过构建验证的专属编辑功能。";
    public IReadOnlyList<GameEditorDescriptor> Editors { get; } =
    [
        new(MainEditorId, "主要功能", GameEditorKind.MasterDetail, 100,
            "完成真实数据链路后替换此说明。")
    ];
    public IReadOnlyList<GameEditorPageRegistration> EditorPages { get; } =
    [
        new(MainEditorId, GameEditorPageRole.Entity, "模块尚未实现读取逻辑。")
    ];

    // 脚手架必须默认安全失败。实现并验证当前构建后才能返回 true。
    public bool Supports(GameProcessContext process, GameBuildIdentity fingerprint) => false;

    public AdapterFieldValue ReadField(GameProcessContext process, string fieldKey) =>
        throw new InvalidOperationException("模块尚未实现字段读取。");

    public AdapterFieldValue WriteField(GameProcessContext process, string fieldKey, string displayValue) =>
        throw new InvalidOperationException("模块尚未实现字段写入。");

    public bool SupportsEntityEditor(GameProcessContext process, string editorId) => false;

    public IReadOnlyList<AdapterEditorEntity> ReadEditorEntities(GameProcessContext process, string editorId) => [];

    public AdapterEditorEntity WriteEditorField(
        GameProcessContext process,
        string editorId,
        string entityId,
        string fieldKey,
        long targetValue) => throw new InvalidOperationException("模块尚未实现实体字段写入。");

    public IReadOnlyList<GameCompatibilityDiagnostic> GetCompatibilityDiagnostics(
        GameProcessContext process,
        GameBuildIdentity fingerprint) =>
    [
        new("构建支持", GameCompatibilityDiagnosticStatus.Warning,
            "脚手架尚未实现只读结构检查；完成真实数据链路后替换此项。")
    ];
}
