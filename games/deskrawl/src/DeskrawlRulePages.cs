using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;

namespace GameValueEditor.Modules.Deskrawl;

internal sealed class DeskrawlEquipmentGenerationPage : ModuleEditorPageBase
{
    private readonly DeskrawlGameAdapter _adapter;
    private readonly TextBlock _state = new() { FontSize = 20, Margin = new Thickness(0, 18, 0, 18) };
    private bool _restoreOnDispose;

    internal DeskrawlEquipmentGenerationPage(DeskrawlGameAdapter adapter, GameEditorPageContext context) : base(context)
    {
        _adapter = adapter;
        var root = GridWithRows(GridLength.Auto, GridLength.Auto, GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        root.Children.Add(Description(
            "装备随机词条先按物品等级和属性种类计算基础值，再乘稀有度系数，并在 0.85～1.15 倍之间随机。启用后只把这一步固定为游戏自己的 1.15 上限；不会影响其他随机数，也不会改动已经生成的装备。"));
        Grid.SetRow(_state, 1);
        root.Children.Add(_state);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var enable = ActionButton("启用最高值");
        enable.Click += async (_, _) => await RunAsync(() => SetAsync(true));
        var disable = ActionButton("恢复随机范围");
        disable.Click += async (_, _) => await RunAsync(() => SetAsync(false));
        var refresh = ActionButton("刷新状态");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        actions.Children.Add(enable);
        actions.Children.Add(disable);
        actions.Children.Add(refresh);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        await ReadSnapshotAsync(() => _adapter.ReadEquipmentMaximum(Context.Process), enabled =>
        {
            _restoreOnDispose = enabled;
            _state.Text = enabled
                ? "当前状态：已启用。之后生成装备的随机词条取合法最高值。"
                : "当前状态：游戏原始 85%～115% 随机范围。";
            Context.Host.ReportStatus(_state.Text);
        });
    }

    private async Task SetAsync(bool enabled)
    {
        var key = ModuleFieldKey.Create(
            DeskrawlGameAdapter.EquipmentEditorId,
            DeskrawlGameAdapter.EquipmentEntityId,
            "maximum-rolls");
        await WriteFieldAsync(key, enabled ? "1" : "0");
        await RefreshAsync();
    }

    public override void Dispose()
    {
        if (_restoreOnDispose)
        {
            try
            {
                _adapter.RestoreEquipmentMaximum(Context.Process);
                Context.Host.ReportStatus("装备词条最高值覆盖已随页面关闭恢复");
            }
            catch (Exception error)
            {
                Context.Host.ReportStatus($"装备词条覆盖恢复失败：{error.Message}");
            }
        }
        base.Dispose();
    }
}

internal sealed class DeskrawlDropRulesPage : ModuleEditorPageBase
{
    private readonly DeskrawlGameAdapter _adapter;
    private readonly TextBlock _equipment = new() { FontSize = 18, Margin = new Thickness(0, 16, 0, 6) };
    private readonly TextBlock _special = new() { FontSize = 18, Margin = new Thickness(0, 6, 0, 16) };
    private bool _hasOverrides;
    private int _effectiveSpecial = 100;

    internal DeskrawlDropRulesPage(DeskrawlGameAdapter adapter, GameEditorPageContext context) : base(context)
    {
        _adapter = adapter;
        var root = GridWithRows(GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        root.Children.Add(Description(
            "“装备掉落率”调整普通、精英、小头目、头目和神话头目的 Min/Max LootVector 中 NoDrop 权重，表示每次掉落尝试产出一件装备的概率；不会改变怪物各自的尝试次数或装备稀有度比例。\n\n“离线可交易特殊掉落倍率”对应 LootConfig.OfflineTradableDropMultiplier。稀有鱼及部分特殊物品候选会使用它，而且这些路径不经过人物的物品发现，所以单独保留。100% 表示 1 倍权重。"));
        Grid.SetRow(_equipment, 1);
        root.Children.Add(_equipment);
        Grid.SetRow(_special, 2);
        root.Children.Add(_special);

        var actions = new WrapPanel();
        var setEquipment = ActionButton("设置装备掉落率…");
        setEquipment.Click += async (_, _) => await RunAsync(SetEquipmentAsync);
        var restoreEquipment = ActionButton("恢复装备掉落规则");
        restoreEquipment.Click += async (_, _) => await RunAsync(() => WriteAsync("equipment-drop-rate", null));
        var setSpecial = ActionButton("设置特殊掉落倍率…");
        setSpecial.Click += async (_, _) => await RunAsync(SetSpecialAsync);
        var restoreSpecial = ActionButton("恢复特殊掉落倍率");
        restoreSpecial.Click += async (_, _) => await RunAsync(() => WriteAsync("offline-tradable-drop-multiplier", null));
        var refresh = ActionButton("刷新状态");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        actions.Children.Add(setEquipment);
        actions.Children.Add(restoreEquipment);
        actions.Children.Add(setSpecial);
        actions.Children.Add(restoreSpecial);
        actions.Children.Add(refresh);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        await ReadSnapshotAsync(() => _adapter.ReadDropRules(Context.Process), settings =>
        {
            _hasOverrides = settings.EquipmentDropRateOverride is not null ||
                            settings.OfflineTradableDropMultiplierOverride is not null;
            _effectiveSpecial = settings.EffectiveOfflineTradableDropMultiplierPercent;
            _equipment.Text = settings.EquipmentDropRateOverride is int rate
                ? $"每次装备掉落尝试：{rate}%（本次运行覆盖）"
                : "每次装备掉落尝试：使用各类怪物的原始 Min/Max 掉落权重";
            _special.Text = settings.OfflineTradableDropMultiplierOverride is int multiplier
                ? $"离线可交易特殊掉落倍率：{multiplier}%（本次运行覆盖）"
                : $"离线可交易特殊掉落倍率：{settings.EffectiveOfflineTradableDropMultiplierPercent}%（游戏当前值）";
            Context.Host.ReportStatus("已读取怪物装备掉落与特殊掉落规则");
        });
    }

    private async Task SetEquipmentAsync()
    {
        var value = await Context.Host.PromptValueAsync(new(
            "设置装备掉落率",
            "输入每次掉落尝试产出装备的概率（1～100）：",
            "100"));
        if (value is not null) await WriteAsync("equipment-drop-rate", value);
    }

    private async Task SetSpecialAsync()
    {
        var value = await Context.Host.PromptValueAsync(new(
            "设置特殊掉落倍率",
            "输入 OfflineTradableDropMultiplier 的最终百分比（100 表示 1 倍；影响稀有鱼及部分特殊物品候选）：",
            _effectiveSpecial.ToString(CultureInfo.InvariantCulture)));
        if (value is not null) await WriteAsync("offline-tradable-drop-multiplier", value);
    }

    private async Task WriteAsync(string field, string? value)
    {
        var key = ModuleFieldKey.Create(
            DeskrawlGameAdapter.DropRulesEditorId,
            DeskrawlGameAdapter.DropRulesEntityId,
            field);
        await WriteFieldAsync(key, value ?? "-1");
        await RefreshAsync();
    }

    public override void Dispose()
    {
        if (_hasOverrides)
        {
            try
            {
                _adapter.RestoreDropRules(Context.Process);
                Context.Host.ReportStatus("掉落规则覆盖已随页面关闭恢复");
            }
            catch (Exception error)
            {
                Context.Host.ReportStatus($"掉落规则覆盖恢复失败：{error.Message}");
            }
        }
        base.Dispose();
    }
}

internal sealed class DeskrawlFishingPage : ModuleEditorPageBase
{
    private readonly DeskrawlGameAdapter _adapter;
    private readonly TextBlock _success = new() { FontSize = 18, Margin = new Thickness(0, 16, 0, 6) };
    private bool _hasOverrides;

    internal DeskrawlFishingPage(DeskrawlGameAdapter adapter, GameEditorPageContext context) : base(context)
    {
        _adapter = adapter;
        var root = GridWithRows(GridLength.Auto, GridLength.Auto, GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        root.Children.Add(Description(
            "空钓概率由地图 NothingChance 和钓鱼技能 SuccessBonus 计算。页面只覆盖最终成功率；游戏每次成功钓鱼固定向背包加入 1 个物品，因此当前构建没有独立的鱼数量参数。人物物品发现只放在“人物属性”；稀有鱼使用的离线特殊倍率放在“掉落规则”。"));
        Grid.SetRow(_success, 1);
        root.Children.Add(_success);

        var actions = new WrapPanel();
        var setSuccess = ActionButton("设置成功率…");
        setSuccess.Click += async (_, _) => await RunAsync(SetSuccessAsync);
        var restoreSuccess = ActionButton("恢复成功率公式");
        restoreSuccess.Click += async (_, _) => await RunAsync(() => WriteAsync("success-rate", null));
        var refresh = ActionButton("刷新状态");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        actions.Children.Add(setSuccess);
        actions.Children.Add(restoreSuccess);
        actions.Children.Add(refresh);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        await ReadSnapshotAsync(() => _adapter.ReadFishingSettings(Context.Process), settings =>
        {
            _hasOverrides = settings.SuccessRateOverride is not null;
            _success.Text = settings.SuccessRateOverride is int success
                ? $"钓鱼最终成功率：{success}%（本次运行覆盖）"
                : "钓鱼最终成功率：使用各地图基础值与钓鱼技能加成公式";
            Context.Host.ReportStatus("已读取钓鱼成功率规则");
        });
    }

    private async Task SetSuccessAsync()
    {
        var value = await Context.Host.PromptValueAsync(new(
            "设置钓鱼成功率",
            "输入最终成功率百分比（0～100）：",
            "100"));
        if (value is not null) await WriteAsync("success-rate", value);
    }

    private async Task WriteAsync(string field, string? value)
    {
        var key = ModuleFieldKey.Create(
            DeskrawlGameAdapter.FishingEditorId,
            DeskrawlGameAdapter.FishingEntityId,
            field);
        await WriteFieldAsync(key, value ?? "-1");
        await RefreshAsync();
    }

    public override void Dispose()
    {
        if (_hasOverrides)
        {
            try
            {
                _adapter.RestoreFishingSettings(Context.Process);
                Context.Host.ReportStatus("钓鱼成功率覆盖已随页面关闭恢复");
            }
            catch (Exception error)
            {
                Context.Host.ReportStatus($"钓鱼成功率覆盖恢复失败：{error.Message}");
            }
        }
        base.Dispose();
    }
}
