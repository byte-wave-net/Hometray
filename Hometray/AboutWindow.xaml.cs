using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Hometray;

/// <summary>
/// About dialog (tray menu → About), after the Claude Design artboard "About Hometray":
/// 720 × 396 without title bar, animated wave backdrop; dragged by the background.
/// Tray-only app: no taskbar button; "Schließen" / Esc only hide it.
/// </summary>
public sealed partial class AboutWindow : Window
{
    private const int WidthDip = 720;
    private const int HeightDip = 396; // design 440 minus the removed 44 px title bar

    private bool _closingForExit;

    public AboutWindow()
    {
        InitializeComponent();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(true, false); // no title bar
        }
        AppWindow.IsShownInSwitchers = false;
        WindowHelper.SetAppIcon(this);
        WindowHelper.UseRoundedCorners(this);
        WindowHelper.SetBorderColor(this, 0x1E, 0x2A, 0x3A); // design: border #1E2A3A

        VersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";

        AppWindow.Closing += (_, e) =>
        {
            if (_closingForExit) return;
            e.Cancel = true;
            HideDialog();
        };

        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) HideDialog();
        };
    }

    internal void ShowAndActivate()
    {
        CenterOnScreen();
        AppWindow.Show();
        Activate();
        WindowHelper.BringToFront(this);
        StartAnimations();
        CloseButton.Focus(FocusState.Programmatic);
    }

    internal void CloseForExit()
    {
        _closingForExit = true;
        StopAnimations();
        Close();
    }

    private void HideDialog()
    {
        StopAnimations();
        AppWindow.Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideDialog();

    /// <summary>No title bar: dragging the background moves the window
    /// (the Close button handles its own press, so it is not affected).</summary>
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed) return;
        WindowHelper.BeginDrag(this);
    }

    // ── animations (respect "Animation effects" off in Windows) ─────────────

    private IEnumerable<Storyboard> Storyboards => new[] { DriftA, DriftB, Glow, PulseA, PulseB, Halo };

    private void StartAnimations()
    {
        bool enabled = true;
        try { enabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch { }
        if (!enabled) return;
        foreach (var sb in Storyboards) sb.Begin();
    }

    private void StopAnimations()
    {
        foreach (var sb in Storyboards) sb.Stop();
    }

    private void CenterOnScreen()
    {
        double scale = WindowHelper.GetScale(this);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = (int)(WidthDip * scale), height = (int)(HeightDip * scale);
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height));
    }
}

