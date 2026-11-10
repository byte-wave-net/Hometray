using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hometray;

/// <summary>
/// Minimal Win32 notification-area icon (WinUI 3 has no NotifyIcon).
/// Owns a hidden window for callbacks, re-adds itself after an explorer
/// restart ("TaskbarCreated") and shows native popup menus.
/// Must be created and used on the UI thread (it needs its message loop).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public sealed record MenuItem(int Id, string Text, bool Checked = false, bool Enabled = true, bool IsDefault = false)
    {
        public static MenuItem Separator { get; } = new(0, "-");
        public bool IsSeparator => Text == "-";
    }

    /// <summary>Left click / Enter on the icon.</summary>
    public event Action? Clicked;
    /// <summary>Right click / context-menu key. Coordinates are screen pixels.</summary>
    public event Action<int, int>? ContextMenuRequested;
    /// <summary>explorer.exe (re)created the taskbar.</summary>
    public event Action? TaskbarCreated;
    /// <summary>Light/dark theme or other immersive colors changed.</summary>
    public event Action? ThemeChanged;

    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly WndProcDelegate _wndProc; // keep alive for the native side
    private readonly uint _taskbarCreatedMsg;
    private readonly string _className = "HometrayWindow_" + Environment.ProcessId;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private string _tooltip = "";
    private bool _visible;
    private bool _added;

    public TrayIcon()
    {
        _wndProc = WndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new Win32Exception();

        // A real (hidden) top-level window: message-only windows do not get
        // the "TaskbarCreated" broadcast.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, _className, "Hometray", WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception();

        TryEnableDarkMenus();
    }

    public bool Visible => _visible;

    /// <summary>Sets icon and tooltip. Takes ownership of <paramref name="hIcon"/>.</summary>
    public void Update(IntPtr hIcon, string tooltip)
    {
        IntPtr old = _hIcon;
        _hIcon = hIcon;
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (_added) Notify(NIM_MODIFY, NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void Show()
    {
        _visible = true;
        AddIcon();
    }

    public void Hide()
    {
        _visible = false;
        if (_added) Notify(NIM_DELETE, 0);
        _added = false;
    }

    /// <summary>Windows notification (toast) from the tray icon.</summary>
    public void ShowBalloon(string title, string text, bool error = false)
    {
        if (!_added) return;
        var data = CreateData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = error ? NIIF_ERROR : NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Removes a notification shown with <see cref="ShowBalloon"/>.</summary>
    public void HideBalloon()
    {
        if (!_added) return;
        var data = CreateData(NIF_INFO); // empty szInfo = remove
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a native popup menu and returns the selected id (0 = none).</summary>
    public int ShowMenu(int x, int y, IEnumerable<MenuItem> items)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                uint flags = MF_STRING;
                if (item.Checked) flags |= MF_CHECKED;
                if (!item.Enabled) flags |= MF_GRAYED;
                AppendMenuW(menu, flags, (UIntPtr)(uint)item.Id, item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)item.Id, 0);
            }

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    // ── internals ────────────────────────────────────────────────────────────

    private void AddIcon()
    {
        if (_added || _hwnd == IntPtr.Zero) return;
        if (Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP))
        {
            var data = CreateData(0);
            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
            _added = true;
        }
    }

    private bool Notify(uint message, uint flags)
    {
        var data = CreateData(flags);
        return Shell_NotifyIconW(message, ref data);
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor x/y
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                int x = (short)(wParam.ToInt64() & 0xFFFF);
                int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                switch (ev)
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                        Clicked?.Invoke();
                        break;
                    case WM_CONTEXTMENU:
                        ContextMenuRequested?.Invoke(x, y);
                        break;
                }
                return IntPtr.Zero;
            }

            if (msg == _taskbarCreatedMsg)
            {
                _added = false;
                if (_visible) AddIcon();
                TaskbarCreated?.Invoke();
                return IntPtr.Zero;
            }

            if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                ThemeChanged?.Invoke();
            }
        }
        catch
        {
            // never let exceptions escape into native code
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Dark context menus when Windows is in dark mode (undocumented uxtheme ordinals 135/136).</summary>
    private static void TryEnableDarkMenus()
    {
        try
        {
            IntPtr uxtheme = LoadLibraryExW("uxtheme.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (uxtheme == IntPtr.Zero) return;
            IntPtr setMode = GetProcAddress(uxtheme, (IntPtr)135);
            IntPtr flush = GetProcAddress(uxtheme, (IntPtr)136);
            if (setMode != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setMode)(1 /* AllowDark */);
            if (flush != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flush)();
        }
        catch
        {
            // cosmetic only
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetPreferredAppModeDelegate(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FlushMenuThemesDelegate();

    private const uint WM_NULL = 0x0000, WM_CONTEXTMENU = 0x007B, WM_SETTINGCHANGE = 0x001A, WM_APP = 0x8000;
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x01, NIIF_ERROR = 0x03, NOTIFYICON_VERSION_4 = 4;
    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    private const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
}

