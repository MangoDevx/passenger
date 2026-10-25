namespace Passenger.App;

/// <summary>What every popup over the window shares (Find Action, Go to File, Find in Files, a Git choice, a
/// breakpoint's properties): the frame (Shell.axaml's <c>Border.popup</c>), a width from one scale, the title's look
/// (<c>TextBlock.popupTitle</c>), and its list's rows of <see cref="ListRow.Height"/>. A popup takes its width from
/// here, never a number of its own.</summary>
public static class PopupFrame
{
    /// <summary>A name to type (New Branch, Stash Changes).</summary>
    public const double Narrow = 360;

    /// <summary>A short list to choose from or a few fields (a Git choice, Unstash, a breakpoint's properties).</summary>
    public const double Medium = 560;

    /// <summary>A search over the solution (Search Everywhere's Find Action, Go to File and Symbol, Recent Locations).</summary>
    public const double Wide = 720;

    /// <summary>A search with a preview or a list beside its details (Find in Files, Breakpoints).</summary>
    public const double Widest = 860;

    /// <summary>Every width a popup may have.</summary>
    public static IReadOnlyList<double> Widths { get; } = [Narrow, Medium, Wide, Widest];
}
