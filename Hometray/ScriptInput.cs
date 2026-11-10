using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hometray.HomeAssistant;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hometray;

/// <summary>
/// The dialog that asks for the inputs ("fields") of a script before it runs. Every field gets the control that
/// fits its selector (text, number, switch, list, entity, time, date, duration, color ...); required fields must be
/// filled before "Run" is enabled. Optional fields that were left empty are not sent.
/// </summary>
internal static class ScriptInput
{
    /// <summary>Height (DIP) the flyout window needs while the dialog is open.</summary>
    private const int DialogMinHeightDip = 640;

    private sealed record FieldInput(ScriptField Field, FrameworkElement Control, Func<object?> Get, Func<bool> HasValue);

    /// <summary>What the user entered: the script's <c>variables</c> and whether to remember them.</summary>
    internal sealed record ScriptAnswer(Dictionary<string, object> Values, bool Remember);

    /// <summary>Password fields are never remembered (the values would be stored as plain text).</summary>
    public static bool CanRemember(IReadOnlyList<ScriptField> fields) =>
        fields.All(f => !(f.Selector == "text" && f.ConfigString("type") == "password"));

    /// <param name="saved">Remembered values of an earlier run: they fill the fields in.</param>
    /// <returns>The entered values, or null if the user cancelled.</returns>
    public static async Task<ScriptAnswer?> AskAsync(XamlRoot root, Func<ContentDialog, Task<ContentDialogResult>> show,
        string title, IReadOnlyList<ScriptField> fields, HaClient ha, JsonObject? saved = null)
    {
        // remembered values act as the fields' default values
        if (saved is not null)
            fields = fields.Select(f => saved.TryGetPropertyValue(f.Key, out var node) && node is not null
                ? f with { Default = JsonSerializer.SerializeToElement(node) }
                : f).ToList();

        var inputs = new List<FieldInput>();
        ContentDialog? dialog = null;
        void Validate()
        {
            if (dialog is not null) dialog.IsPrimaryButtonEnabled = inputs.All(i => !i.Field.Required || i.HasValue());
        }

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var field in fields)
        {
            var input = Build(field, ha, Validate);
            inputs.Add(input);
            panel.Children.Add(Labeled(field, input.Control));
        }
        if (fields.Any(f => f.Required))
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("script.requiredHint"),
                FontSize = 12,
                Foreground = EntityTile.Brush("TextFillColorSecondaryBrush"),
            });

        // "remember": next time the script starts with these values, without this dialog
        CheckBox? remember = null;
        if (CanRemember(fields))
        {
            remember = new CheckBox { Content = Loc.T("script.remember"), IsChecked = saved is not null, MinWidth = 0 };
            panel.Children.Add(remember);
        }

        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320 };
        dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = scroller,
            PrimaryButtonText = Loc.T("script.run"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Tag = DialogMinHeightDip, // the flyout grows to this height while the dialog is open
        };
        // the fields scroll if they do not fit; the dialog never grows beyond the (enlarged) window
        dialog.Opened += (_, _) => scroller.MaxHeight = Math.Max(120, root.Size.Height - 250);
        Validate();

        if (await show(dialog) != ContentDialogResult.Primary) return null;

        var values = new Dictionary<string, object>();
        foreach (var input in inputs)
            if (input.Get() is { } value) values[input.Field.Key] = value;
        return new ScriptAnswer(values, remember?.IsChecked == true);
    }

    private static StackPanel Labeled(ScriptField field, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = field.Required ? field.Name + " *" : field.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(control);
        if (field.Description is not null)
            panel.Children.Add(new TextBlock
            {
                Text = field.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = EntityTile.Brush("TextFillColorSecondaryBrush"),
            });
        return panel;
    }

    private static FieldInput Build(ScriptField f, HaClient ha, Action changed) => f.Selector switch
    {
        "number" => Number(f, changed),
        "boolean" => SwitchInput(f),
        "select" when Options(f).Count > 0 => ChoiceInput(f, changed),
        "entity" when Candidates(f, ha).Count > 0 => Entity(f, ha, changed),
        "time" => TimeInput(f, changed),
        "date" => DateInput(f, changed),
        "datetime" => DateTimeInput(f, changed),
        "duration" => Duration(f, changed),
        "color_rgb" => ColorRgb(f, changed),
        "color_temp" => ColorTemp(f),
        "object" => ObjectInput(f, changed),
        _ => Text(f, changed), // text, template, target, area, device, icon ... and fields without a selector
    };

    // ── text / object ────────────────────────────────────────────────────────

    private static FieldInput Text(ScriptField f, Action changed)
    {
        string example = f.Example is { } ex ? (ex.ValueKind == JsonValueKind.String ? ex.GetString() ?? "" : ex.ToString()) : "";
        if (f.ConfigString("type") == "password")
        {
            var box = new PasswordBox { PlaceholderText = example, Password = f.DefaultString ?? "" };
            box.PasswordChanged += (_, _) => changed();
            return new(f, box, () => box.Password.Length > 0 ? box.Password : null, () => box.Password.Length > 0);
        }

        bool multiline = f.ConfigFlag("multiline");
        var text = new TextBox
        {
            PlaceholderText = example,
            Text = f.DefaultString ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 80 : 0,
        };
        text.TextChanged += (_, _) => changed();
        return new(f, text, () => string.IsNullOrWhiteSpace(text.Text) ? null : text.Text, () => !string.IsNullOrWhiteSpace(text.Text));
    }

    /// <summary>JSON text; valid JSON is sent as a structure (object / list / number ...), anything else as text.</summary>
    private static FieldInput ObjectInput(ScriptField f, Action changed)
    {
        var text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Text = f.Default?.GetRawText() ?? "",
            PlaceholderText = f.Example?.GetRawText() ?? "{ }",
        };
        text.TextChanged += (_, _) => changed();
        return new(f, text, () =>
        {
            if (string.IsNullOrWhiteSpace(text.Text)) return null;
            try { return JsonNode.Parse(text.Text); }
            catch (JsonException) { return text.Text; }
        }, () => !string.IsNullOrWhiteSpace(text.Text));
    }

    // ── numbers ──────────────────────────────────────────────────────────────

    private static object Whole(double v) => v == Math.Floor(v) && Math.Abs(v) < 1e15 ? (long)v : v;

    private static FieldInput Number(ScriptField f, Action changed)
    {
        double? min = f.ConfigNumber("min"), max = f.ConfigNumber("max"), step = f.ConfigNumber("step");
        string unit = f.ConfigString("unit_of_measurement") ?? "";
        string? mode = f.ConfigString("mode");
        bool slider = mode == "slider" || (mode is null && min is not null && max is not null);

        if (slider && min is double lo && max is double hi)
            return SliderInput(f, lo, hi, step ?? 1, f.DefaultNumber, unit);

        var box = new NumberBox
        {
            Minimum = min ?? double.MinValue,
            Maximum = max ?? double.MaxValue,
            SmallChange = step ?? 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = f.DefaultNumber ?? double.NaN,
            Description = unit.Length > 0 ? unit : null,
        };
        box.ValueChanged += (_, _) => changed();
        return new(f, box, () => double.IsNaN(box.Value) ? null : Whole(box.Value), () => !double.IsNaN(box.Value));
    }

    /// <summary>A slider with its value; an untouched slider without default is not sent (optional fields).</summary>
    private static FieldInput SliderInput(ScriptField f, double min, double max, double step, double? initial, string unit)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step <= 0 ? 1 : step,
            Value = Math.Clamp(initial ?? min, min, max),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 56, TextAlignment = TextAlignment.Right };
        string Format() => EntityPresentation.Number(slider.Value) + (unit.Length > 0 ? " " + unit : "");
        label.Text = Format();
        bool touched = false;
        slider.ValueChanged += (_, _) =>
        {
            touched = true;
            label.Text = Format();
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(slider);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        return new(f, grid, () => touched || initial is not null || f.Required ? Whole(slider.Value) : null, () => true);
    }

    private static FieldInput ColorTemp(ScriptField f)
    {
        bool mired = f.ConfigString("unit") == "mired";
        double min = f.ConfigNumber("min") ?? f.ConfigNumber(mired ? "min_mireds" : "min_kelvin") ?? (mired ? 153 : 2000);
        double max = f.ConfigNumber("max") ?? f.ConfigNumber(mired ? "max_mireds" : "max_kelvin") ?? (mired ? 500 : 6500);
        return SliderInput(f, min, max, mired ? 1 : 50, f.DefaultNumber, mired ? "mired" : "K");
    }

    // ── switch / lists ───────────────────────────────────────────────────────

    private static FieldInput SwitchInput(ScriptField f)
    {
        var toggle = new ToggleSwitch { IsOn = f.DefaultFlag, OnContent = Loc.T("state.on"), OffContent = Loc.T("state.off"), MinWidth = 0 };
        return new(f, toggle, () => toggle.IsOn, () => true);
    }

    private static List<(string Value, string Label)> Options(ScriptField f)
    {
        var list = new List<(string, string)>();
        if (f.Config.ValueKind != JsonValueKind.Object || !f.Config.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var o in options.EnumerateArray())
        {
            if (o.ValueKind == JsonValueKind.String) list.Add((o.GetString()!, o.GetString()!));
            else if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("value", out var v))
            {
                string value = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString();
                string label = o.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : value;
                list.Add((value, label));
            }
        }
        return list;
    }

    private static FieldInput ChoiceInput(ScriptField f, Action changed)
    {
        var options = Options(f);
        var defaults = new HashSet<string>(f.Default is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(x => x.ToString())
            : f.DefaultString is { } one ? new[] { one } : Array.Empty<string>());

        if (f.ConfigFlag("multiple"))
        {
            var boxes = new List<CheckBox>();
            var stack = new StackPanel { Spacing = 0 };
            foreach (var (value, label) in options)
            {
                var box = new CheckBox { Content = label, Tag = value, IsChecked = defaults.Contains(value), MinWidth = 0 };
                box.Checked += (_, _) => changed();
                box.Unchecked += (_, _) => changed();
                boxes.Add(box);
                stack.Children.Add(box);
            }
            return new(f, stack,
                () => boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList() is { Count: > 0 } picked ? picked : null,
                () => boxes.Any(b => b.IsChecked == true));
        }

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("script.choose") };
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => defaults.Contains((string)i.Tag));
        combo.SelectionChanged += (_, _) => changed();
        return new(f, combo, () => (combo.SelectedItem as ComboBoxItem)?.Tag as string, () => combo.SelectedItem is not null);
    }

    // ── entities ─────────────────────────────────────────────────────────────

    /// <summary>Entities that match the entity selector (domain / device class, also inside "filter").</summary>
    private static List<HaEntity> Candidates(ScriptField f, HaClient ha)
    {
        var filters = new List<(IReadOnlyList<string> Domains, IReadOnlyList<string> Classes)>();
        filters.Add((f.ConfigStrings("domain"), f.ConfigStrings("device_class")));
        if (f.Config.ValueKind == JsonValueKind.Object && f.Config.TryGetProperty("filter", out var filter))
        {
            var items = filter.ValueKind == JsonValueKind.Array ? filter.EnumerateArray().ToList() : new List<JsonElement> { filter };
            foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var sub = new ScriptField("", "", null, false, "entity", item, null, null);
                filters.Add((sub.ConfigStrings("domain"), sub.ConfigStrings("device_class")));
            }
        }
        // the plain domain / device_class entry only counts when it restricts something
        bool Matches(HaEntity e, (IReadOnlyList<string> Domains, IReadOnlyList<string> Classes) x) =>
            (x.Domains.Count == 0 || x.Domains.Contains(e.Domain)) &&
            (x.Classes.Count == 0 || x.Classes.Contains(e.GetString("device_class") ?? ""));

        var restricting = filters.Where(x => x.Domains.Count > 0 || x.Classes.Count > 0).ToList();
        return ha.Entities
            .Where(e => restricting.Count == 0 || restricting.Any(x => Matches(e, x)))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static FieldInput Entity(ScriptField f, HaClient ha, Action changed)
    {
        var candidates = Candidates(f, ha);
        var defaults = new HashSet<string>(f.Default is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(x => x.ToString())
            : f.DefaultString is { } one ? new[] { one } : Array.Empty<string>());
        string Label(HaEntity e) => e.Name == e.EntityId ? e.EntityId : $"{e.Name}  ({e.EntityId})";

        if (f.ConfigFlag("multiple"))
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 160 };
            foreach (var e in candidates)
            {
                var item = new ListViewItem { Content = Label(e), Tag = e.EntityId };
                list.Items.Add(item);
                if (defaults.Contains(e.EntityId)) list.SelectedItems.Add(item);
            }
            list.SelectionChanged += (_, _) => changed();
            return new(f, list,
                () => list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList() is { Count: > 0 } picked ? picked : null,
                () => list.SelectedItems.Count > 0);
        }

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.T("script.choose") };
        foreach (var e in candidates)
            combo.Items.Add(new ComboBoxItem { Content = Label(e), Tag = e.EntityId });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => defaults.Contains((string)i.Tag));
        combo.SelectionChanged += (_, _) => changed();
        return new(f, combo, () => (combo.SelectedItem as ComboBoxItem)?.Tag as string, () => combo.SelectedItem is not null);
    }

    // ── time / date / duration ───────────────────────────────────────────────

    private static TimeSpan? ParseTime(string? text) =>
        TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null;

    private static FieldInput TimeInput(ScriptField f, Action changed)
    {
        var picker = new TimePicker { ClockIdentifier = "24HourClock", SelectedTime = ParseTime(f.DefaultString) };
        picker.SelectedTimeChanged += (_, _) => changed();
        return new(f, picker,
            () => picker.SelectedTime is { } t ? new DateTime(t.Ticks).ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null,
            () => picker.SelectedTime is not null);
    }

    private static FieldInput DateInput(ScriptField f, Action changed)
    {
        var picker = new CalendarDatePicker();
        if (DateTime.TryParse(f.DefaultString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) picker.Date = d;
        picker.DateChanged += (_, _) => changed();
        return new(f, picker,
            () => picker.Date is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            () => picker.Date is not null);
    }

    private static FieldInput DateTimeInput(ScriptField f, Action changed)
    {
        var date = new CalendarDatePicker();
        var time = new TimePicker { ClockIdentifier = "24HourClock" };
        if (DateTime.TryParse(f.DefaultString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            date.Date = d;
            time.SelectedTime = d.TimeOfDay;
        }
        date.DateChanged += (_, _) => changed();
        time.SelectedTimeChanged += (_, _) => changed();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(date);
        row.Children.Add(time);
        return new(f, row,
            () => date.Date is { } day && time.SelectedTime is { } t
                ? day.Date.Add(t).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null,
            () => date.Date is not null && time.SelectedTime is not null);
    }

    private static FieldInput Duration(ScriptField f, Action changed)
    {
        NumberBox Part(string key, string labelKey)
        {
            double initial = f.Default is { ValueKind: JsonValueKind.Object } def && def.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : double.NaN;
            var box = new NumberBox
            {
                Header = Loc.T(labelKey),
                Minimum = 0,
                SmallChange = 1,
                Value = initial,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MinWidth = 100,
            };
            box.ValueChanged += (_, _) => changed();
            return box;
        }
        var hours = Part("hours", "script.hours");
        var minutes = Part("minutes", "script.minutes");
        var seconds = Part("seconds", "script.seconds");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(hours);
        row.Children.Add(minutes);
        row.Children.Add(seconds);
        double Value(NumberBox b) => double.IsNaN(b.Value) ? 0 : b.Value;
        bool Any() => !double.IsNaN(hours.Value) || !double.IsNaN(minutes.Value) || !double.IsNaN(seconds.Value);
        return new(f, row, () => Any()
            ? new Dictionary<string, object> { ["hours"] = Whole(Value(hours)), ["minutes"] = Whole(Value(minutes)), ["seconds"] = Whole(Value(seconds)) }
            : null, Any);
    }

    // ── color ────────────────────────────────────────────────────────────────

    private static FieldInput ColorRgb(ScriptField f, Action changed)
    {
        Color? chosen = null;
        if (f.Default is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() >= 3)
        {
            var rgb = a.EnumerateArray().Take(3).Select(x => x.ValueKind == JsonValueKind.Number ? (byte)Math.Clamp(x.GetDouble(), 0, 255) : (byte)0).ToArray();
            chosen = Color.FromArgb(255, rgb[0], rgb[1], rgb[2]);
        }

        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = EntityTile.Brush("ControlStrokeColorDefaultBrush") };
        var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(swatch);
        content.Children.Add(caption);
        void Refresh()
        {
            swatch.Background = chosen is Color c ? new SolidColorBrush(c) : new SolidColorBrush(Colors.Transparent);
            caption.Text = chosen is Color c2 ? $"#{c2.R:X2}{c2.G:X2}{c2.B:X2}" : Loc.T("script.pickColor");
        }
        Refresh();

        var picker = new ColorPicker { Color = chosen ?? Colors.White, IsAlphaEnabled = false, IsMoreButtonVisible = false };
        picker.ColorChanged += (_, e) =>
        {
            chosen = e.NewColor;
            Refresh();
            changed();
        };
        var button = new Button { Content = content, Flyout = new Flyout { Content = picker } };
        return new(f, button, () => chosen is Color c ? new[] { (int)c.R, c.G, c.B } : null, () => chosen is not null);
    }
}
