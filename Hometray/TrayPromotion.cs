using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hometray;

/// <summary>
/// "Always show icon in the taskbar" (Windows 11): explorer keeps one subkey per
/// tray icon under HKCU\Control Panel\NotifyIconSettings\&lt;id&gt; with the values
/// ExecutablePath and IsPromoted (1 = always visible, 0 = overflow).
/// The subkey is created by explorer the first time the icon is shown, and
/// explorer picks up changes to IsPromoted immediately.
/// Not an official API - if the key is missing nothing happens.
/// </summary>
internal static partial class TrayPromotion
{
    private const string RootPath = @"Control Panel\NotifyIconSettings";

    /// <summary>Sets IsPromoted for all entries of this exe. Returns false if explorer has none (yet).</summary>
    public static bool SetPromoted(bool promoted)
    {
        bool found = false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootPath);
            if (root is null) return false;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key?.GetValue("ExecutablePath") is not string exe || !IsOwnExecutable(exe)) continue;

                found = true;
                int wanted = promoted ? 1 : 0;
                if (key.GetValue("IsPromoted") is not int current || current != wanted)
                    key.SetValue("IsPromoted", wanted, RegistryValueKind.DWord);
            }
        }
        catch
        {
            // best effort
        }
        return found;
    }

    private static bool IsOwnExecutable(string registered)
    {
        string? own = Environment.ProcessPath;
        if (string.IsNullOrEmpty(own)) return false;
        string expanded = ExpandKnownFolder(registered);
        try
        {
            return string.Equals(Path.GetFullPath(expanded), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(expanded, own, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Explorer stores e.g. "{6D809377-...}\Tool\x.exe" for known folders (Program Files, ...).</summary>
    private static string ExpandKnownFolder(string path)
    {
        var m = KnownFolderPrefix().Match(path);
        if (!m.Success || !Guid.TryParse(m.Groups[1].Value, out var id)) return path;
        if (SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out IntPtr p) != 0) return path;
        try
        {
            return Path.Combine(Marshal.PtrToStringUni(p) ?? "", m.Groups[2].Value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    [GeneratedRegex(@"^\{([0-9A-Fa-f\-]{36})\}\\(.*)$")]
    private static partial Regex KnownFolderPrefix();

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

