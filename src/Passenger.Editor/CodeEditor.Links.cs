using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>A stretch of text a click opens something from (a console's file location or web address): its range, and
/// what it opens, which only the host reads.</summary>
public sealed record TextLink(TextRange Range, object Target);

/// <summary>Links: the host finds a line's links (<see cref="Links"/>), each drawn underlined in the link colour, the
/// pointer over one takes the hand, and a click on it that selects nothing opens it (<see cref="LinkClicked"/>); a drag
/// that begins on one selects as anywhere else.</summary>
public sealed partial class CodeEditor
{
    private Func<TextSnapshot, int, IReadOnlyList<TextLink>>? links;

    /// <summary>The link a press began on, opened by the release if it lands on it with nothing selected.</summary>
    private TextLink? pressedLink;

    /// <summary>The links on a line (from zero) of a text, in its offsets, as the host finds them; null for an editor with
    /// none. Asked for each line drawn and the line under the pointer as it moves, so the host keeps what it found; the
    /// host's highlighter gives them the link colour.</summary>
    public Func<TextSnapshot, int, IReadOnlyList<TextLink>>? Links
    {
        get => links;
        set
        {
            links = value;
            HoveredLink = null;
        }
    }

    /// <summary>The link under the pointer, which a click opens; null when it is over none.</summary>
    public TextLink? HoveredLink { get; private set; }

    /// <summary>A link was clicked: the host opens what it names.</summary>
    public event EventHandler<TextLink>? LinkClicked;

    /// <summary>The link under a point in the control: the one holding the character the point is over, never one the
    /// row ends before.</summary>
    private TextLink? LinkAt(Point point)
    {
        if (links is null || point.X < GutterWidth || point.Y + scroll.Y < 0 || RowAtY(point.Y) >= RowCount)
        {
            return null;
        }
        TextSnapshot text = document.Snapshot;
        (int line, int row) = Rows.LineOfRow(RowAtY(point.Y));
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        RowSpan span = RowSpanOf(text, line, row);
        LineView view = ViewOf(text, span);
        Gap[] gaps = GapsOn(span);
        double x = point.X - TextLeft + scroll.X - span.Indent;
        if (x < 0 || x >= XWith(view, gaps, span.Length, afterHints: false))
        {
            return null;
        }
        int column = ColumnWith(view, gaps, x, span.Length);
        if (column > 0 && XWith(view, gaps, column) > x)
        {
            // The nearest boundary is after the character the point is over.
            column--;
        }
        int offset = span.Start + column;
        foreach (TextLink link in links(text, line))
        {
            if (link.Range.Start <= offset && offset < link.Range.End)
            {
                return link;
            }
        }
        return null;
    }

    /// <summary>The pointer moved: over a link, if no drag is under way, it takes the hand.</summary>
    private void FollowLinks(Point position, bool dragging)
    {
        if (links is null)
        {
            return;
        }
        HoveredLink = dragging ? null : LinkAt(position);
        if (HoveredLink is not null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    /// <summary>A press with no keys held, once: the link it is on, which its release opens.</summary>
    private void PressLink(Point position, KeyModifiers modifiers, int clicks) =>
        pressedLink = modifiers == KeyModifiers.None && clicks == 1 ? LinkAt(position) : null;

    /// <summary>A release: the link the press began on is opened if the release is on it and nothing was selected between.</summary>
    private void ReleaseLink(Point position)
    {
        TextLink? pressed = pressedLink;
        pressedLink = null;
        if (pressed is not null && selections.Primary.IsEmpty && !selections.HasMultiple && LinkAt(position)?.Range == pressed.Range)
        {
            LinkClicked?.Invoke(this, pressed);
        }
    }

    /// <summary>The links held carried through an edit, as output arrives and the oldest goes; one an edit touched is gone.</summary>
    private void FollowLinkEdits(IReadOnlyList<TextEdit> edits)
    {
        HoveredLink = Carried(HoveredLink);
        pressedLink = Carried(pressedLink);

        TextLink? Carried(TextLink? link)
        {
            if (link is null)
            {
                return null;
            }
            TextRange moved = TextEditing.MapRange(link.Range, edits);
            return moved.Length == link.Range.Length ? link with { Range = moved } : null;
        }
    }

    /// <summary>The links' underlines on a line, in the link's underline colour, under the text's baseline.</summary>
    private void DrawLinks(DrawingContext context, TextSnapshot text, LineView view, Gap[] gaps, double left, double y)
    {
        if (links is null)
        {
            return;
        }
        double under = Math.Round(y + ((metrics.LineHeight - view.Height) / 2) + view.Baseline) + 1.5;
        Pen? pen = null;
        foreach (TextLink link in links(text, view.Span.Line))
        {
            int from = Math.Max(link.Range.Start, view.Span.Start) - view.Span.Start;
            int to = Math.Min(link.Range.End, view.Span.End) - view.Span.Start;
            if (to > from)
            {
                pen ??= new Pen(palette.LinkUnderline, 1);
                context.DrawLine(pen, new Point(left + XWith(view, gaps, from), under), new Point(left + XWith(view, gaps, to, afterHints: false), under));
            }
        }
    }
}
