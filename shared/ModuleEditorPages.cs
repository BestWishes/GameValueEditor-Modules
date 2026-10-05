using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.Ui;

internal abstract class ModuleEditorPageBase : IGameEditorPage
{
    private bool _loaded;
    private bool _disposed;

    protected ModuleEditorPageBase(GameEditorPageContext context)
    {
        Context = context;
        Root = new UserControl();
        Root.Loaded += RootOnLoaded;
    }

    protected GameEditorPageContext Context { get; }
    protected UserControl Root { get; }
    public FrameworkElement View => Root;

    protected abstract Task LoadAsync();

    protected async Task RunAsync(Func<Task> operation)
    {
        if (_disposed || Context.Lifetime.IsCancellationRequested) return;
        try
        {
            Root.IsEnabled = false;
            await operation();
        }
        catch (OperationCanceledException) when (Context.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Context.Host.ShowError("模块页面操作未完成", exception.Message);
        }
        finally
        {
            if (!_disposed) Root.IsEnabled = true;
        }
    }

    protected void ThrowIfExpired()
    {
        Context.Lifetime.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async void RootOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await RunAsync(LoadAsync);
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Root.Loaded -= RootOnLoaded;
        Root.Content = null;
        Root.DataContext = null;
    }

    protected static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };

    protected static Button ActionButton(string text) => new()
    {
        Content = text,
        MinWidth = 108,
        Margin = ModuleVisualResources.InlineControlSpacing
    };

    protected static Grid GridWithRows(params GridLength[] heights)
    {
        var grid = new Grid();
        foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        return grid;
    }

    protected static Grid GridWithColumns(params GridLength[] widths)
    {
        var grid = new Grid();
        foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }
}

internal sealed class InventoryEditorPage : ModuleEditorPageBase
{
    private readonly IInventoryGameAdapter _adapter;
    private readonly ObservableCollection<AdapterInventoryItem> _items = [];
    private readonly ICollectionView _itemsView;
    private readonly DataGrid _grid = new() { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private readonly TextBox _nameFilter = new() { ToolTip = "按物品名称查找" };
    private readonly TextBox _countFilter = new() { ToolTip = "按物品数量精确查找", Margin = ModuleVisualResources.InlineControlSpacing };

    public InventoryEditorPage(IInventoryGameAdapter adapter, GameEditorPageContext context, string description)
        : base(context)
    {
        _adapter = adapter;
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _itemsView.Filter = FilterItem;
        _nameFilter.TextChanged += (_, _) => _itemsView.Refresh();
        _countFilter.TextChanged += (_, _) => _itemsView.Refresh();

        var root = GridWithRows(GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        root.Children.Add(Description(description));

        var filters = GridWithColumns(new GridLength(1, GridUnitType.Star), new GridLength(150), GridLength.Auto);
        Grid.SetRow(filters, 1);
        filters.Margin = new Thickness(0, 8, 0, 0);
        filters.Children.Add(_nameFilter);
        Grid.SetColumn(_countFilter, 1);
        filters.Children.Add(_countFilter);
        var refresh = ActionButton("刷新背包");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        Grid.SetColumn(refresh, 2);
        filters.Children.Add(refresh);
        root.Children.Add(filters);

        _grid.ItemsSource = _itemsView;
        _grid.Margin = ModuleVisualResources.SectionSpacing;
        _grid.Columns.Add(new DataGridTextColumn { Header = "物品名称", Binding = new Binding(nameof(AdapterInventoryItem.DisplayName)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "物品总数", Binding = new Binding(nameof(AdapterInventoryItem.CountDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.MouseDoubleClick += async (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) is not null)
                await RunAsync(EditSelectedAsync);
        };
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = ActionButton("添加到已保存字段…");
        save.Click += async (_, _) => await RunAsync(SaveSelectedAsync);
        var edit = ActionButton("修改选中物品…");
        edit.Click += async (_, _) => await RunAsync(EditSelectedAsync);
        actions.Children.Add(save);
        actions.Children.Add(edit);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override Task LoadAsync() => RefreshAsync();

    private async Task RefreshAsync()
    {
        Context.Host.ReportStatus("正在读取游戏背包…");
        var items = await Task.Run(() => _adapter.ReadInventory(Context.Process), Context.Lifetime);
        ThrowIfExpired();
        _items.Clear();
        foreach (var item in items) _items.Add(item);
        _itemsView.Refresh();
        if (_items.Count > 0) _grid.SelectedIndex = 0;
        Context.Host.ReportStatus($"已读取 {_items.Count:N0} 种背包物品");
    }

    private async Task EditSelectedAsync()
    {
        var selected = _grid.SelectedItems.Cast<AdapterInventoryItem>().ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个背包物品。");
        var prompt = selected.Length == 1
            ? $"输入“{selected[0].DisplayName}”的新物品总数："
            : $"把选中的 {selected.Length:N0} 种物品修改为同一个物品总数：";
        var value = await Context.Host.PromptValueAsync(new(
            selected.Length == 1 ? "修改背包物品数量" : "批量修改背包物品数量",
            prompt,
            selected.Length == 1 ? selected[0].CountDisplay : string.Empty));
        if (value is null) return;
        Context.Host.ReportStatus($"正在修改 {selected.Length:N0} 种背包物品…");
        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                Context.Lifetime.ThrowIfCancellationRequested();
                _adapter.WriteField(Context.Process, item.FieldKey, value);
            }
        }, Context.Lifetime);
        await RefreshAsync();
    }

    private async Task SaveSelectedAsync()
    {
        if (_grid.SelectedItems.Count != 1 || _grid.SelectedItem is not AdapterInventoryItem item)
            throw new InvalidOperationException("添加到已保存字段只支持单选，请只选择一种物品。");
        await Context.Host.SaveFieldAsync(new(item.FieldKey, item.DisplayName));
    }

    private bool FilterItem(object value)
    {
        if (value is not AdapterInventoryItem item) return false;
        var name = _nameFilter.Text.Trim();
        var count = _countFilter.Text.Trim();
        return (name.Length == 0 || item.DisplayName.Contains(name, StringComparison.CurrentCultureIgnoreCase)) &&
               (count.Length == 0 || string.Equals(item.CountDisplay, count, StringComparison.Ordinal));
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
}

internal sealed class CharacterEditorPage : ModuleEditorPageBase
{
    private readonly ICharacterAttributesGameAdapter _adapter;
    private readonly string _editorId;
    private readonly ObservableCollection<AdapterCharacterItem> _characters = [];
    private readonly ListBox _characterList = new();
    private readonly DataGrid _attributeGrid = new() { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _filter = new() { ToolTip = "按人物属性名称查找", Margin = ModuleVisualResources.InlineControlSpacing };
    private ICollectionView? _attributeView;

    public CharacterEditorPage(
        ICharacterAttributesGameAdapter adapter,
        GameEditorPageContext context,
        string editorId,
        string description) : base(context)
    {
        _adapter = adapter;
        _editorId = editorId;
        _filter.TextChanged += (_, _) => _attributeView?.Refresh();

        var root = GridWithRows(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        var header = GridWithColumns(new GridLength(1, GridUnitType.Star), new GridLength(260), GridLength.Auto);
        header.Children.Add(Description(description));
        Grid.SetColumn(_filter, 1);
        header.Children.Add(_filter);
        var refresh = ActionButton("刷新人物");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        root.Children.Add(header);

        var content = GridWithColumns(new GridLength(245), new GridLength(10), new GridLength(1, GridUnitType.Star));
        content.Margin = ModuleVisualResources.SectionSpacing;
        _characterList.ItemsSource = _characters;
        _characterList.DisplayMemberPath = nameof(AdapterCharacterItem.DisplayName);
        _characterList.SelectionChanged += (_, _) => BindAttributes();
        content.Children.Add(_characterList);
        _attributeGrid.Columns.Add(new DataGridTextColumn { Header = "属性", Binding = new Binding(nameof(AdapterCharacterAttribute.DisplayName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _attributeGrid.Columns.Add(new DataGridTextColumn { Header = "基础结果", Binding = new Binding(nameof(AdapterCharacterAttribute.RawValueDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _attributeGrid.Columns.Add(new DataGridTextColumn { Header = "游戏界面总值", Binding = new Binding(nameof(AdapterCharacterAttribute.AggregatedValueDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _attributeGrid.Columns.Add(new DataGridTextColumn { Header = "成长", Binding = new Binding(nameof(AdapterCharacterAttribute.GrowthValueDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _attributeGrid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(AdapterCharacterAttribute.Status)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        _attributeGrid.MouseDoubleClick += async (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) is not null)
                await RunAsync(EditSelectedAsync);
        };
        Grid.SetColumn(_attributeGrid, 2);
        content.Children.Add(_attributeGrid);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = ActionButton("添加到已保存字段…");
        save.Click += async (_, _) => await RunAsync(SaveSelectedAsync);
        var edit = ActionButton("修改选中属性…");
        edit.Click += async (_, _) => await RunAsync(EditSelectedAsync);
        actions.Children.Add(save);
        actions.Children.Add(edit);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override async Task LoadAsync()
    {
        var supported = await Task.Run(() => _adapter.SupportsCharacterAttributes(Context.Process), Context.Lifetime);
        ThrowIfExpired();
        if (!supported)
        {
            Root.Content = new TextBlock
            {
                Text = "当前游戏构建暂不支持这个人物属性页面；其他受支持页面仍可正常使用。",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 620
            };
            return;
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var selectedId = (_characterList.SelectedItem as AdapterCharacterItem)?.CharacterId;
        Context.Host.ReportStatus("正在读取人物属性…");
        var characters = await Task.Run(() => _adapter.ReadCharacters(Context.Process), Context.Lifetime);
        ThrowIfExpired();
        _characters.Clear();
        foreach (var character in characters) _characters.Add(character);
        _characterList.SelectedItem = _characters.FirstOrDefault(item => item.CharacterId == selectedId) ?? _characters.FirstOrDefault();
        Context.Host.ReportStatus($"已读取 {_characters.Count:N0} 个人物");
    }

    private void BindAttributes()
    {
        var attributes = (_characterList.SelectedItem as AdapterCharacterItem)?.Attributes ?? [];
        _attributeView = CollectionViewSource.GetDefaultView(attributes);
        _attributeView.Filter = value => value is AdapterCharacterAttribute attribute &&
            (string.IsNullOrWhiteSpace(_filter.Text) ||
             attribute.DisplayName.Contains(_filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _attributeGrid.ItemsSource = _attributeView;
        _attributeGrid.SelectedItem = attributes.FirstOrDefault(item => item.CanWrite);
    }

    private async Task EditSelectedAsync()
    {
        var character = _characterList.SelectedItem as AdapterCharacterItem
                        ?? throw new InvalidOperationException("请先选择一个人物。");
        var attribute = _attributeGrid.SelectedItem as AdapterCharacterAttribute
                        ?? throw new InvalidOperationException("请先选择一个人物属性。");
        if (!attribute.CanWrite) throw new InvalidOperationException("该属性当前不可修改。");
        var value = await Context.Host.PromptValueAsync(new(
            "修改人物属性",
            $"输入“{character.DisplayName}”的{attribute.DisplayName}目标值：",
            attribute.RawValueDisplay));
        if (value is null) return;
        var fieldKey = ModuleFieldKey.Create(_editorId, character.CharacterId, attribute.Key);
        Context.Host.ReportStatus($"正在修改 {character.DisplayName} 的{attribute.DisplayName}…");
        await Task.Run(() => _adapter.WriteField(Context.Process, fieldKey, value), Context.Lifetime);
        await RefreshAsync();
        _characterList.SelectedItem = _characters.FirstOrDefault(item => item.CharacterId == character.CharacterId);
        _attributeGrid.SelectedItem = (_characterList.SelectedItem as AdapterCharacterItem)?.Attributes
            .FirstOrDefault(item => item.Key == attribute.Key);
    }

    private async Task SaveSelectedAsync()
    {
        var character = _characterList.SelectedItem as AdapterCharacterItem
                        ?? throw new InvalidOperationException("请先选择一个人物。");
        var attribute = _attributeGrid.SelectedItem as AdapterCharacterAttribute
                        ?? throw new InvalidOperationException("请先选择一个人物属性。");
        if (!attribute.CanWrite) throw new InvalidOperationException("该属性当前不可保存。");
        await Context.Host.SaveFieldAsync(new(
            ModuleFieldKey.Create(_editorId, character.CharacterId, attribute.Key),
            $"{character.DisplayName} {attribute.DisplayName}"));
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
}

internal sealed class EntityEditorPage : ModuleEditorPageBase
{
    private readonly IEntityEditorsGameAdapter _adapter;
    private readonly string _editorId;
    private readonly ObservableCollection<AdapterEditorEntity> _entities = [];
    private readonly ListBox _entityList = new();
    private readonly DataGrid _fieldGrid = new() { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBox _filter = new() { ToolTip = "按项目或字段名称查找", Margin = ModuleVisualResources.InlineControlSpacing };
    private readonly TextBlock _empty = new() { Text = "当前没有可显示的项目。", TextAlignment = TextAlignment.Center, FontSize = 17 };
    private readonly ICollectionView _entityView;
    private ICollectionView? _fieldView;

    public EntityEditorPage(
        IEntityEditorsGameAdapter adapter,
        GameEditorPageContext context,
        string editorId,
        string description) : base(context)
    {
        _adapter = adapter;
        _editorId = editorId;
        _entityView = CollectionViewSource.GetDefaultView(_entities);
        _entityView.Filter = FilterEntity;
        _filter.TextChanged += (_, _) => { _entityView.Refresh(); _fieldView?.Refresh(); };

        var root = GridWithRows(GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto);
        root.Margin = ModuleVisualResources.PagePadding;
        var header = GridWithColumns(new GridLength(1, GridUnitType.Star), new GridLength(260), GridLength.Auto);
        header.Children.Add(Description(description));
        Grid.SetColumn(_filter, 1);
        header.Children.Add(_filter);
        var refresh = ActionButton("刷新");
        refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        root.Children.Add(header);

        var content = GridWithColumns(new GridLength(280), new GridLength(10), new GridLength(1, GridUnitType.Star));
        content.Margin = ModuleVisualResources.SectionSpacing;
        _entityList.ItemsSource = _entityView;
        _entityList.DisplayMemberPath = nameof(AdapterEditorEntity.DisplayName);
        _entityList.SelectionChanged += (_, _) => BindFields();
        content.Children.Add(_entityList);
        _fieldGrid.Columns.Add(new DataGridTextColumn { Header = "字段", Binding = new Binding(nameof(AdapterEditorField.DisplayName)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _fieldGrid.Columns.Add(new DataGridTextColumn { Header = "当前值", Binding = new Binding(nameof(AdapterEditorField.ValueDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _fieldGrid.Columns.Add(new DataGridTextColumn { Header = "允许范围", Binding = new Binding(nameof(AdapterEditorField.RangeDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _fieldGrid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(AdapterEditorField.Status)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _fieldGrid.MouseDoubleClick += async (_, args) =>
        {
            if (args.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) is not null)
                await RunAsync(EditSelectedAsync);
        };
        Grid.SetColumn(_fieldGrid, 2);
        content.Children.Add(_fieldGrid);
        Grid.SetColumnSpan(_empty, 3);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(_empty);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = ActionButton("添加到已保存字段…");
        save.Click += async (_, _) => await RunAsync(SaveSelectedAsync);
        var edit = ActionButton("修改选中字段…");
        edit.Click += async (_, _) => await RunAsync(EditSelectedAsync);
        actions.Children.Add(save);
        actions.Children.Add(edit);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Root.Content = root;
    }

    protected override async Task LoadAsync()
    {
        var supported = await Task.Run(() => _adapter.SupportsEntityEditor(Context.Process, _editorId), Context.Lifetime);
        ThrowIfExpired();
        if (!supported)
        {
            _empty.Text = "当前游戏构建暂不支持此页面。";
            _empty.Visibility = Visibility.Visible;
            return;
        }
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var selectedId = (_entityList.SelectedItem as AdapterEditorEntity)?.EntityId;
        Context.Host.ReportStatus("正在读取模块页面数据…");
        var entities = await Task.Run(() => _adapter.ReadEditorEntities(Context.Process, _editorId), Context.Lifetime);
        ThrowIfExpired();
        _entities.Clear();
        foreach (var entity in entities) _entities.Add(entity);
        _entityView.Refresh();
        _empty.Visibility = _entities.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _entityList.SelectedItem = _entities.FirstOrDefault(item => item.EntityId == selectedId) ?? _entities.FirstOrDefault();
        Context.Host.ReportStatus($"已读取 {_entities.Count:N0} 个项目");
    }

    private void BindFields()
    {
        var fields = (_entityList.SelectedItem as AdapterEditorEntity)?.Fields ?? [];
        _fieldView = CollectionViewSource.GetDefaultView(fields);
        _fieldView.Filter = value => value is AdapterEditorField field &&
            (string.IsNullOrWhiteSpace(_filter.Text) ||
             field.DisplayName.Contains(_filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _fieldGrid.ItemsSource = _fieldView;
        _fieldGrid.SelectedItem = fields.FirstOrDefault(item => item.CanWrite);
    }

    private bool FilterEntity(object value)
    {
        if (value is not AdapterEditorEntity entity) return false;
        var filter = _filter.Text.Trim();
        return filter.Length == 0 ||
               entity.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
               entity.Fields.Any(field => field.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
    }

    private async Task EditSelectedAsync()
    {
        var entity = _entityList.SelectedItem as AdapterEditorEntity
                     ?? throw new InvalidOperationException("请先选择一个修改项目。");
        var field = _fieldGrid.SelectedItem as AdapterEditorField
                    ?? throw new InvalidOperationException("请先选择一个可修改字段。");
        if (!field.CanWrite) throw new InvalidOperationException("该字段当前不可修改。");
        var range = string.IsNullOrWhiteSpace(field.RangeDisplay) ? string.Empty : $"（{field.RangeDisplay}）";
        var value = await Context.Host.PromptValueAsync(new(
            "修改模块字段",
            $"输入“{entity.DisplayName}”的{field.DisplayName}目标值{range}：",
            field.ValueDisplay));
        if (value is null) return;
        var fieldKey = ModuleFieldKey.Create(_editorId, entity.EntityId, field.Key);
        Context.Host.ReportStatus($"正在修改 {entity.DisplayName} 的{field.DisplayName}…");
        await Task.Run(() => _adapter.WriteField(Context.Process, fieldKey, value), Context.Lifetime);
        await RefreshAsync();
        _entityList.SelectedItem = _entities.FirstOrDefault(item => item.EntityId == entity.EntityId);
        _fieldGrid.SelectedItem = (_entityList.SelectedItem as AdapterEditorEntity)?.Fields
            .FirstOrDefault(item => item.Key == field.Key);
    }

    private async Task SaveSelectedAsync()
    {
        var entity = _entityList.SelectedItem as AdapterEditorEntity
                     ?? throw new InvalidOperationException("请先选择一个修改项目。");
        var field = _fieldGrid.SelectedItem as AdapterEditorField
                    ?? throw new InvalidOperationException("请先选择一个可修改字段。");
        if (!field.CanWrite) throw new InvalidOperationException("该字段当前不可保存。");
        await Context.Host.SaveFieldAsync(new(
            ModuleFieldKey.Create(_editorId, entity.EntityId, field.Key),
            $"{entity.DisplayName} {field.DisplayName}"));
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
}
