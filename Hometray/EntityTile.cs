using Hometray.HomeAssistant;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hometray;

/// <summary>
/// One tile of the flyout: icon, name, status and (per entity type) a primary action on click
/// plus an optional control strip - brightness slider, cover / media buttons, thermostat +/-.
/// Built in code like the rest of the flyout rows; <see cref="Update"/> applies a new state.
/// </summary>
internal sealed class EntityTile
{
    private const int FeatureCoverOpen = 1, FeatureCoverClose = 2, FeatureCoverStop = 8;
    private const int FeatureMediaPause = 1, FeatureMediaPrev = 16, FeatureMediaNext = 32, FeatureMediaPlay = 16384;
    private const int FeatureClimateTargetTemp = 1;

    private readonly HaClient _ha;
    private readonly Action<string, string> _showError;
    private readonly Func<Confirmation, Task<bool>> _confirm;
    private readonly Func<ContentDialog, Task<ContentDialogResult>> _showDialog;
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();

    private readonly Border _root = new();
    private readonly Button _main = new();
    private readonly Border _iconCircle = new();
    private readonly FontIcon _icon = new();
    private readonly TextBlock _name = new();
    private readonly TextBlock _state = new();
    private readonly ContentPresenter _strip = new();

    private HaEntity? _entity;
    private string _stripKey = "";
    private bool _updating;
    private string _missingText = Loc.T("tile.noConnection");

    // strip controls (only the ones of the current strip are non-null)
    private Slider? _brightness;
    private FontIcon? _playPauseIcon;
    private TextBlock? _targetText;
    private DispatcherQueueTimer? _debounce;
    private double? _pendingBrightness;
    private double? _pendingTarget;
    private bool _interacting; // slider held: do not overwrite it with incoming states

    private const int LongPressMilliseconds = 1000;
    private const double LongPressMoveTolerance = 12; // px; moving further (touch scroll, drag) is not a press-and-hold

    private readonly DispatcherQueueTimer _pressTimer;
    private Windows.Foundation.Point _pressPoint;
    private bool _longPressFired; // swallows the Click that follows the release of a long press

    public string EntityId { get; }
    public string? CustomName { get; }
    public FrameworkElement Root => _root;
    internal HaEntity? Entity => _entity;
    internal string DisplayName => CustomName ?? _entity?.Name ?? EntityId;

    /// <summary>Long press (1 s) or right click: the detail panel with the device's further settings is wanted.</summary>
    public event Action<EntityTile>? DetailRequested;

    private bool HasDetail => _entity is { } e && EntityDetail.HasControls(e);

    public EntityTile(TileConfig config, HaClient ha, Action<string, string> showError, Func<Confirmation, Task<bool>> confirm,
        Func<ContentDialog, Task<ContentDialogResult>> showDialog)
    {
        EntityId = config.EntityId;
        CustomName = config.Name;
        _ha = ha;
        _showError = showError;
        _confirm = confirm;
        _showDialog = showDialog;

        // ── layout ──
        _iconCircle.Width = _iconCircle.Height = 32;
        _iconCircle.CornerRadius = new CornerRadius(16);
        _iconCircle.HorizontalAlignment = HorizontalAlignment.Left;
        _icon.FontSize = 16;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _iconCircle.Child = _icon;

        _name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _name.FontSize = 13;
        _name.MaxLines = 2;
        _name.TextWrapping = TextWrapping.Wrap;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;

        _state.FontSize = 12;
        _state.MaxLines = 1;
        _state.TextTrimming = TextTrimming.CharacterEllipsis;

        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(_iconCircle);
        var texts = new StackPanel { Spacing = 0 };
        texts.Children.Add(_name);
        texts.Children.Add(_state);
        content.Children.Add(texts);

        _main.Content = content;
        _main.HorizontalAlignment = HorizontalAlignment.Stretch;
        _main.VerticalAlignment = VerticalAlignment.Stretch;
        _main.HorizontalContentAlignment = HorizontalAlignment.Left;
        _main.VerticalContentAlignment = VerticalAlignment.Top;
        _main.Padding = new Thickness(10, 10, 10, 8);
        _main.Background = new SolidColorBrush(Colors.Transparent);
        _main.BorderThickness = new Thickness(0);
        _main.CornerRadius = new CornerRadius(8);
        _main.Click += (_, _) =>
        {
            if (_longPressFired)
            {
                _longPressFired = false;
                return;
            }
            PrimaryAction();
        };

        // Long press: timer started on press, cancelled by release / leaving / moving away.
        // The button marks pointer events as handled -> handledEventsToo.
        _pressTimer = _dq.CreateTimer();
        _pressTimer.Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds);
        _pressTimer.IsRepeating = false;
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer.Stop();
            if (!HasDetail) return;
            _longPressFired = true;
            DetailRequested?.Invoke(this);
        };
        _main.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        _main.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        foreach (var cancelEvent in new[]
        {
            UIElement.PointerReleasedEvent, UIElement.PointerExitedEvent,
            UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent,
        })
            _main.AddHandler(cancelEvent, new PointerEventHandler((_, _) => _pressTimer.Stop()), handledEventsToo: true);

        // Right click opens the panel at once. A touch long press also raises RightTapped (earlier
        // than 1 s) - that one is ignored so touch and mouse behave the same.
        _main.RightTapped += (_, e) =>
        {
            if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch || !HasDetail) return;
            e.Handled = true;
            DetailRequested?.Invoke(this);
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_main);
        Grid.SetRow(_strip, 1);
        _strip.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _strip.Margin = new Thickness(6, 0, 6, 6);
        grid.Children.Add(_strip);

        _root.Child = grid;
        _root.CornerRadius = new CornerRadius(8);
        _root.BorderThickness = new Thickness(1);
        _root.ActualThemeChanged += (_, _) => Update(_entity);

        Update(null);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _longPressFired = false;
        if (!HasDetail) return;
        var point = e.GetCurrentPoint(_main);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        _pressPoint = point.Position;
        _pressTimer.Stop();
        _pressTimer.Start();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressTimer.IsRunning) return;
        var p = e.GetCurrentPoint(_main).Position;
        if (Math.Abs(p.X - _pressPoint.X) > LongPressMoveTolerance || Math.Abs(p.Y - _pressPoint.Y) > LongPressMoveTolerance)
            _pressTimer.Stop();
    }

    // ── state → UI ───────────────────────────────────────────────────────────

    /// <param name="missingText">Status shown while there is no entity (not connected yet / not in Home Assistant).</param>
    public void Update(HaEntity? entity, string? missingText = null)
    {
        _entity = entity;
        if (missingText is not null) _missingText = missingText;
        _updating = true;
        try
        {
            _root.Background = Brush("CardBackgroundFillColorDefaultBrush");
            _root.BorderBrush = Brush("CardStrokeColorDefaultBrush");

            _name.Text = DisplayName;
            ToolTipService.SetToolTip(_main, $"{DisplayName}\n{EntityId}" +
                (HasDetail ? "\n" + Loc.T("tile.hint.longPress") : ""));

            if (entity is null)
            {
                _state.Text = _missingText;
                ApplyVisual(active: false, color: null, glyph: "", faded: true);
                SetStrip(null);
                _main.IsHitTestVisible = false;
                return;
            }

            _state.Text = EntityPresentation.StateText(entity);
            ApplyVisual(EntityPresentation.IsActive(entity), EntityPresentation.LightColor(entity), EntityPresentation.Glyph(entity), entity.IsUnavailable);

            // clickable when it has a primary action or (helpers, sensors with settings) a detail panel
            bool interactive = EntityPresentation.IsActionable(entity) || HasDetail;
            _main.IsHitTestVisible = interactive;
            _main.IsTabStop = interactive;

            SetStrip(entity);
            UpdateStrip(entity);
        }
        finally
        {
            _updating = false;
        }
    }

    private void ApplyVisual(bool active, Color? color, string glyph, bool faded)
    {
        _icon.Glyph = glyph;
        if (active)
        {
            if (color is Color c)
            {
                _iconCircle.Background = new SolidColorBrush(c);
                double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                _icon.Foreground = new SolidColorBrush(luminance > 0.6 ? Colors.Black : Colors.White);
            }
            else
            {
                _iconCircle.Background = Brush("AccentFillColorDefaultBrush");
                _icon.Foreground = Brush("TextOnAccentFillColorPrimaryBrush");
            }
        }
        else
        {
            _iconCircle.Background = Brush("SubtleFillColorSecondaryBrush");
            _icon.Foreground = Brush("TextFillColorPrimaryBrush");
        }
        _state.Foreground = Brush("TextFillColorSecondaryBrush");
        _root.Opacity = faded ? 0.55 : 1;
    }

    internal static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Gray);

    // ── primary action (click on icon / name) ────────────────────────────────

    private void PrimaryAction()
    {
        if (_entity is not { } e || e.IsUnavailable) return;

        // nothing to trigger (number / select helpers): the click opens the settings instead
        if (!EntityPresentation.IsActionable(e))
        {
            if (HasDetail) DetailRequested?.Invoke(this);
            return;
        }

        if (e.Domain == "script")
        {
            RunScript();
            return;
        }

        (string domain, string service) = e.Domain switch
        {
            "lock" => ("lock", e.State == "locked" ? "unlock" : "lock"),
            "scene" => ("scene", "turn_on"),
            "button" => ("button", "press"),
            "input_button" => ("input_button", "press"),
            "group" => ("homeassistant", "toggle"),
            "media_player" when e.Supports(FeatureMediaPause) || e.Supports(FeatureMediaPlay) => ("media_player", "media_play_pause"),
            _ => (e.Domain, "toggle"),
        };
        Call(domain, service);
    }

    /// <summary>
    /// Starts the script. If it has input fields (the "fields" of the script), a dialog asks for them first and
    /// the values are passed as the script's variables; scripts without fields start right away.
    /// </summary>
    private async void RunScript(bool askAgain = false)
    {
        try
        {
            IReadOnlyList<ScriptField> fields;
            try
            {
                fields = await _ha.GetScriptFieldsAsync(EntityId);
            }
            catch (HaException ex)
            {
                // not starting is safer than starting a script that needs inputs without them
                _showError(DisplayName, Loc.T("script.err.fields", ex.Message));
                return;
            }

            Dictionary<string, object>? variables = null;
            if (fields.Count > 0)
            {
                // Remembered inputs start the script without a dialog - as long as they still cover every required
                // field (the script may have got a new one since). Otherwise the dialog asks, filled in with them.
                var saved = AppSettings.GetScriptInputs(EntityId);
                if (!askAgain && saved is not null && fields.All(f => !f.Required || saved.ContainsKey(f.Key)))
                {
                    variables = new Dictionary<string, object>();
                    foreach (var field in fields)
                        if (saved.TryGetPropertyValue(field.Key, out var node) && node is not null)
                            variables[field.Key] = node.DeepClone();
                }
                else
                {
                    if (_root.XamlRoot is null) return;
                    var answer = await ScriptInput.AskAsync(_root.XamlRoot, _showDialog, DisplayName, fields, _ha, saved);
                    if (answer is null) return; // cancelled
                    variables = answer.Values;

                    if (answer.Remember)
                        AppSettings.SetScriptInputs(EntityId, System.Text.Json.JsonSerializer.SerializeToNode(answer.Values)!.AsObject());
                    else if (saved is not null)
                        AppSettings.SetScriptInputs(EntityId, null); // the box was unticked: ask every time again
                    Update(_entity); // the long-press panel (change / forget) depends on this
                }
            }

            await _ha.CallServiceAsync("script", "turn_on", EntityId,
                variables is { Count: > 0 } ? new Dictionary<string, object> { ["variables"] = variables } : null);
            ShowAccepted();
        }
        catch (Exception ex)
        {
            _showError(DisplayName, ex.Message);
        }
    }

    /// <summary>"Change" in the long-press panel: asks for the inputs again (filled in) and runs the script.</summary>
    internal void EditScriptInputs() => RunScript(askAgain: true);

    /// <summary>"Forget" in the long-press panel: the next click asks for the inputs again.</summary>
    internal void ForgetScriptInputs()
    {
        AppSettings.SetScriptInputs(EntityId, null);
        Update(_entity);
    }

    private async void Call(string domain, string service, Dictionary<string, object>? data = null)
    {
        try
        {
            // unlocking / moving a garage door etc. asks first (see EntityPresentation.ConfirmationFor)
            if (_entity is { } entity &&
                EntityPresentation.ConfirmationFor(entity, domain, service, DisplayName) is { } question &&
                !await _confirm(question))
                return;

            await _ha.CallServiceAsync(domain, service, EntityId, data);
            if (domain is "script" or "scene" or "button" or "input_button") ShowAccepted();
        }
        catch (Exception ex)
        {
            _showError(DisplayName, ex.Message);
        }
    }

    private DispatcherQueueTimer? _acceptedTimer;

    /// <summary>
    /// Home Assistant accepted a "run" action. Scripts, scenes and buttons show no lasting state change, so the
    /// icon turns into a check mark for a moment - otherwise a click looks like nothing happened.
    /// </summary>
    private void ShowAccepted()
    {
        _icon.Glyph = ""; // CheckMark
        _iconCircle.Background = Brush("AccentFillColorDefaultBrush");
        _icon.Foreground = Brush("TextOnAccentFillColorPrimaryBrush");

        if (_acceptedTimer is null)
        {
            _acceptedTimer = _dq.CreateTimer();
            _acceptedTimer.Interval = TimeSpan.FromMilliseconds(1200);
            _acceptedTimer.IsRepeating = false;
            _acceptedTimer.Tick += (_, _) =>
            {
                _acceptedTimer.Stop();
                Update(_entity);
            };
        }
        _acceptedTimer.Stop();
        _acceptedTimer.Start();
    }

    // ── control strip ────────────────────────────────────────────────────────

    /// <summary>Builds the strip for the entity (again only when its kind / features changed).</summary>
    private void SetStrip(HaEntity? e)
    {
        string key = StripKey(e);
        if (key == _stripKey) return;
        _stripKey = key;

        _brightness = null;
        _playPauseIcon = null;
        _targetText = null;
        _debounce?.Stop();
        _debounce = null;

        _strip.Content = key switch
        {
            "brightness" => BuildBrightness(),
            _ when key.StartsWith("cover") => BuildCover(e!),
            _ when key.StartsWith("media") => BuildMedia(e!),
            "climate" => BuildClimate(),
            _ => null,
        };
        _strip.Visibility = _strip.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Identifies the kind of strip (including the feature set), so it is only rebuilt when that changes.</summary>
    private static string StripKey(HaEntity? e)
    {
        if (e is null) return "";
        int coverBits = e.GetInt("supported_features") & (FeatureCoverOpen | FeatureCoverClose | FeatureCoverStop);
        return e.Domain switch
        {
            "light" when SupportsBrightness(e) => "brightness",
            "cover" when coverBits != 0 => $"cover{coverBits}",
            "media_player" when e.Supports(FeatureMediaPrev) || e.Supports(FeatureMediaNext) =>
                $"media{(e.Supports(FeatureMediaPrev) ? 1 : 0)}{(e.Supports(FeatureMediaNext) ? 1 : 0)}",
            "climate" when e.Supports(FeatureClimateTargetTemp) && e.GetDouble("temperature") is not null => "climate",
            _ => "",
        };
    }

    private static bool SupportsBrightness(HaEntity e) =>
        e.GetStringList("supported_color_modes").Any(m => m is not ("onoff" or "unknown"));

    private void UpdateStrip(HaEntity e)
    {
        if (_brightness is not null && !_interacting)
        {
            _brightness.IsEnabled = !e.IsUnavailable;
            _brightness.Value = e.State == "on" && e.GetDouble("brightness") is double b ? Math.Round(b / 255.0 * 100) : 0;
        }
        if (_playPauseIcon is not null)
            _playPauseIcon.Glyph = e.State == "playing" ? "" : "";
        if (_targetText is not null && _pendingTarget is null)
            _targetText.Text = TargetLabel(e, e.GetDouble("temperature"));
    }

    private FrameworkElement BuildBrightness()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            Height = 28,
            Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        slider.ValueChanged += (_, args) =>
        {
            if (_updating) return;
            _pendingBrightness = args.NewValue;
            Debounce(120, SendBrightness);
        };
        // The slider marks pointer events as handled -> handledEventsToo.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _interacting = true), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndSliderInteraction()), handledEventsToo: true);
        slider.PointerCaptureLost += (_, _) => EndSliderInteraction();
        _brightness = slider;
        return slider;
    }

    private void EndSliderInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _debounce?.Stop();
        SendBrightness(); // flush the final value right away
        if (_entity is not null) Update(_entity);
    }

    private void SendBrightness()
    {
        if (_pendingBrightness is not double value) return;
        _pendingBrightness = null;
        int percent = (int)Math.Round(value);
        if (percent <= 0) Call("light", "turn_off");
        else Call("light", "turn_on", new() { ["brightness_pct"] = percent });
    }

    private FrameworkElement BuildCover(HaEntity e)
    {
        var grid = ButtonRow();
        void Add(string glyph, string service, string tip)
        {
            var b = StripButton(glyph, tip);
            b.Click += (_, _) => Call("cover", service);
            Grid.SetColumn(b, grid.ColumnDefinitions.Count);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(b);
        }
        if (e.Supports(FeatureCoverOpen)) Add("", "open_cover", Loc.T("action.open"));
        if (e.Supports(FeatureCoverStop)) Add("", "stop_cover", Loc.T("action.stop"));
        if (e.Supports(FeatureCoverClose)) Add("", "close_cover", Loc.T("action.close"));
        return grid;
    }

    private FrameworkElement BuildMedia(HaEntity e)
    {
        var grid = ButtonRow();
        void Add(string glyph, string service, string tip, bool isPlayPause = false)
        {
            var b = StripButton(glyph, tip);
            if (isPlayPause) _playPauseIcon = (FontIcon)b.Content;
            b.Click += (_, _) => Call("media_player", service);
            Grid.SetColumn(b, grid.ColumnDefinitions.Count);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(b);
        }
        if (e.Supports(FeatureMediaPrev)) Add("", "media_previous_track", Loc.T("media.previous"));
        Add("", "media_play_pause", Loc.T("media.playPause"), isPlayPause: true);
        if (e.Supports(FeatureMediaNext)) Add("", "media_next_track", Loc.T("media.next"));
        return grid;
    }

    private FrameworkElement BuildClimate()
    {
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var minus = StripButton("", Loc.T("climate.colder"));
        minus.Click += (_, _) => StepTarget(-1);
        var plus = StripButton("", Loc.T("climate.warmer"));
        plus.Click += (_, _) => StepTarget(+1);
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        _targetText = text;

        Grid.SetColumn(text, 1);
        Grid.SetColumn(plus, 2);
        grid.Children.Add(minus);
        grid.Children.Add(text);
        grid.Children.Add(plus);
        return grid;
    }

    private void StepTarget(int direction)
    {
        if (_entity is not { } e || e.IsUnavailable) return;
        double step = e.GetDouble("target_temp_step") ?? (EntityPresentation.TempUnit(e).Contains('F') ? 1 : 0.5);
        double current = _pendingTarget ?? e.GetDouble("temperature") ?? 20;
        double next = Math.Round((current + direction * step) / step) * step;
        next = Math.Clamp(next, e.GetDouble("min_temp") ?? 5, e.GetDouble("max_temp") ?? 35);
        _pendingTarget = next;
        if (_targetText is not null) _targetText.Text = TargetLabel(e, next);
        Debounce(600, SendTarget);
    }

    private void SendTarget()
    {
        if (_pendingTarget is not double value) return;
        SendTargetAsync(value);
    }

    private async void SendTargetAsync(double value)
    {
        try
        {
            await _ha.CallServiceAsync("climate", "set_temperature", EntityId, new() { ["temperature"] = value });
        }
        catch (Exception ex)
        {
            _showError(DisplayName, ex.Message);
        }
        finally
        {
            _pendingTarget = null;
            if (_entity is not null) Update(_entity);
        }
    }

    private static string TargetLabel(HaEntity e, double? target) =>
        target is double t ? $"{EntityPresentation.Number(t)} {EntityPresentation.TempUnit(e)}" : "–";

    private void Debounce(int milliseconds, Action action)
    {
        _debounce ??= _dq.CreateTimer();
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _debounce.IsRepeating = false;
        void OnTick(DispatcherQueueTimer t, object a)
        {
            t.Tick -= OnTick;
            action();
        }
        _debounce.Tick += OnTick;
        _debounce.Start();
    }

    private static Grid ButtonRow() => new() { ColumnSpacing = 4 };

    private static Button StripButton(string glyph, string tooltip)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(0, 5, 0, 5),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }
}
