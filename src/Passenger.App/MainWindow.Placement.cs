using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Passenger.App;

public partial class MainWindow
{
    /// <summary>Fits the window to the screen it opens on before it is shown (the one under its place when it has one,
    /// else the primary): see <see cref="FitWithin"/>.</summary>
    public void FitToScreen(bool keepPlace)
    {
        Screen? screen = (keepPlace ? Screens.ScreenFromPoint(Position) : null) ?? Screens.Primary;
        if (screen is not null)
        {
            FitWithin(screen.WorkingArea, screen.Scaling, keepPlace);
        }
    }

    /// <summary>Fits the window inside a screen's working area (in its pixels, at its scaling): no larger than the area,
    /// down to the window's smallest, and wholly on it, centred unless it keeps its place, when it is only moved back onto
    /// the area (a window cascaded past its edge, or one placed on a larger screen). Before the window is shown.</summary>
    public void FitWithin(PixelRect area, double scaling, bool keepPlace)
    {
        double width = Math.Max(SmallestWidth, Math.Min(double.IsNaN(Width) ? SmallestWidth : Width, area.Width / scaling));
        double height = Math.Max(SmallestHeight, Math.Min(double.IsNaN(Height) ? SmallestHeight : Height, area.Height / scaling));
        (Width, Height) = (width, height);
        PixelSize size = PixelSize.FromSize(new Size(width, height), scaling);
        PixelPoint at = keepPlace ? Position : new PixelPoint(area.X + ((area.Width - size.Width) / 2), area.Y + ((area.Height - size.Height) / 2));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(
            Math.Clamp(at.X, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(at.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
    }
}
