using System.Collections;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A document's inlay hints in the order the editor draws them (by offset, at one offset a hint that ends the
/// text before it ahead of one that begins the text after), kept in runs that the lists carried through later edits
/// share: an edit moves hint by hint only the runs it reaches, and a run wholly after it by one shift, so a key moves
/// the hints near it rather than every hint of the file. Never changed once made; the arrays it is made from are
/// shared, never copied.</summary>
public sealed class HintList : IReadOnlyList<InlayHint>
{
    private const int RunLength = 64;

    public static readonly HintList Empty = new([], 0);

    /// <summary>Every run <see cref="RunLength"/> hints long but the last, so a hint's run is its index's quotient.</summary>
    private readonly Run[] runs;

    private HintList(Run[] runs, int count)
    {
        this.runs = runs;
        Count = count;
    }

    public int Count { get; }

    public InlayHint this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return runs[index / RunLength].At(index % RunLength);
        }
    }

    /// <summary>The hints as a list of runs in the order they are drawn: the list itself when it is one, else sorted only
    /// when they are not in order already.</summary>
    public static HintList Of(IReadOnlyList<InlayHint> hints)
    {
        if (hints is HintList list)
        {
            return list;
        }
        if (hints.Count == 0)
        {
            return Empty;
        }
        InlayHint[] all = [.. hints];
        if (!InOrder(all))
        {
            all = [.. all.OrderBy(h => h.Offset).ThenBy(Rank)];
        }
        return new HintList(Cut(all, 0, all.Length), all.Length);
    }

    /// <summary>Whether a hint ends the text before it (a type, a value), which goes with that text through an edit at its
    /// offset and is drawn ahead of a parameter's name there, which begins the text after it.</summary>
    public static bool Trails(InlayHint hint) => hint.Kind != InlayHintKind.Parameter;

    /// <summary>The hints carried through edits (sorted and apart, in this list's offsets): text typed at a parameter's name
    /// goes after it, into the argument; at a type or a value, before it, onto what it ends. The runs the edits reach are
    /// moved hint by hint (sorted again should the edits have put them out of order), those wholly before them kept and
    /// those wholly after them shifted whole.</summary>
    public HintList Through(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0 || Count == 0)
        {
            return this;
        }
        int from = edits[0].Start;
        int to = edits[^1].Start + edits[^1].Length;
        int shift = 0;
        foreach (TextEdit edit in edits)
        {
            shift += edit.Text.Length - edit.Length;
        }
        // The hints are in order, so the runs the edits reach lie side by side, those before and after them untouched.
        int first = -1;
        int last = -1;
        for (int k = 0; k < runs.Length; k++)
        {
            Run run = runs[k];
            if (run.Highest + run.Shift >= from && run.Lowest + run.Shift <= to)
            {
                first = first < 0 ? k : first;
                last = k;
            }
        }
        Run[] moved = new Run[runs.Length];
        for (int k = 0; k < runs.Length; k++)
        {
            Run run = runs[k];
            moved[k] = run.Highest + run.Shift < from ? run : run with { Shift = run.Shift + shift };
        }
        if (first >= 0)
        {
            int start = first * RunLength;
            int end = (last * RunLength) + runs[last].Hints.Count;
            InlayHint[] reached = new InlayHint[end - start];
            for (int i = 0; i < reached.Length; i++)
            {
                InlayHint hint = this[start + i];
                reached[i] = hint with { Offset = TextEditing.MapOffset(hint.Offset, edits, afterInsertions: Trails(hint)) };
            }
            if (!InOrder(reached))
            {
                // Hints at one place told apart by the side they go: inside a replaced range, at an insertion.
                reached = [.. reached.OrderBy(h => h.Offset).ThenBy(Rank)];
            }
            Cut(reached, 0, reached.Length).CopyTo(moved, first);
        }
        return new HintList(moved, Count);
    }

    /// <summary>The offsets of the hints one list draws otherwise than the other (where they stand, what they say, their
    /// padding or kind), those of both where they differ; null when they draw alike. Lists carried through the same edits
    /// from one share their runs, compared by what they share, so a key's hints cost the runs it reached.</summary>
    public static List<int>? Differing(HintList before, HintList after)
    {
        List<int>? offsets = null;
        if (before.Count == after.Count && before.runs.Length == after.runs.Length)
        {
            for (int k = 0; k < before.runs.Length; k++)
            {
                Run was = before.runs[k];
                Run now = after.runs[k];
                if (was.Same(now))
                {
                    continue;
                }
                for (int i = 0; i < was.Hints.Count; i++)
                {
                    InlayHint a = was.At(i);
                    InlayHint b = now.At(i);
                    if (!DrawnAlike(a, b))
                    {
                        (offsets ??= []).Add(a.Offset);
                        offsets.Add(b.Offset);
                    }
                }
            }
            return offsets;
        }
        // Lists of other hints: a merge of the two in drawing order, each hint drawn by one alone told.
        int x = 0;
        int y = 0;
        while (x < before.Count && y < after.Count)
        {
            InlayHint a = before[x];
            InlayHint b = after[y];
            if (DrawnAlike(a, b))
            {
                x++;
                y++;
                continue;
            }
            int order = a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : Rank(a).CompareTo(Rank(b));
            if (order <= 0)
            {
                (offsets ??= []).Add(a.Offset);
                x++;
            }
            if (order >= 0)
            {
                (offsets ??= []).Add(b.Offset);
                y++;
            }
        }
        for (; x < before.Count; x++)
        {
            (offsets ??= []).Add(before[x].Offset);
        }
        for (; y < after.Count; y++)
        {
            (offsets ??= []).Add(after[y].Offset);
        }
        return offsets;
    }

    public IEnumerator<InlayHint> GetEnumerator()
    {
        foreach (Run run in runs)
        {
            for (int i = 0; i < run.Hints.Count; i++)
            {
                yield return run.At(i);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Whether two hints are drawn the same: the same words of the same kind at the same place.</summary>
    private static bool DrawnAlike(InlayHint a, InlayHint b) =>
        a.Offset == b.Offset && a.Kind == b.Kind && a.PadLeft == b.PadLeft && a.PadRight == b.PadRight && string.Equals(a.Label, b.Label, StringComparison.Ordinal);

    /// <summary>A hint's place among those at its offset: one that ends the text before it first.</summary>
    private static int Rank(InlayHint hint) => Trails(hint) ? 0 : 1;

    private static bool InOrder(InlayHint[] hints)
    {
        for (int i = 1; i < hints.Length; i++)
        {
            InlayHint before = hints[i - 1];
            InlayHint hint = hints[i];
            if (hint.Offset < before.Offset || (hint.Offset == before.Offset && Rank(hint) < Rank(before)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Hints cut into runs of <see cref="RunLength"/>, the last shorter.</summary>
    private static Run[] Cut(InlayHint[] hints, int from, int count)
    {
        Run[] made = new Run[((count - 1) / RunLength) + 1];
        for (int k = 0; k < made.Length; k++)
        {
            int start = from + (k * RunLength);
            made[k] = Run.Of(new ArraySegment<InlayHint>(hints, start, Math.Min(RunLength, from + count - start)));
        }
        return made;
    }

    /// <summary>Hints as they stood when the run was made, and what edits wholly before them have added to their offsets
    /// since; the lowest and highest offset among them as made.</summary>
    private readonly record struct Run(ArraySegment<InlayHint> Hints, int Shift, int Lowest, int Highest)
    {
        public static Run Of(ArraySegment<InlayHint> hints)
        {
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            foreach (InlayHint hint in hints)
            {
                lowest = Math.Min(lowest, hint.Offset);
                highest = Math.Max(highest, hint.Offset);
            }
            return new Run(hints, 0, lowest, highest);
        }

        public InlayHint At(int index)
        {
            InlayHint hint = Hints[index];
            return Shift == 0 ? hint : hint with { Offset = hint.Offset + Shift };
        }

        /// <summary>Whether two runs are the one run, shifted alike: both carried from one list through the same edits.</summary>
        public bool Same(Run other) => ReferenceEquals(Hints.Array, other.Hints.Array) && Hints.Offset == other.Hints.Offset
            && Hints.Count == other.Hints.Count && Shift == other.Shift;
    }
}
