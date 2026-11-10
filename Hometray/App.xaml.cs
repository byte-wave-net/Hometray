using Hometray.HomeAssistant;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hometray;

public partial class App : Application
{
    private const string MutexName = @"Local\Hometray_SingleInstance";
    private const string ShowEventName = @"Local\Hometray_ShowWindow";

    private MainWindow? _window;          // tray flyout (left click)
    private SettingsWindow? _settings;    // settings (menu → Einstellungen)
    private AboutWindow? _about;          // about dialog (menu → About)
    private TrayController? _tray;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private bool _exiting;

    public static App Instance => (App)Current;

    internal HaClient Ha { get; } = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cli = Environment.GetCommandLineArgs().Skip(1).Select(a => a.ToLowerInvariant()).ToArray();
        Loc.Set(AppSettings.Language); // before any window or menu text is created

        // The app lives only in the notification area: no window at start, no taskbar button.
        // "--tray" (autostart entry) / the MSIX startup task are the default; "--settings" opens the settings directly.
        // MSIX build: started from the Start menu (not by the startup task) it shows the tiles, so launching
        // it from the Store / Start menu shows something instead of just a tray icon in the overflow.
        bool isAutostart = cli.Contains("--tray") || IsStartupTaskActivation();
        bool openSettings = cli.Contains("--settings") || !AppSettings.IsConfigured;
        bool openTiles = AppSettings.IsPackaged && !isAutostart && !openSettings;

        // ── Single instance ──────────────────────────────────────────────────
        _instanceMutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Started again while running: show the flyout of the running instance.
            if (!isAutostart && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
            Exit();
            return;
        }

        var dq = DispatcherQueue.GetForCurrentThread();
        // Keep running in the tray when the windows are closed.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showEvent = showEvent;
        new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (_exiting) return;
                dq.TryEnqueue(() => { if (AppSettings.IsConfigured) ShowMainWindow(); else ShowSettingsWindow(); });
            }
        }) { IsBackground = true, Name = "ShowWindowWaiter" }.Start();

        AppSettings.RefreshAutostartEntry();

        Ha.StatusChanged += () => dq.TryEnqueue(() => _tray?.Refresh());
        Ha.Configure(AppSettings.BaseUrl, AppSettings.Token);

        // The tray icon is the app's only UI entry point -> always visible.
        _tray = new TrayController(this);
        _tray.SetVisible(true);

        // Create the flyout in the background right away (hidden), so the first
        // click on the tray icon only has to show it.
        dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_exiting) _window ??= new MainWindow();
        });

        if (openSettings) ShowSettingsWindow();
        else if (openTiles) ShowMainWindow();
    }

    /// <summary>MSIX: was this start triggered by the startup task ("Start with Windows")?</summary>
    private static bool IsStartupTaskActivation()
    {
        if (!AppSettings.IsPackaged) return false;
        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind;
            return kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    // ── public API for windows / tray ────────────────────────────────────────

    internal void ShowMainWindow()
    {
        if (_exiting) return;
        _window ??= new MainWindow();
        _window.ShowAndActivate();
    }

    /// <summary>Tray icon clicked: open the flyout, or close it if it is open.</summary>
    internal void ToggleMainWindow()
    {
        if (_window is not null && (_window.AppWindow.IsVisible || _window.WasJustAutoHidden))
        {
            _window.Hide();
            return;
        }
        ShowMainWindow();
    }

    internal void ShowSettingsWindow()
    {
        if (_exiting) return;
        _window?.Hide();
        _settings ??= new SettingsWindow();
        _settings.ShowAndActivate();
    }

    /// <summary>
    /// Language changed in the settings: save it, switch the texts and rebuild all windows in the new language
    /// (their texts are set when they are created). Runs after the calling event handler has finished,
    /// because the settings window that raised the change is replaced as well.
    /// </summary>
    internal void ApplyLanguage(string setting)
    {
        AppSettings.Language = setting;
        Loc.Set(setting);

        var dq = DispatcherQueue.GetForCurrentThread();
        dq.TryEnqueue(() =>
        {
            if (_exiting) return;
            _window?.CloseForExit();
            _settings?.CloseForExit();
            _about?.CloseForExit();
            _window = null;
            _settings = null;
            _about = null;

            _tray?.Refresh();
            ShowSettingsWindow();
            dq.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!_exiting) _window ??= new MainWindow();
            });
        });
    }

    /// <summary>Opens the About dialog (tray menu → About).</summary>
    internal void ShowAboutWindow()
    {
        if (_exiting) return;
        _window?.Hide();
        _about ??= new AboutWindow();
        _about.ShowAndActivate();
    }

    /// <summary>Connection data was saved: reconnect.</summary>
    internal void ApplyConnection()
    {
        Ha.Configure(AppSettings.BaseUrl, AppSettings.Token);
        _tray?.Refresh();
    }

    /// <summary>Tile selection / column count was changed in the settings.</summary>
    internal void ApplyTiles()
    {
        _window ??= new MainWindow();
        _window.InvalidateTiles();
    }

    internal void SetAlwaysShowTrayIcon(bool alwaysShow)
    {
        if (_tray is null) AppSettings.AlwaysShowTrayIcon = alwaysShow;
        else _tray.SetAlwaysShow(alwaysShow);
    }

    /// <summary>
    /// Reports a failed action: inside the flyout while it is open (clearly visible, stays for 10 s),
    /// otherwise as a short Windows notification from the tray icon.
    /// </summary>
    internal void ShowErrorToast(string title, string message) =>
        DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
        {
            if (_window is { IsShown: true }) _window.ShowError(title, message);
            else _tray?.ShowErrorToast(title, message);
        });

    internal void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _tray?.Dispose();
        Ha.Dispose();
        _showEvent?.Set(); // release waiter thread
        _window?.CloseForExit();
        _settings?.CloseForExit();
        _about?.CloseForExit();
        Exit();
    }
}
