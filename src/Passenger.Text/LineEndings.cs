namespace Passenger.Text;

/// <summary>Text written with other line breaks than a document's, made the document's: a paste, or what a language
/// server writes.</summary>
public static class LineEndings
{
    /// <summary>Text with each of its breaks ("\r\n" or "\n") written as <paramref name="eol"/>.</summary>
    public static string In(string text, string eol) =>
        text.Contains('\n', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal) : text;

    /// <summary>Edits to a text, sorted, with every break they write, or make with the text beside them, in the text's
    /// <paramref name="eol"/>: a server that diffs its own text ends an edit at the '\r' of a "\r\n" it wrote where the
    /// text has '\n', or begins one with the '\n' after a '\r' the text has. Edits that touch are made one, so a break
    /// split between them is seen whole. An edit that comes to nothing in the text's breaks is left out.</summary>
    public static List<TextEdit> Fit(TextSnapshot text, IEnumerable<TextEdit> edits, string eol)
    {
        List<TextEdit> joined = [];
        foreach (TextEdit edit in edits.Where(e => e.Length > 0 || e.Text.Length > 0).OrderBy(e => e.Start))
        {
            if (joined.Count > 0 && joined[^1].End == edit.Start)
            {
                joined[^1] = new TextEdit(joined[^1].Start, edit.End - joined[^1].Start, joined[^1].Text + edit.Text);
            }
            else
            {
                joined.Add(edit);
            }
        }
        List<TextEdit> fitted = [];
        foreach (TextEdit edit in joined)
        {
            int start = edit.Start;
            string written = edit.Text;
            bool lineFeedAfter = edit.End < text.Length && text[edit.End] == '\n';
            char first = written.Length > 0 ? written[0] : lineFeedAfter ? '\n' : '\0';
            if (start > 0 && text[start - 1] == '\r' && first == '\n')
            {
                start--;
                written = "\r" + written;
            }
            // A '\r' written before the text's '\n' is the first half of one break with it, so it stays only for "\r\n".
            string made = written.EndsWith('\r') && lineFeedAfter ? In(written[..^1], eol) + eol[..^1] : In(written, eol);
            if (edit.End - start != made.Length || text.GetText(start, made.Length) != made)
            {
                fitted.Add(new TextEdit(start, edit.End - start, made));
            }
        }
        return fitted;
    }
}
