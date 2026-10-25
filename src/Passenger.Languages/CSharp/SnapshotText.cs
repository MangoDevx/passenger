using System.Text;
using Microsoft.CodeAnalysis.Text;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Roslyn's view of a rope snapshot: read in place (the lexer copies it out in chunks), and linked to the
/// snapshot before it with the range that edit changed, so an incremental parse knows what moved without diffing.
/// Roslyn's own <c>WithChanges</c> would do instead, but in time it copies the whole file, which a keystroke in a large
/// file would wait on.</summary>
internal sealed class SnapshotText : SourceText
{
    private readonly TextSnapshot snapshot;
    private SnapshotText? previous;
    private readonly TextChangeRange change;

    public SnapshotText(TextSnapshot snapshot, SnapshotText? previous = null, TextChangeRange change = default)
    {
        this.snapshot = snapshot;
        this.previous = previous;
        this.change = change;
    }

    /// <summary>The editor's snapshot this reads.</summary>
    public TextSnapshot Snapshot => snapshot;

    public override Encoding? Encoding => null;

    public override int Length => snapshot.Length;

    public override char this[int position] => snapshot[position];

    public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) =>
        snapshot.CopyTo(sourceIndex, destination.AsSpan(destinationIndex, count));

    /// <summary>The text after an edit batch: the edits (sorted, in this text's coordinates) collapse
    /// to the one range an incremental parse needs.</summary>
    public SnapshotText With(TextSnapshot after, IReadOnlyList<TextEdit> edits)
    {
        TextChangeRange range = TextChangeRange.Collapse(edits.Select(e => new TextChangeRange(new TextSpan(e.Start, e.Length), e.Text.Length)));
        return new SnapshotText(after, this, range);
    }

    /// <summary>Lets go of every older text: once a parse has used this one, nothing will ask about
    /// the edits before it.</summary>
    public void ForgetHistory() => previous = null;

    public override IReadOnlyList<TextChangeRange> GetChangeRanges(SourceText oldText)
    {
        if (ReferenceEquals(oldText, this))
        {
            return [];
        }
        List<TextChangeRange> chain = [];
        SnapshotText? text = this;
        while (text is not null && !ReferenceEquals(text, oldText))
        {
            chain.Add(text.change);
            text = text.previous;
        }
        if (text is null)
        {
            return [new TextChangeRange(new TextSpan(0, oldText.Length), Length)];
        }
        chain.Reverse();
        TextChangeRange combined = chain[0];
        for (int i = 1; i < chain.Count; i++)
        {
            combined = Compose(combined, chain[i]);
        }
        return [combined];
    }

    public override string ToString(TextSpan span) => snapshot.GetText(span.Start, span.Length);

    /// <summary>Two edits in turn as one: <paramref name="first"/> in the oldest text's coordinates,
    /// <paramref name="second"/> in the text the first left.</summary>
    private static TextChangeRange Compose(TextChangeRange first, TextChangeRange second)
    {
        int start = Math.Min(first.Span.Start, second.Span.Start);
        int endInMiddle = Math.Max(first.Span.Start + first.NewLength, second.Span.End);
        int endInOldest = endInMiddle - (first.NewLength - first.Span.Length);
        int newLength = endInMiddle - start + second.NewLength - second.Span.Length;
        return new TextChangeRange(TextSpan.FromBounds(start, endInOldest), newLength);
    }
}
