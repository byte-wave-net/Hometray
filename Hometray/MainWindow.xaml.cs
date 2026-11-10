using Hometray.HomeAssistant;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Hometray;

/// <summary>
/// Tray flyout (left click): the Home Assistant tiles. No title bar, no taskbar button,
/// closes when it loses the focus. Fixed width, height follows the number of tile rows.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int TileWidthDip = 140;
    private const int ScrollBarGutterDip = 10; // keeps the overlay scroll bar off the tiles
    private const int MinWidthDip = 360;
    private const int MinHeightDip = 220;
    private const int MaxHeightDip = 900;

    private readonly App _app = App.Instance;
    private readonly List<EntityTile> _tiles = new();
    private bool _closingForExit;
    private EntityDetail? _detail;      // open detail panel (long press on a tile)
    private string? _detailEntityId;
    private bool _tilesDirty = true;
    private long _autoHiddenAt; // Environment.TickCount64 of the last hide by focus loss
    private bool _activatedSinceShow; // only a real focus loss after activation hides the flyout

    public MainWindow()
    {
        InitializeComponent();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(true, false); // thin border, no title bar
        }
        WindowHelper.UseRoundedCorners(this);
        WindowHelper.SetAppIcon(this);

        // Tray-only app: no taskbar button, not in Alt+Tab.
        AppWindow.IsShownInSwitchers = false;

        // Escape closes the detail panel first, then the flyout.
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            if (_detail is not null) CloseDetail();
            else Hide();
        };

        // Clicking outside the flyout (focus lost) hides it - but only after it really had
        // the focus: a freshly created window reports "Deactivated" while being set up.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _activatedSinceShow = true;
                return;
            }
            if (_closingForExit || !AppWindow.IsVisible || !_activatedSinceShow) return;
            _autoHiddenAt = Environment.TickCount64;
            Hide();
        };

        AppWindow.Closing += (_, e) =>
        {
            if (_closingForExit) return;
            e.Cancel = true;
            Hide();
        };

        _app.Ha.StatusChanged += OnStatusChanged;
        _app.Ha.EntitiesLoaded += OnEntitiesLoaded;
        _app.Ha.EntityChanged += OnEntityChanged;

        UpdateHeader();
        RebuildTiles();
    }

    /// <summary>Window width follows the column count so tile names do not wrap inside a word.</summary>
    private static int WindowWidthDip
    {
        get
        {
            int columns = AppSettings.Columns;
            return Math.Max(MinWidthDip, columns * TileWidthDip + (columns - 1) * 8 + 32 + ScrollBarGutterDip);
        }
    }

    /// <summary>True right after the flyout was hidden because the user clicked outside it
    /// (a click on the tray icon then arrives as tray click and must close, not reopen it).</summary>
    internal bool WasJustAutoHidden => Environment.TickCount64 - _autoHiddenAt < 600;

    internal void Hide()
    {
        _confirmDialog?.Hide(); // a pending question counts as "no"
        AppWindow.Hide();
    }

    // ── confirmation ─────────────────────────────────────────────────────────

    private ContentDialog? _confirmDialog;

    /// <summary>
    /// Asks before a critical action. The dialog lives inside the flyout window (no extra window, so the
    /// flyout keeps the focus). Cancel is the default button; closing / hiding the flyout means "no".
    /// </summary>
    private async Task<bool> ConfirmAsync(Confirmation question)
    {
        if (_confirmDialog is not null || Content.XamlRoot is null) return false; // one question at a time

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = question.Title,
            Content = new TextBlock { Text = question.Message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = question.Button,
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Shows a dialog inside the flyout window (one at a time; hiding the flyout closes it, which counts as
    /// "cancel"). Used for the confirmations and for the input dialog of scripts.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_confirmDialog is not null) return ContentDialogResult.None;
        _confirmDialog = dialog;

        // A dialog cannot be larger than the window. Big ones (script inputs) say how much room they need
        // in Tag (DIP): the flyout grows while the dialog is open.
        if (dialog.Tag is int minHeightDip)
        {
            _dialogMinHeightDip = minHeightDip;
            PlaceAboveTray();
            await Task.Delay(150); // let the new size reach the XAML layout before the dialog measures itself
        }
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _confirmDialog = null;
            if (_dialogMinHeightDip != 0)
            {
                _dialogMinHeightDip = 0;
                if (!_closingForExit) PlaceAboveTray();
            }
        }
    }

    private int _dialogMinHeightDip; // while a big dialog is open

    internal void ShowAndActivate()
    {
        CloseDetail();
        UpdateHeader();
        if (_tilesDirty) RebuildTiles();
        else RefreshAllTiles();
        PlaceAboveTray();

        _activatedSinceShow = false;
        AppWindow.Show();
        Activate();
        WindowHelper.BringToFront(this);

        // A brand-new window may not get the focus on the first attempt.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closingForExit || !AppWindow.IsVisible || _activatedSinceShow) return;
            Activate();
            WindowHelper.BringToFront(this);
        });
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _errorTimer;

    /// <summary>Whether the flyout is on screen (an error is then shown in it, otherwise as tray notification).</summary>
    internal bool IsShown => AppWindow.IsVisible;

    /// <summary>
    /// Shows why an action failed (e.g. Home Assistant refused to start a script) in the flyout itself -
    /// a tray balloon alone is easy to miss and may be switched off in Windows. Closes itself after a while.
    /// </summary>
    internal void ShowError(string title, string message)
    {
        if (_closingForExit) return;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;

        _errorTimer ??= DispatcherQueue.CreateTimer();
        _errorTimer.Stop();
        _errorTimer.Interval = TimeSpan.FromSeconds(10);
        _errorTimer.IsRepeating = false;
        _errorTimer.Tick -= HideErrorBar;
        _errorTimer.Tick += HideErrorBar;
        _errorTimer.Start();
    }

    private void HideErrorBar(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        ErrorBar.IsOpen = false;
    }

    internal void CloseForExit()
    {
        _closingForExit = true;
        _app.Ha.StatusChanged -= OnStatusChanged;
        _app.Ha.EntitiesLoaded -= OnEntitiesLoaded;
        _app.Ha.EntityChanged -= OnEntityChanged;
        Close();
    }

    /// <summary>Tiles or columns were changed in the settings: rebuild now (or on next show).</summary>
    internal void InvalidateTiles()
    {
        _tilesDirty = true;
        if (AppWindow.IsVisible)
        {
            RebuildTiles();
            PlaceAboveTray();
        }
    }

    // ── Home Assistant events (arrive on background threads) ─────────────────

    private void OnStatusChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closingForExit) return;
        if (_app.Ha.Status != HaStatus.Connected) CloseDetail();
        UpdateHeader();
        RefreshAllTiles();
        UpdateEmptyState();
    });

    private void OnEntitiesLoaded() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closingForExit) return;
        UpdateHeader();
        RefreshAllTiles();
    });

    private void OnEntityChanged(string entityId, HaEntity? entity) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closingForExit) return;
        foreach (var tile in _tiles)
            if (tile.EntityId == entityId) tile.Update(entity, Loc.T("tile.missing"));

        if (_detail is not null && entityId == _detailEntityId)
        {
            if (entity is null || entity.IsUnavailable) CloseDetail();
            else _detail.Update(entity);
        }
    });

    // ── detail panel ─────────────────────────────────────────────────────────

    private void OpenDetail(EntityTile tile)
    {
        if (tile.Entity is not { } entity || !EntityDetail.HasControls(entity)) return;
        CloseDetail();

        var detail = new EntityDetail(entity, tile.DisplayName, _app.Ha, _app.ShowErrorToast, ConfirmAsync,
            changeScriptInputs: () =>
            {
                CloseDetail();
                tile.EditScriptInputs();
            },
            forgetScriptInputs: () =>
            {
                tile.ForgetScriptInputs();
                CloseDetail();
            })
        {
            // room for the header and the card's paddings above the scrolling controls
            MaxContentHeight = Math.Max(120, RootGrid.ActualHeight - 150),
        };
        detail.CloseRequested += CloseDetail;
        _detail = detail;
        _detailEntityId = tile.EntityId;
        DetailHost.Content = detail.Root;
        DetailOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDetail()
    {
        if (_detail is null) return;
        _detail = null;
        _detailEntityId = null;
        DetailHost.Content = null;
        DetailOverlay.Visibility = Visibility.Collapsed;
    }

    private void DetailScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => CloseDetail();

    // ── header / empty state ─────────────────────────────────────────────────

    private void UpdateHeader()
    {
        var ha = _app.Ha;
        (string text, string dotKey) = ha.Status switch
        {
            HaStatus.Connected => (ha.LocationName ?? Loc.T("status.connected"), "SystemFillColorSuccessBrush"),
            HaStatus.Connecting => (Loc.T("status.connecting"), "SystemFillColorCautionBrush"),
            HaStatus.AuthFailed => (ha.StatusMessage ?? Loc.T("status.authFailed"), "SystemFillColorCriticalBrush"),
            HaStatus.Disconnected => (ha.StatusMessage ?? Loc.T("status.disconnected"), "SystemFillColorCriticalBrush"),
            _ => (Loc.T("status.notConfigured"), "TextFillColorTertiaryBrush"),
        };
        StatusText.Text = text;
        if (Application.Current.Resources.TryGetValue(dotKey, out var brush) && brush is Brush b)
            StatusDot.Fill = b;
    }

    private void UpdateEmptyState()
    {
        bool hasTiles = _tiles.Count > 0;
        EmptyPanel.Visibility = hasTiles ? Visibility.Collapsed : Visibility.Visible;
        TilesGrid.Visibility = hasTiles ? Visibility.Visible : Visibility.Collapsed;
        if (hasTiles) return;

        if (!AppSettings.IsConfigured)
        {
            EmptyTitle.Text = Loc.T("main.empty.notConnected.title");
            EmptyText.Text = Loc.T("main.empty.notConnected.text");
        }
        else
        {
            EmptyTitle.Text = Loc.T("main.empty.noTiles.title");
            EmptyText.Text = Loc.T("main.empty.noTiles.text");
        }
    }

    // ── tiles ────────────────────────────────────────────────────────────────

    private void RebuildTiles()
    {
        _tilesDirty = false;
        CloseDetail();
        TilesGrid.Children.Clear();
        TilesGrid.ColumnDefinitions.Clear();
        TilesGrid.RowDefinitions.Clear();
        _tiles.Clear();

        int columns = AppSettings.Columns;
        for (int c = 0; c < columns; c++)
            TilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Group headers start a new section (own rows, optional title). A header is only
        // shown once its first tile appears, so empty groups leave no gap.
        int row = -1, col = columns; // col == columns: next tile starts a new row
        bool sectionStart = false;
        string? pendingTitle = null;

        foreach (var config in AppSettings.Tiles)
        {
            if (config.IsGroupHeader)
            {
                col = columns;
                sectionStart = true;
                pendingTitle = config.Group;
                continue;
            }

            if (sectionStart && !string.IsNullOrWhiteSpace(pendingTitle))
            {
                TilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = pendingTitle,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 13,
                    Margin = new Thickness(2, row >= 0 ? 8 : 0, 0, -2),
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(title, ++row);
                Grid.SetColumnSpan(title, columns);
                TilesGrid.Children.Add(title);
            }
            sectionStart = false;

            if (col >= columns)
            {
                TilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row++;
                col = 0;
            }
            var tile = new EntityTile(config, _app.Ha, _app.ShowErrorToast, ConfirmAsync, ShowDialogAsync);
            tile.DetailRequested += OpenDetail;
            Grid.SetRow(tile.Root, row);
            Grid.SetColumn(tile.Root, col++);
            TilesGrid.Children.Add(tile.Root);
            _tiles.Add(tile);
        }

        _app.Ha.SetWatched(_tiles.Select(t => t.EntityId));
        RefreshAllTiles();
        UpdateEmptyState();
    }

    private void RefreshAllTiles()
    {
        string missing = Loc.T(_app.Ha.Status == HaStatus.Connected ? "tile.missing" : "tile.noConnection");
        foreach (var tile in _tiles)
            tile.Update(_app.Ha.Get(tile.EntityId), missing);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettingsWindow();

    private void OpenHa_Click(object sender, RoutedEventArgs e)
    {
        if (HaClient.NormalizeBaseUri(AppSettings.BaseUrl) is { } uri) WindowHelper.Launch(uri.ToString());
        else _app.ShowSettingsWindow();
    }

    // ── size / position (above the notification area) ────────────────────────

    /// <summary>Fixed width, height follows the content (up to the work area), anchored bottom right.</summary>
    private void PlaceAboveTray()
    {
        if (_closingForExit) return;

        double scale = WindowHelper.GetScale(this);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int margin = (int)(12 * scale);

        // content height: header + gap + tiles / empty state (+ paddings), measured at the content width
        int windowWidthDip = WindowWidthDip;
        double contentWidth = windowWidthDip - 32 - ScrollBarGutterDip;
        ContentHost.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
        HeaderGrid.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
        // paddings of RootGrid (14 + 16), row gap (12), window frame (~20: border + rounded-corner insets)
        double desiredDip = 14 + HeaderGrid.DesiredSize.Height + 12 + ContentHost.DesiredSize.Height + 16 + 20;
        desiredDip = Math.Clamp(desiredDip, MinHeightDip, MaxHeightDip);
        desiredDip = Math.Max(desiredDip, _dialogMinHeightDip); // room for an open input dialog

        int width = (int)(windowWidthDip * scale);
        int height = Math.Min((int)(desiredDip * scale), area.Height - 2 * margin);

        var target = new RectInt32(
            area.X + area.Width - width - margin,
            area.Y + area.Height - height - margin,
            width, height);

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        if (pos.X == target.X && pos.Y == target.Y && size.Width == target.Width && size.Height == target.Height)
            return;
        AppWindow.MoveAndResize(target);
    }
}
