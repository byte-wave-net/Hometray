using System.Globalization;

namespace Hometray.HomeAssistant;

/// <summary>A question to ask before a critical action (see <see cref="EntityPresentation.ConfirmationFor"/>).</summary>
internal sealed record Confirmation(string Title, string Message, string Button);

/// <summary>Icon, status text (in the current language) and "active" state of an entity - pure functions, no UI.</summary>
internal static class EntityPresentation
{
    /// <summary>
    /// The confirmation to ask before this service call, or null when none is needed: unlocking a lock and
    /// moving a garage door / gate / door drive. Locking and stopping are harmless and never ask.
    /// Also null when the user switched the safety question off.
    /// </summary>
    public static Confirmation? ConfirmationFor(HaEntity e, string domain, string service, string name)
    {
        if (!AppSettings.ConfirmCriticalActions) return null;

        if (domain == "lock" && service is "unlock" or "open")
            return new Confirmation(Loc.T("confirm.unlock.title", name), Loc.T("confirm.unlock.text"), Loc.T("confirm.unlock.button"));

        if (domain == "cover" && e.GetString("device_class") is "garage" or "gate" or "door")
        {
            string? verb = service switch
            {
                "open_cover" => "open",
                "close_cover" => "close",
                "toggle" => e.State is "closed" or "closing" ? "open" : "close",
                "set_cover_position" or "set_cover_tilt_position" => "move",
                _ => null, // stop etc.
            };
            if (verb is not null)
                return new Confirmation(Loc.T($"confirm.cover.{verb}.title", name), Loc.T("confirm.cover.text"), Loc.T($"action.{verb}"));
        }
        return null;
    }

    /// <summary>Domains the picker offers and that have a tile action or a meaningful read-only display.</summary>
    public static readonly string[] CommonDomains =
    {
        "light", "switch", "input_boolean", "cover", "climate", "fan", "lock", "media_player",
        "scene", "script", "button", "input_button", "automation", "group",
        "sensor", "binary_sensor", "weather", "person", "sun", "input_number", "number", "input_select", "select",
    };

    /// <summary>The light's own color (rgb_color), or a warm white while it is on without color info.</summary>
    public static Windows.UI.Color? LightColor(HaEntity e)
    {
        if (e.Domain != "light" || e.State != "on") return null;
        if (e.GetIntArray("rgb_color") is { Length: >= 3 } rgb && !(rgb[0] > 245 && rgb[1] > 245 && rgb[2] > 245))
            return Windows.UI.Color.FromArgb(255, (byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
        return Windows.UI.Color.FromArgb(255, 255, 197, 92);
    }

    // ── icon (Segoe Fluent Icons) ───────────────────────────────────────────

    public static string Glyph(HaEntity e)
    {
        string? dc = e.GetString("device_class");
        return e.Domain switch
        {
            "light" => "",
            "switch" or "input_boolean" => "",
            "cover" => dc is "garage" or "gate" ? "" : "",
            "lock" => e.State == "unlocked" ? "" : "",
            "climate" => "",
            "fan" => "",
            "media_player" => dc switch { "tv" => "", "speaker" => "", "receiver" => "", _ => "" },
            "scene" => "",
            "script" => "",
            "button" or "input_button" => "",
            "automation" => "",
            "weather" => "",
            "sun" => "",
            "person" or "device_tracker" => "",
            "camera" => "",
            "input_number" or "number" or "input_select" or "select" => "",
            "sensor" => dc switch
            {
                "temperature" => "",
                "humidity" or "moisture" => "",
                "battery" => "",
                "power" or "energy" or "current" or "voltage" => "",
                "illuminance" => "",
                "timestamp" or "date" => "",
                _ => "",
            },
            "binary_sensor" => dc switch
            {
                "motion" or "occupancy" or "presence" => "",
                "door" or "window" or "opening" or "garage_door" => "",
                "smoke" or "gas" or "safety" or "problem" or "carbon_monoxide" => "",
                "moisture" => "",
                "battery" => "",
                "lock" => "",
                "connectivity" => "",
                "light" => "",
                _ => "",
            },
            _ => "",
        };
    }

    // ── active ("on") state ──────────────────────────────────────────────────

    public static bool IsActive(HaEntity e)
    {
        if (e.IsUnavailable) return false;
        return e.Domain switch
        {
            "cover" => e.State != "closed",
            "lock" => e.State is "unlocked" or "unlocking" or "open" or "opening",
            "media_player" => e.State == "playing",
            "climate" => e.State != "off",
            "person" or "device_tracker" => e.State == "home",
            "sun" => e.State == "above_horizon",
            "weather" or "sensor" or "scene" or "button" or "input_button" or "input_number" or "number" => false,
            _ => e.State is "on" or "open" or "active" or "playing",
        };
    }

    /// <summary>Tile can be clicked to do something (toggle / activate).</summary>
    public static bool IsActionable(HaEntity e) => !e.IsUnavailable && e.Domain is
        "light" or "switch" or "input_boolean" or "cover" or "fan" or "lock" or "media_player" or
        "scene" or "script" or "button" or "input_button" or "automation" or "group";

    // ── status text ──────────────────────────────────────────────────────────

    public static string StateText(HaEntity e)
    {
        if (e.State == "unavailable") return Loc.T("state.unavailable");
        if (e.State == "unknown") return e.Domain is "scene" or "button" or "input_button" ? DomainLabel(e) : Loc.T("state.unknown");

        switch (e.Domain)
        {
            case "light":
                if (e.State != "on") return OnOff(e.State);
                return e.GetDouble("brightness") is double b ? $"{Loc.T("state.on")} · {Math.Round(b / 255.0 * 100)} %" : Loc.T("state.on");

            case "cover":
            {
                string text = CoverState(e.State);
                return e.GetDouble("current_position") is double p && e.State is "open" or "opening" or "closing"
                    ? $"{text} · {p:0} %" : text;
            }

            case "climate":
            {
                string text = e.GetString("hvac_action") is { } action ? HvacAction(action) : HvacMode(e.State);
                return e.GetDouble("current_temperature") is double t ? $"{text} · {Number(t)} {TempUnit(e)}" : text;
            }

            case "media_player":
                return e.State switch
                {
                    "playing" => Trim(e.GetString("media_title")) ?? Loc.T("media.playing"),
                    "paused" => Loc.T("media.paused"),
                    "idle" => Loc.T("state.ready"),
                    "standby" => Loc.T("media.standby"),
                    "off" => Loc.T("state.off"),
                    "on" => Loc.T("state.on"),
                    "buffering" => Loc.T("media.buffering"),
                    _ => e.State,
                };

            case "lock":
                return e.State switch
                {
                    "locked" => Loc.T("lock.locked"),
                    "unlocked" => Loc.T("lock.unlocked"),
                    "locking" => Loc.T("lock.locking"),
                    "unlocking" => Loc.T("lock.unlocking"),
                    "jammed" => Loc.T("lock.jammed"),
                    "open" => Loc.T("state.open"),
                    _ => e.State,
                };

            case "sensor":
            case "input_number":
            case "number":
                return SensorText(e);

            case "binary_sensor":
                return BinarySensorText(e);

            case "person":
            case "device_tracker":
                return e.State switch { "home" => Loc.T("person.home"), "not_home" => Loc.T("person.notHome"), _ => e.State };

            case "sun":
                return e.State == "above_horizon" ? Loc.T("sun.day") : Loc.T("sun.night");

            case "weather":
            {
                string text = Weather(e.State);
                return e.GetDouble("temperature") is double t ? $"{text} · {Number(t)} {e.GetString("temperature_unit") ?? "°C"}" : text;
            }

            case "scene" or "button" or "input_button":
                return DomainLabel(e);

            case "script":
                return e.State == "on" ? Loc.T("state.running") : Loc.T("state.ready");

            case "automation":
                return e.State == "on" ? Loc.T("state.active") : Loc.T("state.off");

            default:
                return e.State is "on" or "off" ? OnOff(e.State) : WithUnit(e, e.State);
        }
    }

    private static string DomainLabel(HaEntity e) => e.Domain switch
    {
        "scene" => Loc.T("state.scene"),
        _ => Loc.T("state.button"),
    };

    private static string OnOff(string state) => state switch { "on" => Loc.T("state.on"), "off" => Loc.T("state.off"), _ => state };

    private static string CoverState(string state) => state switch
    {
        "open" => Loc.T("state.open"),
        "closed" => Loc.T("state.closed"),
        "opening" => Loc.T("state.opening"),
        "closing" => Loc.T("state.closing"),
        _ => state,
    };

    private static string HvacAction(string action) => action switch
    {
        "heating" => Loc.T("hvac.heat"),
        "cooling" => Loc.T("hvac.cool"),
        "drying" => Loc.T("hvac.dry"),
        "fan" => Loc.T("hvac.fan_only"),
        "idle" => Loc.T("state.ready"),
        "off" => Loc.T("state.off"),
        _ => action,
    };

    public static string HvacMode(string mode) => mode switch
    {
        "off" => Loc.T("state.off"),
        "heat" => Loc.T("hvac.heat"),
        "cool" => Loc.T("hvac.cool"),
        "heat_cool" => Loc.T("hvac.heat_cool"),
        "auto" => Loc.T("hvac.auto"),
        "dry" => Loc.T("hvac.dry"),
        "fan_only" => Loc.T("hvac.fan_only"),
        _ => mode,
    };

    public static string TempUnit(HaEntity e) => e.GetString("temperature_unit") ?? "°C";

    private static string Weather(string condition) => condition switch
    {
        "sunny" => Loc.T("weather.sunny"),
        "clear-night" => Loc.T("weather.clear_night"),
        "cloudy" => Loc.T("weather.cloudy"),
        "partlycloudy" => Loc.T("weather.partlycloudy"),
        "rainy" => Loc.T("weather.rainy"),
        "pouring" => Loc.T("weather.pouring"),
        "snowy" => Loc.T("weather.snowy"),
        "snowy-rainy" => Loc.T("weather.snowy_rainy"),
        "fog" => Loc.T("weather.fog"),
        "hail" => Loc.T("weather.hail"),
        "lightning" or "lightning-rainy" => Loc.T("weather.lightning"),
        "windy" or "windy-variant" => Loc.T("weather.windy"),
        "exceptional" => Loc.T("weather.exceptional"),
        _ => condition,
    };

    private static string SensorText(HaEntity e)
    {
        if (e.GetString("device_class") is "timestamp" or "date" &&
            DateTimeOffset.TryParse(e.State, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            var local = when.ToLocalTime();
            return e.GetString("device_class") == "date" ? local.ToString("d") : local.ToString("g");
        }
        return WithUnit(e, e.State);
    }

    private static string BinarySensorText(HaEntity e)
    {
        bool on = e.State == "on";
        return e.GetString("device_class") switch
        {
            "door" or "window" or "opening" or "garage_door" => Loc.T(on ? "state.open" : "state.closed"),
            "motion" => Loc.T(on ? "bs.motion.on" : "bs.motion.off"),
            "occupancy" or "presence" => Loc.T(on ? "bs.presence.on" : "bs.presence.off"),
            "smoke" => Loc.T(on ? "bs.smoke.on" : "bs.smoke.off"),
            "gas" or "carbon_monoxide" => Loc.T(on ? "bs.gas.on" : "bs.gas.off"),
            "moisture" => Loc.T(on ? "bs.moisture.on" : "bs.moisture.off"),
            "battery" => Loc.T(on ? "bs.battery.on" : "bs.normal"),
            "lock" => Loc.T(on ? "lock.unlocked" : "lock.locked"),
            "connectivity" => Loc.T(on ? "status.connected" : "status.disconnected"),
            "problem" or "safety" or "tamper" => Loc.T(on ? "bs.problem.on" : "bs.ok"),
            "plug" or "power" => Loc.T(on ? "bs.plug.on" : "status.disconnected"),
            "cold" => Loc.T(on ? "bs.cold.on" : "bs.normal"),
            "heat" => Loc.T(on ? "bs.heat.on" : "bs.normal"),
            "light" => Loc.T(on ? "bs.light.on" : "bs.light.off"),
            "vibration" => Loc.T(on ? "bs.vibration.on" : "bs.vibration.off"),
            "running" => Loc.T(on ? "state.running" : "state.off"),
            _ => OnOff(e.State),
        };
    }

    // ── number formatting ────────────────────────────────────────────────────

    /// <summary>Number in the user's culture, at most one decimal (or the entity's display precision).</summary>
    public static string Number(double value, int? precision = null) =>
        value.ToString(precision is int p ? "F" + p : "0.#", CultureInfo.CurrentCulture);

    private static string WithUnit(HaEntity e, string state)
    {
        string text = state;
        if (e.NumericState is double d)
        {
            int? precision = e.Attributes.ContainsKey("suggested_display_precision") ? e.GetInt("suggested_display_precision") : null;
            text = Number(d, precision);
        }
        return e.GetString("unit_of_measurement") is { Length: > 0 } unit ? $"{text} {unit}" : text;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
