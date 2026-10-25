using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>Where the main menu stands on the toolbar when the system has no menu bar of its own: the hamburger button,
/// or (Settings' Show main menu as a menu bar) its menus' titles side by side, as many as the toolbar has room for, the
/// button after them holding the rest. Its first child is the button, the rest the titles in the menus' order; every
/// one is measured, and those not shown are laid out to nothing, unseen.</summary>
public sealed class MainMenuPlace : Panel, IGivesWayFirst
{
    /// <summary>Whether the titles are wanted at all; the button alone when not.</summary>
    public bool ShowsBar
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
            // Measured again alone it would keep the row's last width for it, the button's: the row must deal again.
            (Parent as Layoutable)?.InvalidateMeasure();
        }
    }

    /// <summary>How many of the menus stand on the bar as last measured; the button holds those after them.</summary>
    public int MenusShown { get; private set; }

    /// <summary>The title bar's empty space the menus go behind the button to keep, so the window can still be taken
    /// hold of and moved (on Windows, its caption).</summary>
    private const double CaptionRoom = 120;

    public double LeastWidth => Children.Count > 0 ? Children[0].DesiredSize.Width : 0;

    public double LeavesFree => ShowsBar ? CaptionRoom : 0;

    private int Titles => ShowsBar ? Children.Count - 1 : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        double all = 0;
        for (int i = 1; i <= Titles; i++)
        {
            all += Children[i].DesiredSize.Width;
        }
        double room = availableSize.Width;
        if (Titles > 0 && all <= room)
        {
            MenusShown = Titles;
            return new Size(all, height);
        }
        // Not all fit: those that do with the button after them, the button alone at the least.
        double used = LeastWidth;
        int shown = 0;
        while (shown < Titles && used + Children[shown + 1].DesiredSize.Width <= room)
        {
            used += Children[shown + 1].DesiredSize.Width;
            shown++;
        }
        MenusShown = shown;
        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 1; i < Children.Count; i++)
        {
            Place(Children[i], i <= MenusShown, ref x, finalSize.Height);
        }
        Place(Children[0], MenusShown < Titles || Titles == 0, ref x, finalSize.Height);
        return finalSize;
    }

    /// <summary>A child laid out at x when shown, else to nothing, unseen and taking no pointer and no Tab (a fixed Width
    /// keeps its bounds, so it is hidden by its opacity).</summary>
    private static void Place(Control child, bool shown, ref double x, double height)
    {
        child.Opacity = shown ? 1 : 0;
        child.IsHitTestVisible = shown;
        KeyboardNavigation.SetIsTabStop(child, shown);
        if (!shown)
        {
            child.Arrange(default);
            return;
        }
        Size size = child.DesiredSize;
        child.Arrange(new Rect(new Point(x, (height - size.Height) / 2), size));
        x += size.Width;
    }
}
