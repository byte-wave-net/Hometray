using System.Text.Json;

namespace Hometray.HomeAssistant;

/// <summary>
/// One input field of a Home Assistant script (the "fields" section of the script): what the user has to
/// enter before the script runs. <see cref="Selector"/> is the selector type ("text", "number", "boolean",
/// "select", "entity", "time", ...) and <see cref="Config"/> its settings (min / max, options, ...).
/// </summary>
internal sealed record ScriptField(
    string Key,
    string Name,
    string? Description,
    bool Required,
    string Selector,
    JsonElement Config,
    JsonElement? Default,
    JsonElement? Example)
{
    /// <summary>Parses the "fields" object of a script service description (see <c>get_services</c>).</summary>
    public static IReadOnlyList<ScriptField> ParseAll(JsonElement fields)
    {
        var list = new List<ScriptField>();
        if (fields.ValueKind != JsonValueKind.Object) return list;

        foreach (var f in fields.EnumerateObject())
        {
            var def = f.Value;
            if (def.ValueKind != JsonValueKind.Object) continue;

            // "selector": {"number": {"min": 0, ...}} - the single property is the type, its value the settings (or null)
            string selector = "text"; // no selector: free input
            JsonElement config = default;
            if (def.TryGetProperty("selector", out var sel) && sel.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in sel.EnumerateObject())
                {
                    selector = p.Name;
                    config = p.Value;
                    break;
                }
            }

            list.Add(new ScriptField(
                f.Name,
                def.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name ? name : f.Name,
                def.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } desc ? desc : null,
                def.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                selector,
                config,
                def.TryGetProperty("default", out var dv) && dv.ValueKind != JsonValueKind.Null ? dv.Clone() : null,
                def.TryGetProperty("example", out var ex) && ex.ValueKind != JsonValueKind.Null ? ex.Clone() : null));
        }
        return list;
    }

    // ── helpers for the selector settings ────────────────────────────────────

    public double? ConfigNumber(string name) =>
        Config.ValueKind == JsonValueKind.Object && Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public string? ConfigString(string name) =>
        Config.ValueKind == JsonValueKind.Object && Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public bool ConfigFlag(string name) =>
        Config.ValueKind == JsonValueKind.Object && Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A setting that may be a single string or a list of strings (e.g. the entity selector's "domain").</summary>
    public IReadOnlyList<string> ConfigStrings(string name)
    {
        if (Config.ValueKind != JsonValueKind.Object || !Config.TryGetProperty(name, out var v)) return Array.Empty<string>();
        return v.ValueKind switch
        {
            JsonValueKind.String => new[] { v.GetString()! },
            JsonValueKind.Array => v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            _ => Array.Empty<string>(),
        };
    }

    public string? DefaultString => Default is { ValueKind: JsonValueKind.String } d ? d.GetString() : Default?.ToString();

    public double? DefaultNumber => Default is { } d && d.ValueKind switch
    {
        JsonValueKind.Number => true,
        JsonValueKind.String => double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
        _ => false,
    }
        ? (d.ValueKind == JsonValueKind.Number ? d.GetDouble() : double.Parse(d.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
        : null;

    public bool DefaultFlag => Default is { ValueKind: JsonValueKind.True };
}
