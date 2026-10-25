using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A toolbar's icon button (a tool window's, a diff's, a dialog's list's): its icon at the one size every icon
/// has, its tip, and what a click does. It never takes the keys from what it acts on, so the list or editor beside it
/// keeps them. One call per button, so every toolbar's buttons are alike; one that runs one of the window's actions is
/// bound by <see cref="ActionButtons"/> too.</summary>
public static class ToolButtons
{
    public static Button Of(string icon, string tip, Action? run = null)
    {
        Button button = Made(new Button(), icon, tip);
        if (run is not null)
        {
            button.Click += (_, _) => run();
        }
        return button;
    }

    /// <summary>A toolbar's button that stays pressed while what it turns on is on (Mute Breakpoints, Select Element).</summary>
    public static ToggleButton Toggle(string icon, string tip) => Made(new ToggleButton(), icon, tip);

    private static T Made<T>(T button, string icon, string tip)
        where T : Button
    {
        PathIcon glyph = new();
        glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
        button.Classes.Add("tool");
        button.Content = glyph;
        button.Focusable = false;
        ToolTip.SetTip(button, tip);
        return button;
    }
}

/// <summary>Words of less weight beside what a row or a page says (a count, a hint, where something is): the muted
/// text's colour, centred in its row, ending in an ellipsis where it has no room. Its page sets what it needs beyond that
/// (a width, a margin, a caption's smaller size).</summary>
public static class MutedText
{
    public static TextBlock Of(string text) =>
        new() { Classes = { "muted" }, Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>A muted note that wraps to its column, as a page's explanations do.</summary>
    public static TextBlock Wrapped(string text)
    {
        TextBlock note = Of(text);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }
}
