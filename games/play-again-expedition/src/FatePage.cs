using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed record FateProfile(double Blue, double Purple, double Immortal, double Mythic)
{
    internal static readonly string[] Keys = ["blue", "purple", "immortal", "mythic"];
    internal static FateProfile Default => new(70, 25, 4, 1);
    internal double[] Values => [Blue, Purple, Immortal, Mythic];
    internal void Validate()
    {
        if (Values.Any(value => !double.IsFinite(value) || value < 0 || value > 100) || Math.Abs(Values.Sum() - 100) > 1e-8)
            throw new InvalidOperationException("四档命格概率必须在 0～100% 内，合计为 100%。");
    }
    internal string ToJson() { Validate(); return JsonSerializer.Serialize(this, WheelProfile.JsonOptions); }
    internal static FateProfile Parse(string json)
    {
        if (json.Length > 1024) throw new InvalidOperationException("命格概率配置过大。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
            root.EnumerateObject().Any(property => !Keys.Contains(property.Name, StringComparer.Ordinal)) ||
            !Keys.All(key => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number))
            throw new InvalidOperationException("请填写完整的四档命格概率。");
        var result = new FateProfile(root.GetProperty("blue").GetDouble(), root.GetProperty("purple").GetDouble(),
            root.GetProperty("immortal").GetDouble(), root.GetProperty("mythic").GetDouble());
        result.Validate(); return result;
    }
}
internal sealed record FateSnapshot(SessionRead Receipt, FateProfile Profile, FateProfile Original, bool Installed, bool Ready, string Status, bool CanRestore = true);

internal sealed class FatePage : ExpeditionPage
{
    private readonly TextBox[] _values = Enumerable.Range(0, 4).Select(_ => new TextBox { MinWidth = 100 }).ToArray();
    private readonly TextBlock[] _original = Enumerable.Range(0, 4).Select(_ => Text("原概率：—", 12)).ToArray();
    private readonly Button _apply = Button("应用规则"), _restore = Button("恢复原规则");
    private bool _ready;
    internal FatePage(ExpeditionGameAdapter adapter, GameEditorPageContext context) : base(adapter, context)
    {
        var root = Rows(GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        var toolbar = new WrapPanel(); var refresh = Button("刷新");
        refresh.Click += async (_, _) => await Run(RefreshAsync);
        _apply.Click += async (_, _) => await Run(ApplyAsync);
        _restore.Click += async (_, _) => await Run(RestoreAsync);
        toolbar.Children.Add(refresh); toolbar.Children.Add(_apply); toolbar.Children.Add(_restore); root.Children.Add(toolbar);
        var content = new StackPanel { Margin = ModuleVisualResources.SectionSpacing };
        content.Children.Add(Text("命格抽取概率 · 单抽与十连抽共用规则"));
        content.Children.Add(Text("调整普通抽取的四档概率，合计 100%。保留游戏百抽神话保底、角色限定和正常消耗；不修改已有命格或待选结果。", 12));
        var inputs = new WrapPanel(); var names = new[] { "蓝色", "紫色", "不朽", "神话" };
        for (var i = 0; i < 4; i++)
        {
            var item = new StackPanel { Width = 142, Margin = new(0, 12, 16, 12) };
            item.Children.Add(Text(names[i] + "（%）", 12));
            _values[i].ToolTip = names[i] + "普通抽取概率：0～100%，支持小数";
            item.Children.Add(_values[i]); _original[i].SetResourceReference(TextBlock.ForegroundProperty, ModuleVisualResources.MutedTextBrush);
            item.Children.Add(_original[i]); inputs.Children.Add(item);
        }
        content.Children.Add(inputs);
        content.Children.Add(Text("仅当前游戏会话有效，重启恢复原概率。恢复原规则不会撤销抽取结果，也不会改变大转盘或掉落规则。", 12));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Status.Margin = ModuleVisualResources.SectionSpacing; Grid.SetRow(Status, 2); root.Children.Add(Status);
        _apply.IsEnabled = _restore.IsEnabled = false; Root.Content = root;
    }
    protected override Task RefreshAsync() => Snapshot(() => Adapter.ReadFate(Context.Process), snapshot =>
    {
        for (var i = 0; i < 4; i++)
        {
            _values[i].Text = snapshot.Profile.Values[i].ToString("0.########", CultureInfo.InvariantCulture);
            _original[i].Text = "原概率：" + snapshot.Original.Values[i].ToString("0.########", CultureInfo.InvariantCulture) + "%";
        }
        _ready = snapshot.Ready; _apply.IsEnabled = _ready; _restore.IsEnabled = snapshot.CanRestore && snapshot.Installed;
        Status.Text = snapshot.Status + snapshot.Receipt.ConnectionWarning; Context.Host.ReportStatus(Status.Text);
    });
    private async Task ApplyAsync()
    {
        Check(); if (!_ready) throw new InvalidOperationException("请先刷新并等待当前游戏操作结束。");
        var values = _values.Select(input => double.TryParse(input.Text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        var profile = new FateProfile(values[0], values[1], values[2], values[3]);
        await Write(ExpeditionGameAdapter.FateKey, profile.ToJson()); Check(); await RefreshAsync();
    }
    private async Task RestoreAsync()
    { Check(); await Write(ExpeditionGameAdapter.FateKey, "restore"); Check(); await RefreshAsync(); }
}
