using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Ui;

namespace GameValueEditor.Modules.LastEpoch;

internal sealed class LastEpochMaterialsEditorPage : ModuleEditorPageBase
{
    private static readonly string[] CategoryOrder = ["词缀碎片", "符文与雕文", "副本钥匙"];

    private readonly LastEpochGameAdapter _adapter;
    private readonly ObservableCollection<MaterialRow> _rows = [];
    private readonly StackPanel _categories = new() { Orientation = Orientation.Horizontal };
    private readonly DataGrid _grid = new() { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _filter = new() { ToolTip = "按资源名称查找" };
    private readonly TextBlock _empty = new()
    {
        Text = "当前分类没有可显示的资源。",
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private IReadOnlyList<AdapterEditorEntity> _allEntities = [];
    private string _selectedCategory = string.Empty;

    public LastEpochMaterialsEditorPage(LastEpochGameAdapter adapter, GameEditorPageContext context) : base(context)
    {
        _adapter = adapter;
        _filter.TextChanged += (_, _) => BindRows();

        var root = GridWithRows(GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        root.Margin = ModuleVisualResources.PagePadding;

        var header = GridWithColumns(new GridLength(1, GridUnitType.Star), new GridLength(260), GridLength.Auto);
        header.Children.Add(Description("按资源分类直接查看并修改全部项目；未拥有的资源也会显示为 0。"));
        _filter.Margin = ModuleVisualResources.InlineControlSpacing;
        Grid.SetColumn(_filter, 1);
        header.Children.Add(_filter);
        var refresh = ActionButton("刷新资源");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        root.Children.Add(header);

        var categoryBorder = new Border
        {
            Child = _categories,
            Margin = ModuleVisualResources.SectionSpacing,
            Padding = new Thickness(4)
        };
        categoryBorder.SetResourceReference(Border.BackgroundProperty, ModuleVisualResources.PanelRaisedBrush);
        categoryBorder.SetResourceReference(Border.BorderBrushProperty, ModuleVisualResources.BorderBrush);
        categoryBorder.BorderThickness = new Thickness(1);
        categoryBorder.CornerRadius = new CornerRadius(6);
        Grid.SetRow(categoryBorder, 1);
        root.Children.Add(categoryBorder);

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "资源名称",
            Binding = new Binding(nameof(MaterialRow.DisplayName)),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "数量",
            Binding = new Binding(nameof(MaterialRow.QuantityDisplay)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "状态",
            Binding = new Binding(nameof(MaterialRow.Status)),
            Width = new DataGridLength(1.6, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(CreateActionsColumn());
        _grid.ItemsSource = _rows;
        _grid.MouseDoubleClick += async (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) is not null)
                await RunAsync(EditSelectedAsync);
        };
        _grid.AddHandler(Button.ClickEvent, new RoutedEventHandler(RowActionOnClick));

        var content = new Grid();
        content.Children.Add(_grid);
        content.Children.Add(_empty);
        Grid.SetRow(content, 2);
        root.Children.Add(content);
        Root.Content = root;
    }

    protected override async Task LoadAsync()
    {
        var supported = await Task.Run(
            () => _adapter.SupportsEntityEditor(Context.Process, LastEpochGameAdapter.MaterialsEditorId),
            Context.Lifetime);
        ThrowIfExpired();
        if (!supported)
        {
            _empty.Text = "当前游戏构建暂不支持资源页面。";
            _empty.Visibility = Visibility.Visible;
            return;
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var selectedEntityId = (_grid.SelectedItem as MaterialRow)?.Entity.EntityId;
        Context.Host.ReportStatus("正在读取 Last Epoch 资源…");
        _allEntities = await Task.Run(
            () => _adapter.ReadEditorEntities(Context.Process, LastEpochGameAdapter.MaterialsEditorId),
            Context.Lifetime);
        ThrowIfExpired();

        var availableCategories = CategoryOrder.Where(category =>
                _allEntities.Any(entity => string.Equals(entity.Summary, category, StringComparison.Ordinal)))
            .ToList();
        if (!availableCategories.Contains(_selectedCategory, StringComparer.Ordinal))
            _selectedCategory = availableCategories.FirstOrDefault() ?? string.Empty;
        RebuildCategoryButtons(availableCategories);
        BindRows(selectedEntityId);
        Context.Host.ReportStatus($"已读取 {_allEntities.Count:N0} 项资源");
    }

    private void RebuildCategoryButtons(IReadOnlyList<string> categories)
    {
        _categories.Children.Clear();
        foreach (var category in categories)
        {
            var button = new Button { Content = category, Tag = category, MinWidth = 118 };
            if (string.Equals(category, _selectedCategory, StringComparison.Ordinal))
                button.SetResourceReference(Control.BackgroundProperty, ModuleVisualResources.AccentSoftBrush);
            button.Click += (_, _) =>
            {
                _selectedCategory = category;
                RebuildCategoryButtons(categories);
                BindRows();
            };
            _categories.Children.Add(button);
        }
    }

    private void BindRows(string? selectedEntityId = null)
    {
        selectedEntityId ??= (_grid.SelectedItem as MaterialRow)?.Entity.EntityId;
        var filter = _filter.Text.Trim();
        _rows.Clear();
        foreach (var entity in _allEntities.Where(entity =>
                     string.Equals(entity.Summary, _selectedCategory, StringComparison.Ordinal) &&
                     (filter.Length == 0 || entity.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase))))
        {
            var field = entity.Fields.SingleOrDefault(item => string.Equals(item.Key, "quantity", StringComparison.Ordinal));
            if (field is not null) _rows.Add(new MaterialRow(entity, field));
        }
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _grid.SelectedItem = _rows.FirstOrDefault(row => row.Entity.EntityId == selectedEntityId) ?? _rows.FirstOrDefault();
    }

    private async void RowActionOnClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button { CommandParameter: MaterialRow row } button) return;
        _grid.SelectedItem = row;
        if (string.Equals(button.Tag as string, "edit", StringComparison.Ordinal))
            await RunAsync(EditSelectedAsync);
        else if (string.Equals(button.Tag as string, "save", StringComparison.Ordinal))
            await RunAsync(SaveSelectedAsync);
        args.Handled = true;
    }

    private async Task EditSelectedAsync()
    {
        var row = _grid.SelectedItem as MaterialRow ?? throw new InvalidOperationException("请先选择一个资源。");
        if (!row.Field.CanWrite) throw new InvalidOperationException("该资源当前不可修改。");
        var value = await Context.Host.PromptValueAsync(new(
            "修改资源数量",
            $"输入“{row.DisplayName}”的新数量（{row.Field.RangeDisplay}）：",
            row.QuantityDisplay));
        if (value is null) return;
        Context.Host.ReportStatus($"正在修改 {row.DisplayName}…");
        await Task.Run(() => _adapter.WriteField(
            Context.Process,
            ModuleFieldKey.Create(LastEpochGameAdapter.MaterialsEditorId, row.Entity.EntityId, row.Field.Key),
            value), Context.Lifetime);
        await RefreshAsync();
        _grid.SelectedItem = _rows.FirstOrDefault(item => item.Entity.EntityId == row.Entity.EntityId);
    }

    private async Task SaveSelectedAsync()
    {
        var row = _grid.SelectedItem as MaterialRow ?? throw new InvalidOperationException("请先选择一个资源。");
        if (!row.Field.CanWrite) throw new InvalidOperationException("该资源当前不可保存。");
        await Context.Host.SaveFieldAsync(new(
            ModuleFieldKey.Create(LastEpochGameAdapter.MaterialsEditorId, row.Entity.EntityId, row.Field.Key),
            row.DisplayName));
    }

    private static DataGridTemplateColumn CreateActionsColumn()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var edit = new FrameworkElementFactory(typeof(Button));
        edit.SetValue(ContentControl.ContentProperty, "修改…");
        edit.SetValue(FrameworkElement.TagProperty, "edit");
        edit.SetBinding(Button.CommandParameterProperty, new Binding("."));
        panel.AppendChild(edit);

        var save = new FrameworkElementFactory(typeof(Button));
        save.SetValue(ContentControl.ContentProperty, "保存字段…");
        save.SetValue(FrameworkElement.TagProperty, "save");
        save.SetBinding(Button.CommandParameterProperty, new Binding("."));
        panel.AppendChild(save);

        return new DataGridTemplateColumn
        {
            Header = "操作",
            CellTemplate = new DataTemplate { VisualTree = panel },
            Width = DataGridLength.Auto
        };
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private sealed record MaterialRow(AdapterEditorEntity Entity, AdapterEditorField Field)
    {
        public string DisplayName => Entity.DisplayName;
        public string QuantityDisplay => Field.ValueDisplay;
        public string Status => string.IsNullOrWhiteSpace(Field.Status) ? "可修改" : Field.Status;
    }
}
