using Hometray.HomeAssistant;
using Microsoft.UI.Dispatching;

namespace Hometray;

/// <summary>Notification-area icon (house, cross when not connected) and its context menu.</summary>
internal sealed class TrayController : IDisposable
{
    private enum Cmd
    {
        Open = 1,
        OpenHa,
        Settings,
        About,
        Exit,
    }

    private readonly App _app;
    private readonly TrayIcon _icon = new();
    private readonly DispatcherQueueTimer _promoteTimer;
    private int _promoteAttempts;
    private string? _lastState;

    public TrayController(App app)
    {
        _app = app;
        _icon.Clicked += () => _app.ToggleMainWindow();
        _icon.ContextMenuRequested += ShowMenu;
        _icon.ThemeChanged += () => Refresh(force: true);
        _icon.TaskbarCreated += ApplyPromotion;

        // explorer creates the NotifyIconSettings entry asynchronously after the
        // icon was added -> retry for a while.
        _promoteTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _promoteTimer.Interval = TimeSpan.FromSeconds(1);
        _promoteTimer.Tick += (_, _) =>
        {
            if (TrayPromotion.SetPromoted(AppSettings.AlwaysShowTrayIcon) || ++_promoteAttempts >= 30)
                _promoteTimer.Stop();
        };

        Refresh();
    }

    public bool Visible => _icon.Visible;

    public void SetVisible(bool visible)
    {
        if (visible == _icon.Visible) return;
        if (visible)
        {
            Refresh();
            _icon.Show();
            ApplyPromotion();
            if (!AppSettings.PinHintShown)
            {
                AppSettings.PinHintShown = true;
                _icon.ShowBalloon(Loc.T("tray.balloon.title"), Loc.T("tray.balloon.text"));
            }
        }
        else
        {
            _icon.Hide();
        }
    }

    /// <summary>Applies the "always show in taskbar" setting to explorer.</summary>
    public void ApplyPromotion()
    {
        // MSIX: the registry is virtualized, explorer would never see the change (the user pins the icon in Windows)
        if (!_icon.Visible || AppSettings.IsPackaged) return;
        _promoteAttempts = 0;
        if (!TrayPromotion.SetPromoted(AppSettings.AlwaysShowTrayIcon))
            _promoteTimer.Start();
    }

    /// <summary>Changes the "always show in taskbar" option. Returns false if explorer had no entry yet.</summary>
    public bool SetAlwaysShow(bool alwaysShow)
    {
        AppSettings.AlwaysShowTrayIcon = alwaysShow;
        if (!_icon.Visible || AppSettings.IsPackaged) return true;
        bool ok = TrayPromotion.SetPromoted(alwaysShow);
        if (!ok) ApplyPromotion();
        return ok;
    }

    public void Refresh() => Refresh(force: false);

    /// <summary>Re-renders icon and tooltip (only if something changed).</summary>
    private void Refresh(bool force)
    {
        var ha = _app.Ha;
        bool connected = ha.Status == HaStatus.Connected;
        string tooltip = ha.Status switch
        {
            HaStatus.Connected => ha.LocationName is { } n ? Loc.T("tray.tip.connectedAt", n) : "Home Assistant",
            HaStatus.Connecting => Loc.T("tray.tip.connecting"),
            HaStatus.AuthFailed => Loc.T("tray.tip.authFailed"),
            HaStatus.Disconnected => Loc.T("tray.tip.disconnected") + (ha.StatusMessage is { } m ? "\n" + m : ""),
            _ => Loc.T("tray.tip.notConfigured"),
        };

        string state = $"{connected}|{tooltip}";
        if (!force && state == _lastState) return;

        try
        {
            _icon.Update(TrayIconRenderer.Create(connected), tooltip);
            _lastState = state;
        }
        catch
        {
            // keep the previous icon
        }
    }

    private void ShowMenu(int x, int y)
    {
        var items = new List<TrayIcon.MenuItem>
        {
            new((int)Cmd.Open, Loc.T("menu.showTiles"), IsDefault: true),
            new((int)Cmd.OpenHa, Loc.T("menu.openHa"), Enabled: HaClient.NormalizeBaseUri(AppSettings.BaseUrl) is not null),
            new((int)Cmd.Settings, Loc.T("menu.settings")),
            new((int)Cmd.About, Loc.T("menu.about")),
            TrayIcon.MenuItem.Separator,
            new((int)Cmd.Exit, Loc.T("menu.exit")),
        };

        switch ((Cmd)_icon.ShowMenu(x, y, items))
        {
            case Cmd.Open:
                _app.ShowMainWindow();
                break;
            case Cmd.OpenHa:
                if (HaClient.NormalizeBaseUri(AppSettings.BaseUrl) is { } uri) WindowHelper.Launch(uri.ToString());
                break;
            case Cmd.Settings:
                _app.ShowSettingsWindow();
                break;
            case Cmd.About:
                _app.ShowAboutWindow();
                break;
            case Cmd.Exit:
                _app.ExitApp();
                break;
        }
    }

    private DispatcherQueueTimer? _toastTimer;

    /// <summary>Error notification that disappears again after 3 seconds.</summary>
    public void ShowErrorToast(string title, string message)
    {
        _icon.ShowBalloon(title, message, error: true);
        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private DispatcherQueueTimer CreateToastTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _icon.HideBalloon();
        return timer;
    }

    public void Dispose()
    {
        _toastTimer?.Stop();
        _promoteTimer.Stop();
        _icon.Dispose();
    }
}
