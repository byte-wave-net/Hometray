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
/// The detail panel of a tile (opened by long press / right click): the settings of the device
/// beyond the tile's primary action - light color and color temperature, cover position,
/// thermostat mode, fan speed, volume, ... Only what the entity supports is offered.
/// <see cref="Update"/> keeps the controls in sync with state changes made elsewhere.
/// </summary>
internal sealed class EntityDetail
{
    private const int FeatureCoverSetPosition = 4, FeatureCoverSetTilt = 128;
    private const int FeatureClimateTargetTemp = 1, FeatureClimatePreset = 16;
    private const int FeatureFanSpeed = 1, FeatureFanOscillate = 2, FeatureFanPreset = 8;
    private const int FeatureMediaVolumeSet = 4, FeatureMediaMute = 8, FeatureMediaSource = 2048;

    private readonly HaClient _ha;
    private readonly string _entityId;
    private readonly string _title;
    private readonly Action<string, string> _showError;
    private readonly Func<Confirmation, Task<bool>> _confirm;
    private readonly Action? _changeScriptInputs, _forgetScriptInputs;
    private HaEntity _current;
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private readonly List<Action<HaEntity>> _refreshers = new();
    private readonly StackPanel _rows = new() { Spacing = 14 };
    private readonly Border _iconCircle = new();
    private readonly FontIcon _icon = new();
    private readonly TextBlock _stateText = new();
    private bool _updating;

    // light color: hue and saturation are sent together
    private double _hue, _sat = 100;
    private GradientStop? _satEndStop;

    public FrameworkElement Root { get; }

    /// <summary>The close button was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>Limits the height of the scrolling content (the panel never exceeds the window).</summary>
    public double MaxContentHeight { set => _scroller.MaxHeight = value; }
    private readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    // ── what an entity offers ────────────────────────────────────────────────

    private static List<string> Sections(HaEntity e)
    {
        var list = new List<string>();
        switch (e.Domain)
        {
            case "light":
                var modes = e.GetStringList("supported_color_modes");
                if (modes.Any(m => m is not ("onoff" or "unknown"))) list.Add("brightness");
                if (modes.Contains("color_temp")) list.Add("colortemp");
                if (modes.Any(m => m is "hs" or "xy" or "rgb" or "rgbw" or "rgbww")) list.Add("color");
                if (e.GetStringList("effect_list").Count > 0) list.Add("effect");
                break;
            case "cover":
                if (e.Supports(FeatureCoverSetPosition)) list.Add("position");
                if (e.Supports(FeatureCoverSetTilt)) list.Add("tilt");
                break;
            case "climate":
                if (e.GetStringList("hvac_modes").Count > 1) list.Add("hvac");
                if (e.Supports(FeatureClimateTargetTemp) && e.GetDouble("temperature") is not null) list.Add("target");
                if (e.Supports(FeatureClimatePreset) && e.GetStringList("preset_modes").Count > 0) list.Add("preset");
                break;
            case "fan":
                if (e.Supports(FeatureFanSpeed)) list.Add("fanspeed");
                if (e.Supports(FeatureFanPreset) && e.GetStringList("preset_modes").Count > 0) list.Add("fanpreset");
                if (e.Supports(FeatureFanOscillate)) list.Add("oscillate");
                break;
            case "media_player":
                if (e.Supports(FeatureMediaVolumeSet)) list.Add("volume");
                if (e.Supports(FeatureMediaMute)) list.Add("mute");
                if (e.Supports(FeatureMediaSource) && e.GetStringList("source_list").Count > 0) list.Add("source");
                break;
            case "input_number" or "number":
                if (e.GetDouble("min") is not null && e.GetDouble("max") is not null) list.Add("number");
                break;
            case "input_select" or "select":
                if (e.GetStringList("options").Count > 0) list.Add("select");
                break;
            case "script":
                // a script whose inputs are remembered: change / forget them (otherwise it has no panel)
                if (AppSettings.HasScriptInputs(e.EntityId)) list.Add("scriptinputs");
                break;
        }
        return list;
    }

    public static bool HasControls(HaEntity e) => !e.IsUnavailable && Sections(e).Count > 0;

    // ── build ────────────────────────────────────────────────────────────────

    public EntityDetail(HaEntity entity, string title, HaClient ha, Action<string, string> showError,
        Func<Confirmation, Task<bool>> confirm, Action? changeScriptInputs = null, Action? forgetScriptInputs = null)
    {
        _changeScriptInputs = changeScriptInputs;
        _forgetScriptInputs = forgetScriptInputs;
        _ha = ha;
        _entityId = entity.EntityId;
        _title = title;
        _showError = showError;
        _confirm = confirm;
        _current = entity;

        foreach (string section in Sections(entity)) AddSection(section, entity);

        // header: icon, name + state, close
        _iconCircle.Width = _iconCircle.Height = 36;
        _iconCircle.CornerRadius = new CornerRadius(18);
        _icon.FontSize = 18;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _iconCircle.Child = _icon;

        var name = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _stateText.FontSize = 12;
        _stateText.Foreground = EntityTile.Brush("TextFillColorSecondaryBrush");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(_stateText);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(close, Loc.T("action.close"));
        close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_iconCircle);
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        _scroller.Content = _rows;
        _rows.Margin = new Thickness(0, 0, 10, 0); // gutter for the overlay scroll bar

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(header);
        body.Children.Add(_scroller);

        Root = new Border
        {
            Child = body,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = EntityTile.Brush("SolidBackgroundFillColorSecondaryBrush"),
            BorderBrush = EntityTile.Brush("SurfaceStrokeColorDefaultBrush"),
        };

        Update(entity);
    }

    private void AddSection(string section, HaEntity e)
    {
        switch (section)
        {
            // ── light ──
            case "brightness":
                AddSlider(Loc.T("detail.brightness"), 1, 100, 1, v => $"{v:0} %",
                    x => x.State == "on" ? x.GetDouble("brightness") / 255.0 * 100 : null,
                    v => Call("light", "turn_on", new() { ["brightness_pct"] = (int)Math.Round(v) }));
                break;

            case "colortemp":
                double minK = e.GetDouble("min_color_temp_kelvin") ?? 2000, maxK = e.GetDouble("max_color_temp_kelvin") ?? 6500;
                AddSlider(Loc.T("detail.colorTemp"), minK, maxK, 50, v => $"{v:0} K",
                    x => x.GetDouble("color_temp_kelvin"),
                    v => Call("light", "turn_on", new() { ["color_temp_kelvin"] = (int)Math.Round(v) }),
                    gradient: Gradient(Color.FromArgb(255, 255, 167, 87), Color.FromArgb(255, 255, 244, 229), Color.FromArgb(255, 166, 209, 255)));
                break;

            case "color":
                AddSwatches();
                AddColorSliders();
                break;

            case "effect":
                AddChoice(Loc.T("detail.effect"), e.GetStringList("effect_list").Select(x => (x, x)).ToList(),
                    x => x.GetString("effect"),
                    v => Call("light", "turn_on", new() { ["effect"] = v }));
                break;

            // ── cover ──
            case "position":
                AddSlider(Loc.T("detail.position"), 0, 100, 1, v => $"{v:0} %", x => x.GetDouble("current_position"),
                    v => Call("cover", "set_cover_position", new() { ["position"] = (int)Math.Round(v) }));
                break;
            case "tilt":
                AddSlider(Loc.T("detail.tilt"), 0, 100, 1, v => $"{v:0} %", x => x.GetDouble("current_tilt_position"),
                    v => Call("cover", "set_cover_tilt_position", new() { ["tilt_position"] = (int)Math.Round(v) }));
                break;

            // ── climate ──
            case "hvac":
                AddChoice(Loc.T("detail.hvacMode"), e.GetStringList("hvac_modes").Select(m => (m, EntityPresentation.HvacMode(m))).ToList(),
                    x => x.State, v => Call("climate", "set_hvac_mode", new() { ["hvac_mode"] = v }));
                break;
            case "target":
                string unit = EntityPresentation.TempUnit(e);
                double step = e.GetDouble("target_temp_step") ?? (unit.Contains('F') ? 1 : 0.5);
                AddSlider(Loc.T("detail.targetTemp"), e.GetDouble("min_temp") ?? 5, e.GetDouble("max_temp") ?? 35, step,
                    v => $"{EntityPresentation.Number(v)} {unit}", x => x.GetDouble("temperature"),
                    v => Call("climate", "set_temperature", new() { ["temperature"] = v }));
                break;
            case "preset":
                AddChoice(Loc.T("detail.preset"), e.GetStringList("preset_modes").Select(x => (x, x)).ToList(),
                    x => x.GetString("preset_mode"), v => Call("climate", "set_preset_mode", new() { ["preset_mode"] = v }));
                break;

            // ── fan ──
            case "fanspeed":
                AddSlider(Loc.T("detail.fanSpeed"), 0, 100, e.GetDouble("percentage_step") ?? 1, v => $"{v:0} %", x => x.GetDouble("percentage"),
                    v => Call("fan", "set_percentage", new() { ["percentage"] = (int)Math.Round(v) }));
                break;
            case "fanpreset":
                AddChoice(Loc.T("detail.preset"), e.GetStringList("preset_modes").Select(x => (x, x)).ToList(),
                    x => x.GetString("preset_mode"), v => Call("fan", "set_preset_mode", new() { ["preset_mode"] = v }));
                break;
            case "oscillate":
                AddToggle(Loc.T("detail.oscillation"), x => x.Attributes.TryGetValue("oscillating", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True,
                    on => Call("fan", "oscillate", new() { ["oscillating"] = on }));
                break;

            // ── media player ──
            case "volume":
                AddSlider(Loc.T("detail.volume"), 0, 100, 1, v => $"{v:0} %", x => x.GetDouble("volume_level") * 100,
                    v => Call("media_player", "volume_set", new() { ["volume_level"] = Math.Round(v) / 100.0 }));
                break;
            case "mute":
                AddToggle(Loc.T("detail.mute"), x => x.Attributes.TryGetValue("is_volume_muted", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.True,
                    on => Call("media_player", "volume_mute", new() { ["is_volume_muted"] = on }));
                break;
            case "source":
                AddChoice(Loc.T("detail.source"), e.GetStringList("source_list").Select(x => (x, x)).ToList(),
                    x => x.GetString("source"), v => Call("media_player", "select_source", new() { ["source"] = v }));
                break;

            // ── helpers (input_number / number, input_select / select) ──
            case "number":
                string numberUnit = e.GetString("unit_of_measurement") is { Length: > 0 } u ? " " + u : "";
                AddSlider(e.Name, e.GetDouble("min") ?? 0, e.GetDouble("max") ?? 100, e.GetDouble("step") ?? 1,
                    v => EntityPresentation.Number(v) + numberUnit, x => x.NumericState,
                    v => Call(e.Domain, "set_value", new() { ["value"] = v }));
                break;
            case "scriptinputs":
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var change = new Button { Content = Loc.T("script.edit") };
                change.Click += (_, _) => _changeScriptInputs?.Invoke();
                var forget = new Button { Content = Loc.T("script.forget") };
                forget.Click += (_, _) => _forgetScriptInputs?.Invoke();
                buttons.Children.Add(change);
                buttons.Children.Add(forget);
                _rows.Children.Add(Labeled(Loc.T("script.saved"), buttons));
                break;
            }
            case "select":
                AddChoice(e.Name, e.GetStringList("options").Select(x => (x, x)).ToList(),
                    x => x.State, v => Call(e.Domain, "select_option", new() { ["option"] = v }));
                break;
        }
    }

    // ── light color ──────────────────────────────────────────────────────────

    private static readonly (string Name, double Hue)[] Swatches =
    {
        ("color.red", 0), ("color.orange", 28), ("color.yellow", 52), ("color.green", 120), ("color.turquoise", 175), ("color.blue", 220), ("color.violet", 275), ("color.pink", 325),
    };

    private void AddSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (name, hue) in Swatches)
        {
            var swatch = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(HueColor(hue, 100)),
                BorderBrush = EntityTile.Brush("ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(swatch, Loc.T(name));
            double h = hue;
            swatch.Tapped += (_, _) => Call("light", "turn_on", new() { ["hs_color"] = new[] { h, 100.0 } });
            row.Children.Add(swatch);
        }
        _rows.Children.Add(Labeled(Loc.T("detail.color"), row));
    }

    private void AddColorSliders()
    {
        // hue: rainbow track
        var rainbow = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        for (int i = 0; i <= 6; i++)
            rainbow.GradientStops.Add(new GradientStop { Color = HueColor(i * 60, 100), Offset = i / 6.0 });

        // saturation: white → the current hue
        _satEndStop = new GradientStop { Color = HueColor(_hue, 100), Offset = 1 };
        var satBrush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        satBrush.GradientStops.Add(new GradientStop { Color = Colors.White, Offset = 0 });
        satBrush.GradientStops.Add(_satEndStop);

        AddSlider(Loc.T("detail.hue"), 0, 360, 1, v => $"{v:0}°", x => x.GetDoubleArray("hs_color") is { Length: >= 2 } hs ? hs[0] : null,
            _ => SendColor(), rainbow, onValueChanged: v =>
            {
                _hue = v;
                if (_satEndStop is not null) _satEndStop.Color = HueColor(v, 100);
            });
        AddSlider(Loc.T("detail.saturation"), 0, 100, 1, v => $"{v:0} %", x => x.GetDoubleArray("hs_color") is { Length: >= 2 } hs ? hs[1] : null,
            _ => SendColor(), satBrush, onValueChanged: v => _sat = v);
    }

    private void SendColor() => Call("light", "turn_on", new() { ["hs_color"] = new[] { _hue, _sat } });

    /// <summary>Color of a hue / saturation pair (full brightness).</summary>
    private static Color HueColor(double hue, double saturationPercent)
    {
        double h = (hue % 360 + 360) % 360 / 60, s = saturationPercent / 100, v = 1;
        int i = (int)Math.Floor(h);
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (double r, double g, double b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromArgb(255, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static LinearGradientBrush Gradient(params Color[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        for (int i = 0; i < colors.Length; i++)
            brush.GradientStops.Add(new GradientStop { Color = colors[i], Offset = i / (double)(colors.Length - 1) });
        return brush;
    }

    // ── generic rows ─────────────────────────────────────────────────────────

    /// <summary>A slider row: debounced while dragging, final value sent on release, not overwritten by
    /// incoming states while held. <paramref name="gradient"/> replaces the track with a color bar.</summary>
    private void AddSlider(string label, double min, double max, double step, Func<double, string> format,
        Func<HaEntity, double?> read, Action<double> commit, Brush? gradient = null, Action<double>? onValueChanged = null)
    {
        var valueText = new TextBlock { FontSize = 12, Foreground = EntityTile.Brush("TextFillColorSecondaryBrush") };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, HorizontalAlignment = HorizontalAlignment.Stretch };

        FrameworkElement control = slider;
        if (gradient is not null)
        {
            var transparent = new SolidColorBrush(Colors.Transparent);
            foreach (string key in new[]
            {
                "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed", "SliderTrackFillDisabled",
                "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderTrackValueFillDisabled",
            })
                slider.Resources[key] = transparent;

            var bar = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = gradient,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
            };
            var stack = new Grid();
            stack.Children.Add(bar);
            stack.Children.Add(slider);
            control = stack;
        }

        bool dragging = false;
        double? pending = null;
        var debounce = _dq.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(150);
        debounce.IsRepeating = false;
        void Flush()
        {
            debounce.Stop();
            if (pending is not double v) return;
            pending = null;
            commit(v);
        }
        debounce.Tick += (_, _) => Flush();

        slider.ValueChanged += (_, args) =>
        {
            valueText.Text = format(args.NewValue);
            onValueChanged?.Invoke(args.NewValue);
            if (_updating) return;
            pending = args.NewValue;
            debounce.Stop();
            debounce.Start();
        };
        // The slider marks pointer events as handled -> handledEventsToo.
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { dragging = false; Flush(); }), handledEventsToo: true);
        slider.PointerCaptureLost += (_, _) => { dragging = false; Flush(); };

        _refreshers.Add(e =>
        {
            if (dragging) return;
            if (read(e) is double v && !double.IsNaN(v)) slider.Value = Math.Clamp(v, min, max);
            valueText.Text = format(slider.Value);
        });

        var header = new Grid();
        header.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = EntityTile.Brush("TextFillColorSecondaryBrush") });
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(valueText);

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(header);
        row.Children.Add(control);
        _rows.Children.Add(row);
    }

    private void AddChoice(string label, IReadOnlyList<(string Value, string Text)> options,
        Func<HaEntity, string?> read, Action<string> commit)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, text) in options)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });

        combo.SelectionChanged += (_, _) =>
        {
            if (_updating || combo.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            commit(value);
        };
        _refreshers.Add(e =>
        {
            string? current = read(e);
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current);
        });
        _rows.Children.Add(Labeled(label, combo));
    }

    private void AddToggle(string label, Func<HaEntity, bool> read, Action<bool> commit)
    {
        var toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.Toggled += (_, _) =>
        {
            if (!_updating) commit(toggle.IsOn);
        };
        _refreshers.Add(e => toggle.IsOn = read(e));

        var row = new Grid();
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(toggle);
        _rows.Children.Add(row);
    }

    private static StackPanel Labeled(string label, UIElement content)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = EntityTile.Brush("TextFillColorSecondaryBrush") });
        panel.Children.Add(content);
        return panel;
    }

    // ── state / service calls ────────────────────────────────────────────────

    public void Update(HaEntity entity)
    {
        _current = entity;
        _updating = true;
        try
        {
            _stateText.Text = EntityPresentation.StateText(entity);
            _icon.Glyph = EntityPresentation.Glyph(entity);
            if (EntityPresentation.IsActive(entity))
            {
                var color = EntityPresentation.LightColor(entity);
                _iconCircle.Background = color is Color c ? new SolidColorBrush(c) : EntityTile.Brush("AccentFillColorDefaultBrush");
                double luminance = color is Color lc ? (0.299 * lc.R + 0.587 * lc.G + 0.114 * lc.B) / 255.0 : 0;
                _icon.Foreground = color is null
                    ? EntityTile.Brush("TextOnAccentFillColorPrimaryBrush")
                    : new SolidColorBrush(luminance > 0.6 ? Colors.Black : Colors.White);
            }
            else
            {
                _iconCircle.Background = EntityTile.Brush("SubtleFillColorSecondaryBrush");
                _icon.Foreground = EntityTile.Brush("TextFillColorPrimaryBrush");
            }

            foreach (var refresh in _refreshers) refresh(entity);
        }
        finally
        {
            _updating = false;
        }
    }

    private async void Call(string domain, string service, Dictionary<string, object> data)
    {
        try
        {
            if (EntityPresentation.ConfirmationFor(_current, domain, service, _title) is { } question && !await _confirm(question))
            {
                // declined: put the controls (e.g. a position slider) back to the real state
                if (_ha.Get(_entityId) is { } latest) Update(latest);
                return;
            }
            await _ha.CallServiceAsync(domain, service, _entityId, data);
        }
        catch (Exception ex)
        {
            _showError(_title, ex.Message);
        }
    }
}
