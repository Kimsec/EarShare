using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EarShare.UI;

/// <summary>Draws the tray/window icon at runtime so no binary asset has to ship with the source.</summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(bool active)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = active ? Color.FromArgb(46, 204, 113) : Color.FromArgb(168, 168, 168);

            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new[]
            {
                new Point(3, 12), new Point(10, 12), new Point(17, 5),
                new Point(17, 27), new Point(10, 20), new Point(3, 20),
            });

            using var pen = new Pen(color, 2.5f);
            g.DrawArc(pen, 18, 10, 7, 12, -55, 110);
            g.DrawArc(pen, 21, 6, 11, 20, -55, 110);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
