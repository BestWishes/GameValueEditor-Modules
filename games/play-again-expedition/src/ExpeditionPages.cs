using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal abstract class ExpeditionPage : IGameEditorPage
{
    protected readonly ExpeditionGameAdapter Adapter;
    protected readonly GameEditorPageContext Context;
    protected readonly UserControl Root = new();
    protected readonly TextBlock Status = Text("请点击刷新读取当前游戏。", 12);
    private bool _disposed, _loaded, _running;
    protected ExpeditionPage(ExpeditionGameAdapter adapter, GameEditorPageContext context)
    {
        Adapter = adapter; Context = context;
        Root.SetResourceReference(Control.ForegroundProperty, ModuleVisualResources.TextBrush);
        Root.SetResourceReference(Control.BackgroundProperty, ModuleVisualResources.PanelBrush);
        Root.Loaded += Loaded;
        Status.SetResourceReference(TextBlock.ForegroundProperty, ModuleVisualResources.MutedTextBrush);
    }
    public FrameworkElement View => Root;
    protected abstract Task RefreshAsync();
    private async void Loaded(object sender, RoutedEventArgs args)
    { if (_loaded) return; _loaded = true; await Run(RefreshAsync, false); }
    protected void Check() { Context.Lifetime.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); }
    protected async Task Run(Func<Task> action, bool showError = true)
    {
        if (_disposed || Context.Lifetime.IsCancellationRequested || _running) return;
        _running = true;
        Root.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_disposed || Context.Lifetime.IsCancellationRequested) { }
        catch (GameEditorSnapshotChangedException error) { Status.Text = error.Message; }
        catch (Exception error)
        {
            if (_disposed || Context.Lifetime.IsCancellationRequested) return;
            Status.Text = error.Message;
            if (showError) Context.Host.ShowError("模块操作未完成", error.Message);
        }
        finally { _running = false; if (!_disposed && !Context.Lifetime.IsCancellationRequested) Root.IsEnabled = true; }
    }
    protected Task Snapshot<T>(Func<T> read, Action<T> apply)
    {
        Check();
        return Context.Host is IGameEditorSnapshotOperations bridge
            ? bridge.ReadSnapshotAsync(read, value => { Check(); apply(value); })
            : throw new InvalidOperationException("当前主程序缺少安全刷新能力，请更新主程序。");
    }
    protected Task<AdapterFieldValue> Write(string key, string value)
    {
        Check(); return Context.Host is IGameEditorFieldOperations bridge ? bridge.WriteFieldAsync(key, value)
            : throw new InvalidOperationException("当前主程序缺少协调写入能力，请更新主程序。");
    }
    protected static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    protected static Button Button(string title) => new() { Content = title, MinWidth = 96, Margin = ModuleVisualResources.InlineControlSpacing };
    protected static Grid Rows(params GridLength[] heights)
    {
        var grid = new Grid { Margin = ModuleVisualResources.PagePadding };
        foreach (var height in heights) grid.RowDefinitions.Add(new() { Height = height }); return grid;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Root.Loaded -= Loaded; Root.Content = null; Root.DataContext = null;
    }
}

internal sealed class MaterialsPage : ExpeditionPage
{
    private readonly ObservableCollection<MaterialRow> _items = [];
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single,
        MinRowHeight = 30, ColumnHeaderHeight = 32, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBox _search = new() { MinWidth = 180, ToolTip = "按材料名称或分类搜索" };
    private readonly System.ComponentModel.ICollectionView _view;
    internal MaterialsPage(ExpeditionGameAdapter adapter, GameEditorPageContext context) : base(adapter, context)
    {
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = item => item is MaterialRow row && (string.IsNullOrWhiteSpace(_search.Text) ||
            row.Name.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) || row.Category.Contains(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _search.TextChanged += (_, _) => _view.Refresh();
        var root = Rows(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        root.Children.Add(Text("材料数量 · 读取当前存档，修改单项数量并使用游戏自身保存。"));
        var toolbar = new DockPanel { Margin = ModuleVisualResources.SectionSpacing };
        var refresh = Button("刷新"); DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += async (_, _) => await Run(RefreshAsync); toolbar.Children.Add(refresh); toolbar.Children.Add(_search);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        _grid.ItemsSource = _view;
        foreach (var column in new[] { ("材料", "Name", 1.5, 125.0), ("分类", "Category", .7, 65.0), ("当前数量", "Quantity", 1.0, 105.0),
            ("已保存数量", "SavedQuantity", 1.0, 105.0), ("允许范围", "Range", 1.3, 160.0), ("状态", "Status", 2.0, 170.0) })
            _grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), MinWidth = column.Item4, Width = new(column.Item3, DataGridLengthUnitType.Star) });
        _grid.MouseDoubleClick += async (_, args) =>
        { if (ItemsControl.ContainerFromElement(_grid, args.OriginalSource as DependencyObject) is DataGridRow) await Run(EditSelectedAsync); };
        Grid.SetRow(_grid, 2); root.Children.Add(_grid);
        var actions = new DockPanel { Margin = ModuleVisualResources.SectionSpacing };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Button("保存字段…"); save.Click += async (_, _) => await Run(SaveSelectedAsync);
        var edit = Button("修改数量…"); edit.Click += async (_, _) => await Run(EditSelectedAsync);
        buttons.Children.Add(save); buttons.Children.Add(edit); DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons); actions.Children.Add(Status);
        Grid.SetRow(actions, 3); root.Children.Add(actions); Root.Content = root;
    }
    protected override Task RefreshAsync() => Snapshot(() => Adapter.ReadMaterials(Context.Process), snapshot =>
    {
        var id = (_grid.SelectedItem as MaterialRow)?.Id;
        _items.Clear(); foreach (var row in snapshot.Rows) _items.Add(row); _view.Refresh();
        _grid.SelectedItem = _items.FirstOrDefault(row => row.Id == id) ?? _items.FirstOrDefault(row => row.CanWrite);
        Status.Text = $"已读取 {_items.Count} 项材料；可修改 {_items.Count(row => row.CanWrite)} 项。" + snapshot.Receipt.ConnectionWarning;
        Context.Host.ReportStatus(Status.Text);
    });
    private async Task EditSelectedAsync()
    {
        Check(); var row = _grid.SelectedItem as MaterialRow ?? throw new InvalidOperationException("请先选择一项材料。");
        if (!row.CanWrite) throw new InvalidOperationException(row.Status);
        var value = await Context.Host.PromptValueAsync(new("修改材料数量", $"输入“{row.Name}”的目标数量（{row.Range}）。", row.Quantity));
        Check(); if (value is null) return;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) || target < row.Minimum || target > row.Maximum)
            throw new InvalidOperationException("请输入允许范围内的整数数量。");
        await Write(row.FieldKey, target.ToString(CultureInfo.InvariantCulture)); Check(); await RefreshAsync();
    }
    private async Task SaveSelectedAsync()
    {
        Check(); var row = _grid.SelectedItem as MaterialRow ?? throw new InvalidOperationException("请先选择一项材料。");
        if (!row.CanWrite) throw new InvalidOperationException(row.Status);
        await Context.Host.SaveFieldAsync(new(row.FieldKey, row.Name)); Check();
    }
}

internal sealed class WheelPage : ExpeditionPage
{
    private readonly TextBox[] _groups = Enumerable.Range(0, 5).Select(_ => new TextBox()).ToArray();
    private readonly TextBox[] _bonus = [new(), new()];
    private readonly TextBox[] _weights = WheelProfile.WeightKeys.Select(_ => new TextBox()).ToArray();
    private readonly CheckBox _customWeights = new() { Content = "启用类内权重" };
    private readonly Button _apply = Button("应用规则"), _restore = Button("恢复原规则");
    private bool _ready;
    internal WheelPage(ExpeditionGameAdapter adapter, GameEditorPageContext context) : base(adapter, context)
    {
        var root = Rows(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        root.Children.Add(Text("大转盘 · 设置只影响当前游戏会话；奖品、价格和原生结算保持不变。"));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = ModuleVisualResources.SectionSpacing };
        var refresh = Button("刷新"); refresh.Click += async (_, _) => await Run(RefreshAsync);
        _restore.Click += async (_, _) => await Run(RestoreAsync);
        _apply.Click += async (_, _) => await Run(ApplyAsync);
        toolbar.Children.Add(refresh); toolbar.Children.Add(_apply); toolbar.Children.Add(_restore);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var parameters = new StackPanel { Margin = ModuleVisualResources.SectionSpacing };
        parameters.Children.Add(Text("奖励概率（五类合计 100%）"));
        parameters.Children.Add(Inputs(["普通", "不朽", "神话", "装备保护卷", "神圣保护卷"], _groups));
        parameters.Children.Add(Text("额外奖励（两类合计不超过 100%，剩余为无额外奖励）", 12));
        parameters.Children.Add(Inputs(["双倍奖励", "特殊额外奖励"], _bonus));
        _bonus[1].ToolTip = "原生 marquee 奖励分支；不推测为品质升级，保留游戏原本的发奖流程。";
        var weightPanel = new StackPanel(); weightPanel.Children.Add(_customWeights);
        weightPanel.Children.Add(Text("这些是相对权重，不是百分比；默认均为 1，0 表示排除。不存在的奖格不会被新增。", 12));
        weightPanel.Children.Add(Inputs(["装备", "时装", "徽章", "宝石", "材料", "紫色", "橙色", "不朽", "神话"], _weights));
        parameters.Children.Add(new Expander { Header = "类内类型 / 品质权重（可选）", Content = weightPanel });
        var scroll = new ScrollViewer { Content = parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        Status.Margin = ModuleVisualResources.SectionSpacing; Grid.SetRow(Status, 3); root.Children.Add(Status);
        SetProfile(WheelProfile.Default); _apply.IsEnabled = _restore.IsEnabled = false; Root.Content = root;
    }
    private static WrapPanel Inputs(string[] labels, TextBox[] inputs)
    {
        var panel = new WrapPanel();
        for (var index = 0; index < labels.Length; index++)
        {
            var item = new StackPanel { Width = 128, Margin = new(0, 4, 10, 8) };
            item.Children.Add(Text(labels[index], 12)); inputs[index].ToolTip = labels[index] + "：0～100，可使用小数";
            item.Children.Add(inputs[index]); panel.Children.Add(item);
        }
        return panel;
    }
    protected override Task RefreshAsync() => Snapshot(() => Adapter.ReadWheel(Context.Process), snapshot =>
    {
        SetProfile(snapshot.Profile);
        _ready = snapshot.Ready; _apply.IsEnabled = _ready; _restore.IsEnabled = _ready && snapshot.Installed;
        Status.Text = snapshot.Status + snapshot.Receipt.ConnectionWarning; Context.Host.ReportStatus(Status.Text);
    });
    private void SetProfile(WheelProfile profile)
    {
        var groups = profile.Wheel.GroupPercent;
        var values = new[] { groups.Ordinary, groups.Immortal, groups.Mythic, groups.Protection, groups.SacredProtection };
        for (var index = 0; index < 5; index++) _groups[index].Text = Format(values[index]);
        _bonus[0].Text = Format(profile.WheelBonus.DoublePercent); _bonus[1].Text = Format(profile.WheelBonus.MarqueePercent);
        _customWeights.IsChecked = profile.Wheel.TypeWeights is not null;
        for (var index = 0; index < _weights.Length; index++) _weights[index].Text = Format(profile.Wheel.TypeWeights?.GetValueOrDefault(WheelProfile.WeightKeys[index], 1) ?? 1);
    }
    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static double Value(TextBox input)
    {
        if (!double.TryParse(input.Text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException("概率或权重必须是有限数值，小数点请使用“.”。");
        return value;
    }
    private async Task ApplyAsync()
    {
        Check(); if (!_ready) throw new InvalidOperationException("请先刷新并等待游戏空闲。");
        var values = _groups.Select(Value).ToArray();
        var weights = _customWeights.IsChecked == true ? _weights.Select((input, index) => (WheelProfile.WeightKeys[index], Value(input))).ToDictionary(item => item.Item1, item => item.Item2) : null;
        var profile = new WheelProfile(new(new(values[0], values[1], values[2], values[3], values[4]), weights), new(Value(_bonus[0]), Value(_bonus[1])));
        await Write(ExpeditionGameAdapter.WheelKey, profile.ToJson()); Check(); await RefreshAsync();
    }
    private async Task RestoreAsync()
    {
        Check(); if (!_ready) throw new InvalidOperationException("请先刷新并等待游戏空闲。");
        await Write(ExpeditionGameAdapter.WheelKey, "restore"); Check(); await RefreshAsync();
    }
}
