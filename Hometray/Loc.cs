using System.Globalization;

namespace Hometray;

/// <summary>
/// UI language: the eight supported languages, the current one, and lookup of texts from
/// <see cref="Strings"/>. Numbers and dates follow the chosen language as well.
/// </summary>
internal static class Loc
{
    internal sealed record Language(string Code, string Native, string Culture);

    /// <summary>Order = column order of every row in <see cref="Strings.Table"/>.</summary>
    public static readonly Language[] Languages =
    {
        new("de", "Deutsch", "de-DE"),
        new("en", "English", "en-US"),
        new("es", "Español", "es-ES"),
        new("zh", "中文", "zh-CN"),     // Simplified Chinese
        new("ru", "Русский", "ru-RU"),
        new("pt", "Português", "pt-BR"), // Brazilian Portuguese
        new("it", "Italiano", "it-IT"),
        new("sv", "Svenska", "sv-SE"),
    };

    public const string Auto = "auto";
    private const int EnglishIndex = 1;
    private static int _index = EnglishIndex;

    public static string Code => Languages[_index].Code;

    /// <summary>"auto" or a language code → index into <see cref="Languages"/> (auto = system language, English if unsupported).</summary>
    public static int Resolve(string? setting)
    {
        if (setting is not null && setting != Auto)
        {
            int index = Array.FindIndex(Languages, l => l.Code == setting);
            if (index >= 0) return index;
        }
        string system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        int found = Array.FindIndex(Languages, l => l.Code == system);
        return found >= 0 ? found : EnglishIndex;
    }

    /// <summary>Selects the language ("auto" or a code) and switches number / date formats with it.</summary>
    public static void Set(string? setting)
    {
        _index = Resolve(setting);
        var culture = CultureInfo.GetCultureInfo(Languages[_index].Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Text for a key in the current language (falls back to English, then German; unknown key = the key itself).</summary>
    public static string T(string key)
    {
        if (!Strings.Table.TryGetValue(key, out var row)) return key;
        if (!string.IsNullOrEmpty(row[_index])) return row[_index];
        if (!string.IsNullOrEmpty(row[EnglishIndex])) return row[EnglishIndex];
        return row[0];
    }

    public static string T(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);
}
