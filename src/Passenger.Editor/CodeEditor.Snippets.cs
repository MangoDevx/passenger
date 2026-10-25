using Avalonia;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A live template being filled in: each variable in turn has all its places selected, so what is typed fills
/// every one, Tab (or Enter) goes on to the next and Shift+Tab back, and after the last the caret goes to the
/// template's end. Escape, a caret that leaves the variable or another document shown ends it. Each place is boxed
/// while it lasts.</summary>
public sealed partial class CodeEditor
{
    private Session? snippet;
    private bool steppingField;

    /// <summary>Whether a template is being filled in.</summary>
    public bool IsInSnippet => snippet is not null;

    /// <summary>Puts a template's text over a range, its line breaks the document's, and selects its first variable's
    /// places.</summary>
    public void InsertSnippet(TextRange replace, Snippet inserted)
    {
        snippet = null;
        string eol = document.LineEnding;
        // A template is written with '\n'; in a CRLF file each such break grows by one, and every place after it moves on.
        int Placed(int offset)
        {
            int grown = 0;
            for (int i = 0; i < offset && eol != "\n"; i++)
            {
                if (inserted.Text[i] == '\n' && (i == 0 || inserted.Text[i - 1] != '\r'))
                {
                    grown += eol.Length - 1;
                }
            }
            return offset + grown;
        }
        ApplyEdits([new TextEdit(replace.Start, replace.Length, LineEndings.In(inserted.Text, eol))]);
        int at = replace.Start;
        TextRange end = TextRange.FromBounds(Placed(inserted.End.Start) + at, Placed(inserted.End.End) + at);
        snippet = new Session(
            [.. inserted.Fields.Select(f => f.Select(r => TextRange.FromBounds(Placed(r.Start) + at, Placed(r.End) + at)).ToList())],
            end);
        StepField(1);
    }

    /// <summary>Tab, Shift+Tab, Enter and Escape while a template is filled in; false for any other action.</summary>
    private bool RunSnippetKey(string action)
    {
        if (snippet is null)
        {
            return false;
        }
        switch (action)
        {
            case EditorActions.Indent or EditorActions.NewLine:
                StepField(1);
                return true;
            case EditorActions.Outdent:
                StepField(-1);
                return true;
            case EditorActions.Escape:
                snippet = null;
                SetSelections(SelectionSet.Single(selections.Primary.Caret), reveal: false);
                return true;
        }
        return false;
    }

    private void StepField(int step)
    {
        Session session = snippet!;
        int next = Math.Max(0, session.Current + step);
        steppingField = true;
        try
        {
            if (next >= session.Fields.Count)
            {
                snippet = null;
                int length = document.Snapshot.Length;
                SetSelections(SelectionSet.Single(new Selection(Math.Min(session.End.Start, length), Math.Min(session.End.End, length))), reveal: true);
                return;
            }
            session.Current = next;
            SetSelections(SelectionSet.Create([.. session.Fields[next].Select(r => new Selection(r.Start, r.End))], 0), reveal: true);
        }
        finally
        {
            steppingField = false;
        }
    }

    /// <summary>Carries the template's places through an edit; one typed into grows with it.</summary>
    private void FollowSnippet(IReadOnlyList<TextEdit> edits)
    {
        if (snippet is not { } session)
        {
            return;
        }
        foreach (List<TextRange> field in session.Fields)
        {
            for (int i = 0; i < field.Count; i++)
            {
                int start = TextEditing.MapOffset(field[i].Start, edits, afterInsertions: false);
                int end = TextEditing.MapOffset(field[i].End, edits, afterInsertions: true);
                field[i] = TextRange.FromBounds(start, Math.Max(start, end));
            }
        }
        int endStart = TextEditing.MapOffset(session.End.Start, edits, afterInsertions: session.End.Length == 0);
        session.End = TextRange.FromBounds(endStart, Math.Max(endStart, TextEditing.MapOffset(session.End.End, edits, afterInsertions: true)));
    }

    /// <summary>Ends the template when a caret goes outside the variable being filled in.</summary>
    private void CheckSnippet()
    {
        if (snippet is not { Current: >= 0 } session || steppingField)
        {
            return;
        }
        List<TextRange> places = session.Fields[session.Current];
        if (!selections.Items.All(s => places.Any(r => r.Start <= s.Start && s.End <= r.End)))
        {
            snippet = null;
            InvalidateVisual();
        }
    }

    /// <summary>A box around each place of the template, the variable being filled in in the caret's colour.</summary>
    private void DrawSnippet(DrawingContext context, TextSnapshot text, double left)
    {
        if (snippet is not { } session)
        {
            return;
        }
        for (int f = 0; f < session.Fields.Count; f++)
        {
            Pen pen = new(f == session.Current ? palette.Caret : palette.LineNumber, 1);
            foreach (TextRange place in session.Fields[f])
            {
                if (place.End > text.Length)
                {
                    continue;
                }
                int line = text.GetLineFromOffset(place.Start);
                if (line != text.GetLineFromOffset(place.End) || IsHidden(line))
                {
                    continue;
                }
                (int row, double start) = Place(text, place.Start, beforeWrap: false);
                (int endRow, double end) = Place(text, place.End, beforeWrap: true);
                if (row != endRow)
                {
                    continue;
                }
                double x1 = left + start;
                double x2 = Math.Max(x1 + 2, left + end);
                double y = (row * metrics.LineHeight) - scroll.Y;
                context.DrawRectangle(null, pen, new Rect(x1 - 1, y + 1, x2 - x1 + 2, metrics.LineHeight - 2), 2, 2);
            }
        }
    }

    /// <summary>A template's places by variable, in Tab's order, and its end: where the caret goes, or what is selected.</summary>
    private sealed class Session(List<List<TextRange>> fields, TextRange end)
    {
        public List<List<TextRange>> Fields { get; } = fields;
        public TextRange End { get; set; } = end;
        public int Current { get; set; } = -1;
    }
}
