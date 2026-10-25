using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Languages;

/// <summary>Colours found for an older version of a text, carried to the text as it stands through the edits made since,
/// so a highlighter still working shows its last colours where the text kept them and typing never flashes plain.</summary>
internal static class CarriedSpans
{
    /// <summary>Takes a change's one edit into the last change of a history, as the version it made, when that too was one
    /// edit and the two touch (a run of typing or deleting in one place): a history that grows while colours are slow to
    /// come (a solution loading) then holds one change for the run, which every range drawn is carried through, rather than
    /// one for each key. The caller says the last change may go: nothing shown or asked for is of the text it made.</summary>
    public static bool Join(List<(int Version, IReadOnlyList<TextEdit> Edits)> history, int version, IReadOnlyList<TextEdit> edits)
    {
        if (history.Count == 0 || edits.Count != 1 || history[^1].Edits.Count != 1)
        {
            return false;
        }
        TextEdit last = history[^1].Edits[0];
        TextEdit next = edits[0];
        // Where the last edit's text stands in the text it made.
        int start = last.Start;
        int end = start + last.Text.Length;
        if (next.Start > end || next.End < start)
        {
            return false;
        }
        string text = string.Concat(
            next.Start > start ? last.Text.AsSpan(0, next.Start - start) : [],
            next.Text,
            next.End < end ? last.Text.AsSpan(next.End - start) : []);
        int joinedEnd = next.End > end ? last.End + (next.End - end) : last.End;
        int joinedStart = Math.Min(start, next.Start);
        history[^1] = (version, [new TextEdit(joinedStart, joinedEnd - joinedStart, text)]);
        return true;
    }

    /// <summary>An offset after <paramref name="batch"/> as it was before it. Inside inserted text it
    /// lands at the insertion's start, or past the replaced text for the end of a range.</summary>
    public static int Back(IReadOnlyList<TextEdit> batch, int offset, bool toEnd)
    {
        int shift = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            TextEdit edit = batch[i];
            int start = edit.Start + shift;
            int end = start + edit.Text.Length;
            if (offset < start)
            {
                break;
            }
            if (offset <= end)
            {
                return toEnd ? edit.End : edit.Start;
            }
            shift += edit.Text.Length - edit.Length;
        }
        return offset - shift;
    }

    /// <summary>Carries colours through a batch of edits (sorted, in the old text's coordinates), in place. A span text is
    /// deleted inside shrinks with it, and one text is typed inside grows with it when <paramref name="grows"/> takes that
    /// text (any, without one); one an edit crosses is dropped until the next pass brings it back.</summary>
    public static void Forward(List<SyntaxSpan> spans, IReadOnlyList<TextEdit> batch, Func<string, bool>? grows = null)
    {
        int kept = 0;
        int e = 0;
        int delta = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            SyntaxSpan span = spans[i];
            while (e < batch.Count && batch[e].End <= span.Start)
            {
                delta += batch[e].Text.Length - batch[e].Length;
                e++;
            }
            if (e >= batch.Count || batch[e].Start > span.End || (batch[e].Start == span.End && batch[e].Length > 0))
            {
                spans[kept++] = span with { Start = span.Start + delta };
                continue;
            }
            TextEdit edit = batch[e];
            bool alone = e + 1 >= batch.Count || batch[e + 1].Start > span.End;
            if (alone && edit.Length == 0 && edit.Start > span.Start && grows?.Invoke(edit.Text) != false)
            {
                spans[kept++] = new SyntaxSpan(span.Start + delta, span.Length + edit.Text.Length, span.Kind);
                delta += edit.Text.Length;
                e++;
            }
            else if (alone && edit.Text.Length == 0 && edit.Start >= span.Start && edit.End <= span.End)
            {
                if (span.Length > edit.Length)
                {
                    spans[kept++] = new SyntaxSpan(span.Start + delta, span.Length - edit.Length, span.Kind);
                }
                delta -= edit.Length;
                e++;
            }
        }
        spans.RemoveRange(kept, spans.Count - kept);
    }
}
