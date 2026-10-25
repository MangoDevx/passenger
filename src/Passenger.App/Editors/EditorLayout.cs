namespace Passenger.App.Editors;

/// <summary>The editors' splits as a folder keeps them for next time: a group of tabs (the one it showed, whether the
/// person worked in it), or a split into two parts beside each other (<c>right</c>) or one over the other
/// (<c>down</c>), the first part's share of the room with it.</summary>
public sealed record EditorLayout
{
    public string? Split { get; init; }
    public double Ratio { get; init; } = 0.5;
    public EditorLayout? First { get; init; }
    public EditorLayout? Second { get; init; }

    public List<KeptTab>? Tabs { get; init; }
    public int Shown { get; init; }
    public bool Current { get; init; }
}

/// <summary>A tab as kept: its file, where its caret was (from 0), and whether it was pinned.</summary>
public sealed record KeptTab(string Path, int Line, int Column)
{
    public bool Pinned { get; init; }
}

/// <summary>An editor window as a folder keeps it: where it stood (its place in screen pixels, its size in its own
/// units) and its splits.</summary>
public sealed record KeptEditorWindow
{
    public int[]? Bounds { get; init; }
    public EditorLayout? Editors { get; init; }
}
