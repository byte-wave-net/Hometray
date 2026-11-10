using System.Collections.ObjectModel;
using Hometray.HomeAssistant;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Hometray;

/// <summary>A row of the "Deine Kacheln" list: a tile or a group header (bound by the XAML template).</summary>
public sealed class SelectedTile
{
    public bool IsGroup { get; init; }
    public string EntityId { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    /// <summary>Tile: custom name (empty = use the Home Assistant name). Group: its title.</summary>
    public string Name { get; set; } = "";

    public string Glyph => IsGroup ? "" : ""; // folder / drag handle
    public string Placeholder => IsGroup ? Loc.T("settings.group.placeholder") : FriendlyName;
    public string Caption => IsGroup ? Loc.T("settings.group.caption") : EntityId;

    public Microsoft.UI.Xaml.Media.Brush? RowBrush =>
        IsGroup && Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var b)
            ? b as Microsoft.UI.Xaml.Media.Brush : null;
}

/// <summary>
/// Settings (tray menu → Einstellungen): connection, tile selection, general options.
/// Normal closable window, centered, no taskbar button (tray-only app). Closing only hides it.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int WindowWidthDip = 560;
    private const int MaxPickerRows = 150;

    /// <summary>Picker type filters; the label of "*x" is text "filter.x", of a domain text "domain.&lt;domain&gt;".</summary>
    private static readonly string[] DomainFilters =
    {
        "*common", "*all", "light", "switch", "input_boolean", "cover", "climate", "fan", "lock",
        "media_player", "scene", "script", "button", "automation", "sensor", "binary_sensor", "weather", "person",
    };

    private static string FilterLabel(string key) => Loc.T(key.StartsWith('*') ? "filter." + key[1..] : "domain." + key);

    private readonly App _app = App.Instance;
    private readonly ObservableCollection<SelectedTile> _selected = new();
    private readonly Dictionary<string, CheckBox> _pickerChecks = new();
    private List<HaEntity> _entities = new();
    private bool _updatingUi;
    private bool _closingForExit;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = Loc.T("settings.title");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false; // no taskbar button to restore from
            presenter.IsResizable = false;
        }
        WindowHelper.SetAppIcon(this);
        AppWindow.IsShownInSwitchers = false;

        AppWindow.Closing += (_, e) =>
        {
            if (_closingForExit) return;
            e.Cancel = true;
            AppWindow.Hide();
        };

        if (Content is FrameworkElement root)
        {
            root.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape) AppWindow.Hide();
            };
        }

        // Height follows the content.
        ContentPanel.SizeChanged += (_, e) => ApplyHeight(e.NewSize.Height, center: false);

        _updatingUi = true;
        foreach (string key in DomainFilters)
            DomainBox.Items.Add(new ComboBoxItem { Content = FilterLabel(key), Tag = key });
        DomainBox.SelectedIndex = 0;
        RebuildAreaFilter();

        // language: "automatic" + the eight languages in their own names
        LanguageBox.Items.Add(new ComboBoxItem { Content = Loc.T("settings.language.auto"), Tag = Loc.Auto });
        foreach (var language in Loc.Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = language.Native, Tag = language.Code });
        _updatingUi = false;

        for (int c = AppSettings.MinColumns; c <= AppSettings.MaxColumns; c++)
            ColumnsBox.Items.Add(new ComboBoxItem { Content = c.ToString(), Tag = c });

        SelectedList.ItemsSource = _selected;

        _app.Ha.StatusChanged += OnHaStatusChanged;
        _app.Ha.EntitiesLoaded += OnHaStatusChanged;

        LoadFromSettings();
    }

    internal void ShowAndActivate()
    {
        LoadFromSettings();
        if (!AppWindow.IsVisible)
        {
            double height = ContentPanel.ActualHeight;
            if (height <= 0)
            {
                ContentPanel.Measure(new Windows.Foundation.Size(WindowWidthDip, double.PositiveInfinity));
                height = ContentPanel.DesiredSize.Height;
            }
            ApplyHeight(height, center: true);
        }
        AppWindow.Show();
        Activate();
        WindowHelper.BringToFront(this);
    }

    internal void CloseForExit()
    {
        _closingForExit = true;
        _app.Ha.StatusChanged -= OnHaStatusChanged;
        _app.Ha.EntitiesLoaded -= OnHaStatusChanged;
        Close();
    }

    // ── load / refresh ───────────────────────────────────────────────────────

    private void LoadFromSettings()
    {
        _updatingUi = true;
        try
        {
            UrlBox.Text = AppSettings.BaseUrl;
            TokenBox.Password = "";
            TokenBox.PlaceholderText = Loc.T(AppSettings.IsConfigured ? "settings.token.saved" : "settings.token.paste");

            _selected.Clear();
            foreach (var tile in AppSettings.Tiles)
            {
                if (tile.IsGroupHeader)
                {
                    _selected.Add(new SelectedTile { IsGroup = true, Name = tile.Group ?? "" });
                    continue;
                }
                var entity = _app.Ha.Get(tile.EntityId);
                _selected.Add(new SelectedTile
                {
                    EntityId = tile.EntityId,
                    FriendlyName = entity?.Name ?? tile.EntityId,
                    Name = tile.Name ?? "",
                });
            }

            ColumnsBox.SelectedIndex = AppSettings.Columns - AppSettings.MinColumns;
            LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == AppSettings.Language) ?? LanguageBox.Items[0];
            AlwaysShowToggle.IsOn = AppSettings.AlwaysShowTrayIcon;
            if (AppSettings.IsPackaged)
            {
                // MSIX: the app cannot write explorer's tray settings (its registry is virtualized)
                AlwaysShowToggle.Visibility = Visibility.Collapsed;
                AlwaysShowButton.Visibility = Visibility.Visible;
                AlwaysShowHint.Text = Loc.T("settings.alwaysShow.packagedHint");
            }
            ConfirmToggle.IsOn = AppSettings.ConfirmCriticalActions;
            try { AutostartCheckBox.IsChecked = AppSettings.AutostartEnabled; } catch { }
        }
        finally
        {
            _updatingUi = false;
        }

        NoTilesText.Visibility = _selected.Any(s => !s.IsGroup) ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus();
        RefreshEntities();
    }

    private void OnHaStatusChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closingForExit) return;
        UpdateStatus();
        RefreshEntities();
    });

    private void UpdateStatus()
    {
        var ha = _app.Ha;
        switch (ha.Status)
        {
            case HaStatus.Connected:
                bool insecure = HaClient.NormalizeBaseUri(AppSettings.BaseUrl) is { } saved && HaClient.IsInsecureRemote(saved);
                StatusBar.Severity = insecure ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
                string where = ha.LocationName ?? "Home Assistant";
                int count = ha.Entities.Count();
                StatusBar.Message = (ha.Version is { } v
                                        ? Loc.T("settings.status.connectedVersion", where, v, count)
                                        : Loc.T("settings.status.connected", where, count)) +
                                    (insecure ? Loc.T("settings.status.insecureSuffix") : "");
                break;
            case HaStatus.Connecting:
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.Message = Loc.T("status.connecting");
                break;
            case HaStatus.AuthFailed:
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Message = ha.StatusMessage ?? Loc.T("settings.status.authFailed");
                break;
            case HaStatus.Disconnected:
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Message = Loc.T("settings.status.disconnected", ha.StatusMessage ?? "");
                break;
            default:
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.Message = Loc.T("settings.status.notConfigured");
                break;
        }
    }

    // ── connection ───────────────────────────────────────────────────────────

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        string token = TokenBox.Password.Trim();
        if (token.Length == 0) token = AppSettings.Token; // keep the saved one

        var uri = HaClient.NormalizeBaseUri(url);
        if (uri is null)
        {
            ShowError(Loc.T("settings.err.badUrl"));
            return;
        }
        if (token.Length == 0)
        {
            ShowError(Loc.T("settings.err.noToken"));
            return;
        }

        // plain http outside the home network: the token would be sent unencrypted - make the user decide
        if (HaClient.IsInsecureRemote(uri) && !await ConfirmInsecureAsync(uri))
        {
            ShowError(Loc.T("settings.err.insecureDeclined"));
            return;
        }

        ConnectButton.IsEnabled = false;
        BusyRing.IsActive = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = Loc.T("settings.status.testing");
        try
        {
            string? error = await HaClient.TestAsync(uri.ToString(), token);
            if (error is not null)
            {
                ShowError(Loc.T("settings.err.connectFailed", error));
                return;
            }

            AppSettings.SetConnection(uri.ToString().TrimEnd('/'), token);
            _app.ApplyConnection();
            UrlBox.Text = AppSettings.BaseUrl;
            TokenBox.Password = "";
            TokenBox.PlaceholderText = Loc.T("settings.token.saved");
            UpdateStatus();
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveConn", ex.Message));
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            BusyRing.IsActive = false;
        }
    }

    private async Task<bool> ConfirmInsecureAsync(Uri uri)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("insecure.title"),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("insecure.text", uri.Host) },
            PrimaryButtonText = Loc.T("insecure.proceed"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── tile picker ──────────────────────────────────────────────────────────

    private void RefreshEntities()
    {
        _entities = _app.Ha.Entities.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        // names of already selected tiles may have been unknown before the connection
        foreach (var tile in _selected.Where(s => !s.IsGroup))
            if (_app.Ha.Get(tile.EntityId) is { } entity) tile.FriendlyName = entity.Name;

        RebuildAreaFilter();
        RebuildPicker();
    }

    /// <summary>
    /// Fills the room dropdown from Home Assistant's areas and keeps the current choice.
    /// Without areas (no admin token / none defined) it only offers "Alle Räume" and is disabled.
    /// </summary>
    private void RebuildAreaFilter()
    {
        string? keep = (AreaBox.SelectedItem as ComboBoxItem)?.Tag as string;
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        try
        {
            AreaBox.Items.Clear();
            AreaBox.Items.Add(new ComboBoxItem { Content = Loc.T("area.all"), Tag = "*all" });
            var areas = _app.Ha.Areas;
            foreach (var area in areas)
                AreaBox.Items.Add(new ComboBoxItem { Content = area.Name, Tag = area.Id });
            if (areas.Count > 0)
                AreaBox.Items.Add(new ComboBoxItem { Content = Loc.T("area.none"), Tag = "*none" });

            var match = AreaBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == keep);
            AreaBox.SelectedItem = match ?? AreaBox.Items[0];
            AreaBox.IsEnabled = areas.Count > 0;
        }
        finally
        {
            _updatingUi = wasUpdating;
        }
    }

    private void RebuildPicker()
    {
        string search = SearchBox.Text.Trim();
        string filter = (DomainBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "*common";
        var selectedIds = _selected.Select(s => s.EntityId).ToHashSet();

        IEnumerable<HaEntity> query = _entities;
        query = filter switch
        {
            "*all" => query,
            "*common" => query.Where(e => EntityPresentation.CommonDomains.Contains(e.Domain)),
            _ => query.Where(e => e.Domain == filter),
        };
        string areaFilter = (AreaBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "*all";
        if (areaFilter != "*all")
        {
            query = areaFilter == "*none"
                ? query.Where(e => _app.Ha.AreaIdOf(e.EntityId) is null)
                : query.Where(e => _app.Ha.AreaIdOf(e.EntityId) == areaFilter);
        }
        if (search.Length > 0)
        {
            query = query.Where(e =>
                e.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                e.EntityId.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        var matches = query.ToList();

        PickerList.Items.Clear();
        _pickerChecks.Clear();
        foreach (var entity in matches.Take(MaxPickerRows))
        {
            var check = new CheckBox
            {
                IsChecked = selectedIds.Contains(entity.EntityId),
                Tag = entity.EntityId,
                MinWidth = 0,
                Content = CreatePickerRow(entity, _app.Ha.AreaNameOf(entity.EntityId)),
            };
            check.Checked += PickerCheck_Changed;
            check.Unchecked += PickerCheck_Changed;
            _pickerChecks[entity.EntityId] = check;
            PickerList.Items.Add(new ListViewItem { Content = check, Padding = new Thickness(8, 0, 8, 0) });
        }

        if (_app.Ha.Status != HaStatus.Connected)
            PickerHint.Text = Loc.T("picker.notConnected");
        else if (matches.Count == 0)
            PickerHint.Text = Loc.T("picker.none");
        else if (matches.Count > MaxPickerRows)
            PickerHint.Text = Loc.T("picker.truncated", matches.Count, MaxPickerRows);
        else
            PickerHint.Text = Loc.T("picker.count", matches.Count);
    }

    private static UIElement CreatePickerRow(HaEntity entity, string? areaName)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entity.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var id = new TextBlock
        {
            Text = areaName is null ? entity.EntityId : $"{entity.EntityId} · {areaName}",
            FontSize = 12,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var style) && style is Style s) id.Style = s;
        text.Children.Add(id);
        return text;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingUi) RebuildPicker();
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // both combos fire while InitializeComponent is still creating the other controls
        if (!_updatingUi && SearchBox is not null && DomainBox is not null && AreaBox is not null) RebuildPicker();
    }

    private void PickerCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } check) return;
        if (check.IsChecked == true)
        {
            if (_selected.All(s => s.EntityId != id))
                _selected.Add(new SelectedTile { EntityId = id, FriendlyName = _app.Ha.Get(id)?.Name ?? id });
        }
        else if (_selected.FirstOrDefault(s => s.EntityId == id) is { } tile)
        {
            _selected.Remove(tile);
        }
        PersistTiles();
    }

    // ── selected tiles ───────────────────────────────────────────────────────

    private void AddGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var group = new SelectedTile { IsGroup = true, Name = Loc.T("settings.group.default") };
        _selected.Add(group);
        SelectedList.ScrollIntoView(group);
        PersistTiles();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int direction)
    {
        if (sender is not Button { Tag: SelectedTile row }) return;
        int from = _selected.IndexOf(row), to = from + direction;
        if (from < 0 || to < 0 || to >= _selected.Count) return;
        _selected.Move(from, to);
        PersistTiles();
    }

    private void RemoveTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SelectedTile row }) return;
        _selected.Remove(row);
        if (!row.IsGroup && _pickerChecks.TryGetValue(row.EntityId, out var check))
        {
            // unchecking raises Unchecked → PickerCheck_Changed → PersistTiles (nothing left to remove)
            check.IsChecked = false;
        }
        else
        {
            PersistTiles();
        }
    }

    private void TileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: SelectedTile row } box) return;
        if (row.Name != box.Text)
        {
            row.Name = box.Text;
            PersistTiles();
        }
    }

    private void SelectedList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => PersistTiles();

    private void PersistTiles()
    {
        if (_updatingUi) return;
        try
        {
            AppSettings.SetTiles(_selected.Select(s => s.IsGroup
                ? new TileConfig { Group = s.Name }
                : new TileConfig { EntityId = s.EntityId, Name = s.Name }));
            _app.ApplyTiles();
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveTiles", ex.Message));
        }
        NoTilesText.Visibility = _selected.Any(s => !s.IsGroup) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── general ──────────────────────────────────────────────────────────────

    private void ColumnsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ColumnsBox.SelectedItem is not ComboBoxItem { Tag: int columns }) return;
        try
        {
            AppSettings.Columns = columns;
            _app.ApplyTiles();
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveSetting", ex.Message));
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string setting }) return;
        if (setting == AppSettings.Language) return;
        try
        {
            // saves the choice and rebuilds all windows (including this one) in the new language
            _app.ApplyLanguage(setting);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveSetting", ex.Message));
        }
    }

    private void ConfirmToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        try
        {
            AppSettings.ConfirmCriticalActions = ConfirmToggle.IsOn;
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveSetting", ex.Message));
        }
    }

    private void AlwaysShowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        try
        {
            _app.SetAlwaysShowTrayIcon(AlwaysShowToggle.IsOn);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.saveSetting", ex.Message));
        }
    }

    /// <summary>Store version: the tray icon can only be pinned in the Windows taskbar settings.</summary>
    private void AlwaysShowButton_Click(object sender, RoutedEventArgs e) => WindowHelper.Launch("ms-settings:taskbar");

    private async void AutostartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? error = await AppSettings.SetAutostartAsync(AutostartCheckBox.IsChecked == true);
            if (error is not null) ShowError(error);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("settings.err.autostart", ex.Message));
        }
        // show what Windows really did (the MSIX startup task can be refused by the user / a policy)
        try { AutostartCheckBox.IsChecked = AppSettings.AutostartEnabled; } catch { }
    }

    private void ShowError(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = message;
    }

    // ── size / position ──────────────────────────────────────────────────────

    private void ApplyHeight(double contentHeightDip, bool center)
    {
        if (_closingForExit || contentHeightDip <= 0) return;

        double scale = WindowHelper.GetScale(this);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        int width = (int)(WindowWidthDip * scale);
        int height = (int)Math.Ceiling((32 + contentHeightDip + 2) * scale); // title bar + content + frame
        height = Math.Min(height, area.Height - (int)(48 * scale));

        if (center)
        {
            AppWindow.MoveAndResize(new RectInt32(
                area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2,
                width, height));
        }
        else if (AppWindow.Size.Height != height || AppWindow.Size.Width != width)
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
    }
}
