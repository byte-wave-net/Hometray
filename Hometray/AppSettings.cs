using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Hometray;

/// <summary>
/// One entry of the tile list: either a tile (entity + optional custom name) or a group header
/// (<see cref="Group"/> set, no entity). A header applies to all tiles after it, up to the next header;
/// tiles before the first header are ungrouped.
/// </summary>
internal sealed class TileConfig
{
    [JsonPropertyName("id")] public string EntityId { get; set; } = "";
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("group"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Group { get; set; }

    [JsonIgnore] public bool IsGroupHeader => Group is not null;
}

/// <summary>
/// Settings in %AppData%\Hometray\settings.json. The access token is stored
/// encrypted with DPAPI (only this Windows user can decrypt it).
/// Autostart is the HKCU Run entry "Hometray".
/// </summary>
internal static class AppSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Hometray";

    // Name before the product was renamed to Hometray: settings folder and autostart value are taken over once.
    private const string OldName = "HomeAssistantTray";

    public const int MinColumns = 2, MaxColumns = 4;

    private sealed class Data
    {
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
        [JsonPropertyName("token")] public string EncryptedToken { get; set; } = "";
        [JsonPropertyName("columns")] public int Columns { get; set; } = 3;
        [JsonPropertyName("alwaysShowTrayIcon")] public bool AlwaysShowTrayIcon { get; set; } = true;
        [JsonPropertyName("pinHintShown")] public bool PinHintShown { get; set; }
        [JsonPropertyName("confirmCritical")] public bool ConfirmCritical { get; set; } = true;
        [JsonPropertyName("language")] public string Language { get; set; } = Loc.Auto;
        [JsonPropertyName("tiles")] public List<TileConfig> Tiles { get; set; } = new();
        /// <summary>Remembered inputs of scripts: entity id → {field → value} (see ScriptInput).</summary>
        [JsonPropertyName("scriptInputs")] public Dictionary<string, JsonObject> ScriptInputs { get; set; } = new();
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hometray", "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static Data _data = Load();

    /// <summary>Moves %AppData%\HomeAssistantTray\settings.json to the new folder (only if there is no new file yet).</summary>
    private static void MigrateOldSettings()
    {
        try
        {
            string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
            string oldFile = Path.Combine(oldDir, "settings.json");
            if (File.Exists(FilePath) || !File.Exists(oldFile)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.Move(oldFile, FilePath); // the DPAPI-encrypted token stays valid: same user, no extra entropy
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
        }
        catch
        {
            // not critical: the app then starts without the old settings
        }
    }

    private static Data Load()
    {
        MigrateOldSettings();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch
        {
            // unreadable / corrupt: start with defaults
        }
        return new Data();
    }

    private static void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true); // atomic: never a half-written file
        }
    }

    public static string BaseUrl => _data.BaseUrl;

    public static string Token
    {
        get
        {
            if (_data.EncryptedToken.Length == 0) return "";
            try
            {
                byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(_data.EncryptedToken), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return ""; // e.g. settings copied from another PC / user
            }
        }
    }

    public static bool IsConfigured => _data.BaseUrl.Length > 0 && Token.Length > 0;

    public static void SetConnection(string baseUrl, string token)
    {
        _data.BaseUrl = baseUrl.Trim();
        _data.EncryptedToken = token.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
        Save();
    }

    public static int Columns
    {
        get => Math.Clamp(_data.Columns, MinColumns, MaxColumns);
        set { _data.Columns = Math.Clamp(value, MinColumns, MaxColumns); Save(); }
    }

    public static bool AlwaysShowTrayIcon
    {
        get => _data.AlwaysShowTrayIcon;
        set { _data.AlwaysShowTrayIcon = value; Save(); }
    }

    /// <summary>UI language: "auto" (system language) or one of the codes in <see cref="Loc.Languages"/>.</summary>
    public static string Language
    {
        get => string.IsNullOrEmpty(_data.Language) ? Loc.Auto : _data.Language;
        set { _data.Language = value; Save(); }
    }

    /// <summary>Ask before unlocking a lock or moving a garage door / gate / door drive.</summary>
    public static bool ConfirmCriticalActions
    {
        get => _data.ConfirmCritical;
        set { _data.ConfirmCritical = value; Save(); }
    }

    public static bool PinHintShown
    {
        get => _data.PinHintShown;
        set { _data.PinHintShown = value; Save(); }
    }

    public static IReadOnlyList<TileConfig> Tiles => _data.Tiles;

    public static void SetTiles(IEnumerable<TileConfig> tiles)
    {
        _data.Tiles = tiles.Select(t => t.IsGroupHeader
            ? new TileConfig { Group = t.Group!.Trim() }
            : new TileConfig { EntityId = t.EntityId, Name = string.IsNullOrWhiteSpace(t.Name) ? null : t.Name.Trim() }).ToList();

        // remembered script inputs of tiles that are gone are not kept
        var ids = _data.Tiles.Where(t => !t.IsGroupHeader).Select(t => t.EntityId).ToHashSet();
        foreach (string id in _data.ScriptInputs.Keys.Where(k => !ids.Contains(k)).ToList())
            _data.ScriptInputs.Remove(id);
        Save();
    }

    // ── remembered script inputs ─────────────────────────────────────────────

    public static bool HasScriptInputs(string entityId) => _data.ScriptInputs.ContainsKey(entityId);

    /// <summary>A copy of the remembered inputs of a script (null = none remembered).</summary>
    public static JsonObject? GetScriptInputs(string entityId) =>
        _data.ScriptInputs.TryGetValue(entityId, out var values) ? (JsonObject)values.DeepClone() : null;

    /// <summary>Remembers the inputs of a script (an empty object = "run without inputs"); null forgets them.</summary>
    public static void SetScriptInputs(string entityId, JsonObject? values)
    {
        if (values is null) _data.ScriptInputs.Remove(entityId);
        else _data.ScriptInputs[entityId] = values;
        Save();
    }

    // ── Autostart (HKCU Run / MSIX startup task) ─────────────────────────────

    private const string StartupTaskId = "HometrayStartup"; // TaskId in Package.appxmanifest

    /// <summary>True when running from the MSIX package (Microsoft Store build).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    private static bool DetectPackage()
    {
        const int AppModelErrorNoPackage = 15700;
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    public static bool AutostartEnabled
    {
        get
        {
            if (IsPackaged)
            {
                var state = StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult().State;
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Switches autostart on / off. Returns an error text if Windows refused (MSIX: the user or a policy
    /// disabled the startup task in Windows), null on success.
    /// </summary>
    public static async Task<string?> SetAutostartAsync(bool enable)
    {
        if (!IsPackaged)
        {
            AutostartEnabled = enable;
            return null;
        }

        var task = await StartupTask.GetAsync(StartupTaskId);
        if (!enable)
        {
            task.Disable();
            return null;
        }
        return await task.RequestEnableAsync() switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
            StartupTaskState.DisabledByPolicy => Loc.T("settings.err.autostartPolicy"),
            _ => Loc.T("settings.err.autostartDisabled"),
        };
    }

    /// <summary>Keeps an existing autostart entry pointing at the current exe (after moving the app).</summary>
    public static void RefreshAutostartEntry()
    {
        if (IsPackaged) return; // startup task, no Run entry
        try
        {
            // an autostart entry under the old name becomes the entry of this app
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                if (key?.GetValue(OldName) is string)
                {
                    key.DeleteValue(OldName, throwOnMissingValue: false);
                    AutostartEnabled = true;
                }
            }

            if (AutostartEnabled) AutostartEnabled = true;
        }
        catch
        {
            // not critical
        }
    }
}
