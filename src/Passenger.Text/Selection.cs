namespace Passenger.Text;

/// <summary>A caret and the end of its selection. <see cref="Anchor"/> stays put while
/// <see cref="Caret"/> moves; they are equal when nothing is selected. <see cref="PreferredX"/> is the
/// horizontal position a vertical move keeps returning to, in the view's units, or NaN for none.</summary>
public readonly record struct Selection(int Anchor, int Caret, double PreferredX = double.NaN)
{
    public static Selection At(int offset) => new(offset, offset);

    public int Start => Math.Min(Anchor, Caret);
    public int End => Math.Max(Anchor, Caret);
    public int Length => End - Start;
    public bool IsEmpty => Anchor == Caret;
    public TextRange Range => TextRange.FromBounds(Start, End);

    public Selection MoveTo(int offset, bool extend) => new(extend ? Anchor : offset, offset);
}

/// <summary>Every caret in an editor, sorted by position, with overlapping selections merged and the
/// most recently added one primary. Immutable: each change returns a new set.</summary>
public sealed class SelectionSet
{
    /// <summary>The most carets an editor keeps: every key acts at each, and past this a key would take longer than a
    /// person waits.</summary>
    public const int MaxCount = 1000;

    private SelectionSet(Selection[] items, int primary)
    {
        Items = items;
        PrimaryIndex = primary;
    }

    public IReadOnlyList<Selection> Items { get; }
    public int PrimaryIndex { get; }
    public Selection Primary => Items[PrimaryIndex];
    public int Count => Items.Count;
    public bool HasMultiple => Items.Count > 1;

    public static SelectionSet Single(Selection selection) => new([selection], 0);

    public static SelectionSet Single(int offset) => Single(Selection.At(offset));

    /// <summary>Sorts and merges <paramref name="selections"/>; the one at
    /// <paramref name="primary"/> (an index into the input) stays primary through the merge.</summary>
    public static SelectionSet Create(IReadOnlyList<Selection> selections, int primary)
    {
        if (selections.Count == 0)
        {
            throw new ArgumentException("A selection set needs at least one caret.", nameof(selections));
        }
        int[] order = Enumerable.Range(0, selections.Count).ToArray();
        Array.Sort(order, (a, b) =>
        {
            int byStart = selections[a].Start.CompareTo(selections[b].Start);
            return byStart != 0 ? byStart : selections[a].End.CompareTo(selections[b].End);
        });

        List<Selection> merged = [];
        int primaryOut = 0;
        foreach (int index in order)
        {
            Selection next = selections[index];
            if (merged.Count > 0 && Touches(merged[^1], next))
            {
                Selection last = merged[^1];
                int start = Math.Min(last.Start, next.Start);
                int end = Math.Max(last.End, next.End);
                bool forward = (index == primary ? next : last).Caret >= (index == primary ? next : last).Anchor;
                merged[^1] = forward ? new Selection(start, end) : new Selection(end, start);
            }
            else
            {
                merged.Add(next);
            }
            if (index == primary)
            {
                primaryOut = merged.Count - 1;
            }
        }
        return new SelectionSet([.. merged], primaryOut);
    }

    public SelectionSet With(Func<Selection, Selection> change)
    {
        Selection[] changed = new Selection[Items.Count];
        for (int i = 0; i < changed.Length; i++)
        {
            changed[i] = change(Items[i]);
        }
        return Create(changed, PrimaryIndex);
    }

    public SelectionSet Add(Selection selection) => Create([.. Items, selection], Items.Count);

    /// <summary>The set without the caret at <paramref name="offset"/>, or this set if removing it
    /// would leave none.</summary>
    public SelectionSet RemoveAt(int offset)
    {
        List<Selection> kept = Items.Where(s => !(s.IsEmpty ? s.Caret == offset : s.Start <= offset && offset <= s.End)).ToList();
        return kept.Count == 0 || kept.Count == Items.Count ? this : Create(kept, kept.Count - 1);
    }

    public SelectionSet OnlyPrimary() => Single(Primary);

    /// <summary>Every offset kept valid for a shorter or longer snapshot.</summary>
    public SelectionSet Clamp(int length) =>
        With(s => new Selection(Math.Clamp(s.Anchor, 0, length), Math.Clamp(s.Caret, 0, length), s.PreferredX));

    public bool SameAs(SelectionSet other) => Items.SequenceEqual(other.Items);

    // With a.Start <= b.Start: overlapping selections merge, and so does a caret at a selection's edge
    // (two carets at one offset included); two selections that only meet stay apart.
    private static bool Touches(Selection a, Selection b) =>
        b.Start < a.End || (b.Start == a.End && (a.IsEmpty || b.IsEmpty));
}
