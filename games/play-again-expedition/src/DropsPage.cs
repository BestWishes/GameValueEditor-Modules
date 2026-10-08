using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.PlayAgainExpedition;

internal sealed class DropEditRow : INotifyPropertyChanged
{
    internal DropOption Option { get; }
    public string Name => Option.Name;
    public string Help => Option.Help;
    public string Range => $"{Option.Minimum}～{Option.Maximum}";
    private bool _enabled; private string _value; private string _original = "未读取";
    public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
    public string Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    public string Original { get => _original; set { _original = value; PropertyChanged?.Invoke(this, new(nameof(Original))); } }
    internal DropEditRow(DropOption option) { Option = option; _value = DropProfile.Format(option.Default); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class DropsPage : ExpeditionPage
{
    private readonly ObservableCollection<DropEditRow> _items = new(DropProfile.Options.Select(option => new DropEditRow(option)));
    private readonly Button _apply = Button("应用规则"), _restore = Button("恢复原规则");
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
    private bool _ready;
    internal DropsPage(ExpeditionGameAdapter adapter, GameEditorPageContext context) : base(adapter, context)
    {
        _apply.IsEnabled = _restore.IsEnabled = false;
        var root = Rows(GridLength.Auto, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        root.Children.Add(Text("掉落 · 勾选要修改的项目；未勾选保持原规则。修改只在本次游戏会话有效。"));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = ModuleVisualResources.SectionSpacing };
        var refresh = Button("刷新"); refresh.Click += async (_, _) => await Run(RefreshAsync);
        _apply.Click += async (_, _) => await Run(ApplyAsync); _restore.Click += async (_, _) => await Run(RestoreAsync);
        toolbar.Children.Add(refresh); toolbar.Children.Add(_apply); toolbar.Children.Add(_restore); Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var sections = new StackPanel();
        foreach (var group in DropProfile.Options.Select(option => option.Group).Distinct())
        {
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, MinRowHeight = 34, ItemsSource = _items.Where(item => item.Option.Group == group).ToArray() };
            var toggle = new FrameworkElementFactory(typeof(CheckBox)); toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(DropEditRow.Enabled)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn { Header = "启用", CellTemplate = new DataTemplate { VisualTree = toggle }, Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "项目", Binding = new Binding(nameof(DropEditRow.Name)), IsReadOnly = true, Width = new(2, DataGridLengthUnitType.Star), MinWidth = 150 });
            var factory = new FrameworkElementFactory(typeof(TextBox)); factory.SetBinding(TextBox.TextProperty, new Binding(nameof(DropEditRow.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            factory.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(DropEditRow.Enabled))); factory.SetValue(FrameworkElement.MinWidthProperty, 75.0);
            grid.Columns.Add(new DataGridTemplateColumn { Header = group == "总加成" ? "增减百分点" : group == "奶牛奖池权重" ? "相对权重" : "目标概率（%）", CellTemplate = new DataTemplate { VisualTree = factory }, Width = 120 });
            grid.Columns.Add(new DataGridTextColumn { Header = "原规则", Binding = new Binding(nameof(DropEditRow.Original)), IsReadOnly = true, Width = new(1, DataGridLengthUnitType.Star), MinWidth = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "范围", Binding = new Binding(nameof(DropEditRow.Range)), IsReadOnly = true, Width = 90 });
            var style = new Style(typeof(DataGridRow)); style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(DropEditRow.Help)))); grid.RowStyle = style;
            if (group == "奶牛奖池权重") sections.Children.Add(new Expander { Header = group + "（可选）", Content = grid, Margin = ModuleVisualResources.SectionSpacing });
            else sections.Children.Add(new GroupBox { Header = group == "矿石品质" ? "矿石品质（四项一起启用，合计 100%）" : group, Content = grid, Margin = ModuleVisualResources.SectionSpacing });
        }
        _scroll.Content = sections; _scroll.PreviewMouseWheel += ScrollWheel;
        Grid.SetRow(_scroll, 2); root.Children.Add(_scroll);
        Status.Margin = ModuleVisualResources.SectionSpacing; Grid.SetRow(Status, 3); root.Children.Add(Status); Root.Content = root;
    }
    private void ScrollWheel(object sender, MouseWheelEventArgs args)
    {
        if (!Root.IsEnabled || Context.Lifetime.IsCancellationRequested || Keyboard.Modifiers != ModifierKeys.None ||
            args.Delta == 0 || _scroll.ScrollableHeight <= 0) return;
        // Handle the tunnel event before nested grids/text inputs can swallow it.
        // Do not forward a second event: scrolling must never edit a value.
        _scroll.ScrollToVerticalOffset(CalculateWheelOffset(_scroll.VerticalOffset, _scroll.ExtentHeight,
            _scroll.ViewportHeight, args.Delta, SystemParameters.WheelScrollLines));
        args.Handled = true;
    }
    internal static double CalculateWheelOffset(double offset, double extent, double viewport, int delta, int lines)
    {
        var maximum = Math.Max(0, extent - viewport);
        var step = lines == -1 ? viewport : Math.Max(0, lines) * 16.0;
        return Math.Clamp(Math.Clamp(offset, 0, maximum) - delta / 120.0 * step, 0, maximum);
    }
    protected override Task RefreshAsync() => Snapshot(() => Adapter.ReadDrops(Context.Process), read =>
    {
        foreach (var item in _items)
        {
            item.Enabled = read.Profile.TryGet(item.Option.Path, out var value);
            item.Value = DropProfile.Format(item.Enabled ? value : item.Option.Default);
            item.Original = read.NativeValues.GetValueOrDefault(item.Option.Path, "需在对应模式读取");
        }
        _ready = read.Ready; _apply.IsEnabled = _restore.IsEnabled = _ready; Status.Text = read.Status + read.Receipt.ConnectionWarning;
    });
    private async Task ApplyAsync()
    {
        Check(); if (!_ready) throw new InvalidOperationException("请先刷新并停止战斗，确认接口可用。");
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in _items.Where(item => item.Enabled))
        {
            if (!double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException($"“{item.Name}”请输入有限数值。");
            values.Add(item.Option.Path, value);
        }
        var profile = new DropProfile(values); if (profile.IsEmpty) throw new InvalidOperationException("请启用至少一项，或使用恢复原规则。");
        await Write(ExpeditionGameAdapter.DropsKey, profile.ToJson()); Check(); await RefreshAsync();
    }
    private async Task RestoreAsync()
    { Check(); if (!_ready) throw new InvalidOperationException("请先刷新并停止战斗。"); await Write(ExpeditionGameAdapter.DropsKey, "restore"); Check(); await RefreshAsync(); }
}
