using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hometray;

/// <summary>
/// Renders the tray icon at runtime from the Windows icon font (Segoe Fluent Icons /
/// Segoe MDL2 Assets) in the taskbar's text color, in the style of the Windows system
/// tray icons. A small cross at the bottom right marks "not connected".
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
    private const string HomeGlyph = "";

    /// <summary>Returns an HICON (caller owns it / pass it to <see cref="TrayIcon.Update"/>).</summary>
    public static IntPtr Create(bool connected)
    {
        int size = GetTrayIconSize();
        Color color = IsTaskbarLight() ? Color.Black : Color.White;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // Glyph at its design size (em = icon size), centered on whole pixels,
            // hinted so the 1 px lines stay crisp.
            using var family = FindIconFont();
            using var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);

            float offsetX = 0, offsetY = 0;
            using (var path = new GraphicsPath())
            {
                path.AddString(HomeGlyph, family, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF bounds = path.GetBounds();
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    offsetX = MathF.Round((size - bounds.Width) / 2f - bounds.X);
                    offsetY = MathF.Round((size - bounds.Height) / 2f - bounds.Y);
                }
            }

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            g.DrawString(HomeGlyph, font, brush, new PointF(offsetX, offsetY), StringFormat.GenericTypographic);
        }

        BoostAlpha(bmp, factor: 1.4f);

        if (!connected)
        {
            using var overlay = Graphics.FromImage(bmp);
            overlay.SmoothingMode = SmoothingMode.AntiAlias;
            overlay.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawCross(overlay, size, color);
        }

        return bmp.GetHicon();
    }

    private static void BoostAlpha(Bitmap bmp, float factor)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            for (int i = 3; i < bytes; i += 4) // BGRA, alpha is byte 3
                buffer[i] = (byte)Math.Min(255, (int)(buffer[i] * factor));
            Marshal.Copy(buffer, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>"x" badge in the bottom-right quarter with a transparent gap around it.</summary>
    private static void DrawCross(Graphics g, int size, Color color)
    {
        float badge = size * 0.56f;
        float x0 = size - badge, y0 = size - badge;
        float gap = size / 16f * 1.2f;

        // Cut a hole into the glyph so the cross stays readable.
        var oldMode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
            g.FillEllipse(clear, x0 - gap, y0 - gap, badge + 2 * gap, badge + 2 * gap);
        g.CompositingMode = oldMode;

        float inset = badge * 0.18f;
        float stroke = size / 16f * 1.5f;
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, x0 + inset, y0 + inset, size - inset, size - inset);
        g.DrawLine(pen, size - inset, y0 + inset, x0 + inset, size - inset);
    }

    private static FontFamily FindIconFont()
    {
        foreach (var name in IconFonts)
        {
            try
            {
                var family = new FontFamily(name);
                if (family.Name == name) return family;
                family.Dispose();
            }
            catch (ArgumentException)
            {
                // font not installed
            }
        }
        return new FontFamily("Segoe UI Symbol");
    }

    /// <summary>Taskbar theme: HKCU\...\Themes\Personalize\SystemUsesLightTheme (0 = dark).</summary>
    private static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    private static int GetTrayIconSize()
    {
        const int SM_CXSMICON = 49;
        try
        {
            int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            return size > 0 ? size : 16;
        }
        catch
        {
            return 16;
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
