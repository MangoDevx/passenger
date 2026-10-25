using Avalonia;
using Avalonia.Controls;
using Passenger.App.Docking;

namespace Passenger.App.Editors;

/// <summary>Editor tabs in a window of their own, made by Open Tab in New Window or a tab dragged out of the editors:
/// its own tabs and splits over the folder's open files, which every window shows alike (one text, saved once). Its
/// last tab closed, it goes; closed, its tabs close.</summary>
public sealed class EditorWindow
{
    private readonly EditorFrame frame;
    private readonly FloatWindow window;

    internal EditorWindow(EditorFrame frame, FloatWindow window)
    {
        this.frame = frame;
        this.window = window;
    }

    public Window Window => window;

    /// <summary>Its groups of tabs, in the order of its splits.</summary>
    public IReadOnlyList<EditorGroup> Groups => [.. frame.Groups];

    /// <summary>Where it stands: its place in screen pixels, its size in its own units.</summary>
    internal PixelRect Placement => window.Placement;

    internal void CloseQuietly() => window.CloseQuietly();
}
