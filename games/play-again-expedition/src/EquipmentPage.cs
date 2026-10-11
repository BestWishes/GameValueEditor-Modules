using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed record EquipmentSnapshot(SessionRead Receipt, bool Installed, bool Ready, bool CanRestore, string Status);

internal sealed class EquipmentPage : ExpeditionPage
{
    internal const string EnabledJson = "{\"enabled\":true}";
    private readonly CheckBox _enabled = new() { Content = "新生成物品的词条数值取满值", Margin = ModuleVisualResources.SectionSpacing };
    private readonly Button _apply = Button("应用规则"), _restore = Button("恢复原规则");
    private bool _ready;
    internal EquipmentPage(ExpeditionGameAdapter adapter, GameEditorPageContext context) : base(adapter, context)
    {
        var root = Rows(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        var toolbar = new WrapPanel(); var refresh = Button("刷新");
        refresh.Click += async (_, _) => await Run(RefreshAsync);
        _apply.Click += async (_, _) => await Run(ApplyAsync);
        _restore.Click += async (_, _) => await Run(RestoreAsync);
        toolbar.Children.Add(refresh); toolbar.Children.Add(_apply); toolbar.Children.Add(_restore); root.Children.Add(toolbar);
        var content = new StackPanel { Margin = ModuleVisualResources.SectionSpacing };
        content.Children.Add(Text("装备、宝石、时装、徽章 · 新生成时取满值"));
        content.Children.Add(_enabled);
        content.Children.Add(Text("只影响之后新生成的武器、防具、宝石、时装和徽章，包括商城生成、宝石打磨、大转盘单抽、十连与额外奖励。现有物品不会被修改。", 12));
        content.Children.Add(Text("取满的是宝石基础属性和物品附加词条的随机数值，包括双数值词条；不改变品质、种类、词条数量、颜色或掉落概率，固定效果仍按游戏原规则计算。", 12));
        content.Children.Add(Text("恢复或重启游戏后，新物品恢复随机取值，已经得到的物品保留。此开关不主动发放物品。", 12));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Status.Margin = ModuleVisualResources.SectionSpacing; Grid.SetRow(Status, 2); root.Children.Add(Status);
        _enabled.Checked += (_, _) => UpdateApply(); _enabled.Unchecked += (_, _) => UpdateApply();
        _apply.IsEnabled = _restore.IsEnabled = false; Root.Content = root;
    }
    internal static void ValidateProfile(string json)
    {
        if (json.Length > 256) throw new InvalidOperationException("装备规则配置过大。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("请启用新生成物品词条满值，或使用恢复原规则。");
    }
    private void UpdateApply() => _apply.IsEnabled = _ready && _enabled.IsChecked == true;
    protected override Task RefreshAsync() => Snapshot(() => Adapter.ReadEquipment(Context.Process), snapshot =>
    {
        _ready = snapshot.Ready; _enabled.IsChecked = snapshot.Installed; UpdateApply();
        _restore.IsEnabled = snapshot.Installed && snapshot.CanRestore;
        Status.Text = snapshot.Status + snapshot.Receipt.ConnectionWarning; Context.Host.ReportStatus(Status.Text);
    });
    private async Task ApplyAsync()
    {
        Check(); if (!_ready || _enabled.IsChecked != true) throw new InvalidOperationException("请先刷新并勾选新生成物品词条满值。");
        await Write(ExpeditionGameAdapter.EquipmentKey, EnabledJson); Check(); await RefreshAsync();
    }
    private async Task RestoreAsync()
    { Check(); await Write(ExpeditionGameAdapter.EquipmentKey, "restore"); Check(); await RefreshAsync(); }
}
