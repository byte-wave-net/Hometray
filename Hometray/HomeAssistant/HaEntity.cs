using System.Globalization;
using System.Text.Json;

namespace Hometray.HomeAssistant;

/// <summary>Immutable snapshot of one Home Assistant entity state.</summary>
internal sealed class HaEntity
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoAttributes = new Dictionary<string, JsonElement>();

    public string EntityId { get; }
    public string State { get; }
    public IReadOnlyDictionary<string, JsonElement> Attributes { get; }

    private HaEntity(string entityId, string state, IReadOnlyDictionary<string, JsonElement> attributes)
    {
        EntityId = entityId;
        State = state;
        Attributes = attributes;
    }

    public string Domain
    {
        get
        {
            int dot = EntityId.IndexOf('.');
            return dot < 0 ? EntityId : EntityId[..dot];
        }
    }

    public string Name => GetString("friendly_name") ?? EntityId;
    /// <summary>Device offline. ("unknown" is a normal state, e.g. of a scene that was never activated.)</summary>
    public bool IsUnavailable => State == "unavailable";

    /// <summary>Parses a state object ({"entity_id", "state", "attributes"}); null if malformed.</summary>
    public static HaEntity? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("entity_id", out var id) || id.GetString() is not { Length: > 0 } entityId) return null;
        string state = e.TryGetProperty("state", out var s) ? s.ToString() : "unknown";

        IReadOnlyDictionary<string, JsonElement> attributes = NoAttributes;
        if (e.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            // Clone: the JsonDocument the element belongs to is disposed after parsing.
            var dict = new Dictionary<string, JsonElement>();
            foreach (var p in attrs.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            attributes = dict;
        }
        return new HaEntity(entityId, state, attributes);
    }

    public string? GetString(string name) =>
        Attributes.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public double? GetDouble(string name)
    {
        if (!Attributes.TryGetValue(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public int GetInt(string name) => GetDouble(name) is double d ? (int)d : 0;

    public IReadOnlyList<string> GetStringList(string name)
    {
        if (!Attributes.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }

    public double[]? GetDoubleArray(string name)
    {
        if (!Attributes.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray();
        return list.Length == 0 ? null : list;
    }

    public int[]? GetIntArray(string name)
    {
        if (!Attributes.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToArray();
        return list.Length == 0 ? null : list;
    }

    public bool Supports(int featureBit) => (GetInt("supported_features") & featureBit) != 0;

    /// <summary>State as a number (null if not numeric).</summary>
    public double? NumericState =>
        double.TryParse(State, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
