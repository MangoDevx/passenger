using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.App.Workspace;

/// <summary>An XML file's text (a nuget.config, a project, a .slnx) and the elements in it, changed in place: each change
/// is a splice of the text, after which the elements are read from it again, so an element is looked up afresh for each
/// change. Only the elements changed are written, indented as their neighbours are; every other character stays as it
/// was. Comments are no elements, so what one holds is never found or changed.</summary>
internal sealed class XmlText
{
    private XElement root = null!;
    private int[] lines = [];

    public XmlText(string text)
    {
        Text = text;
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Parse();
        // The step one level of the file is indented by, as its root's first child has it.
        string step = root.Elements().FirstOrDefault() is { } first ? Indent(first) : string.Empty;
        Unit = step.Length > Indent(root).Length ? step[Indent(root).Length..] : "  ";
    }

    public string Text { get; private set; }

    public string NewLine { get; }

    public string Unit { get; }

    /// <summary>The root element as the text now reads.</summary>
    public XElement Root => root;

    /// <summary>The root's child of a name, whatever namespace the file gives its elements.</summary>
    public XElement? Section(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>A value as an attribute's quotes hold it.</summary>
    public static string Escape(string value) => SecurityElement.Escape(value);

    /// <summary>The indent a section's children have: its last child's, else one step in from the section.</summary>
    public string ChildIndent(string section) => Section(section) is not { } found
        ? Indent(root) + Unit + Unit
        : found.Elements().LastOrDefault() is { } last && Indent(last) is { Length: > 0 } own ? own : Indent(found) + Unit;

    /// <summary>Adds an element at the end of a section, the section made at the root's end if there is none.</summary>
    public void AddChild(string section, string element)
    {
        if (Section(section) is not { } found)
        {
            string indent = Indent(root) + Unit;
            Insert(root, $"<{section}>{NewLine}{indent}{Unit}{element}{NewLine}{indent}</{section}>", indent);
            return;
        }
        Insert(found, element, ChildIndent(section));
    }

    /// <summary>Puts an element after another: on the same line when more follows it there, else on a line of its own
    /// indented as it is.</summary>
    public void After(XElement element, string with)
    {
        int end = Span(element).End;
        int lineEnd = Text.IndexOfAny(['\r', '\n'], end);
        if (Text[end..(lineEnd < 0 ? Text.Length : lineEnd)].Trim().Length > 0)
        {
            Splice(end, 0, with);
            return;
        }
        Splice(end, 0, NewLine + LineIndent(Span(element).Start) + with);
    }

    public void Replace(XElement element, string with)
    {
        (int start, int end) = Span(element);
        Splice(start, end - start, with);
    }

    /// <summary>Sets one attribute in an element's start tag, the rest of the tag as it was.</summary>
    public void SetAttribute(XElement element, string name, string value)
    {
        (int start, _) = Span(element);
        int end = TagEnd(start);
        string tag = Text[start..end];
        int at = tag.IndexOf($" {name}=", StringComparison.Ordinal);
        if (at < 0)
        {
            int close = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
            Splice(start + close, 0, $" {name}=\"{Escape(value)}\"");
            return;
        }
        int open = at + name.Length + 2;
        char quote = tag[open];
        int shut = tag.IndexOf(quote, open + 1);
        Splice(start + open + 1, shut - open - 1, Escape(value));
    }

    /// <summary>Takes an element out, with its line when it stands on one of its own.</summary>
    public void Remove(XElement element)
    {
        (int start, int end) = Span(element);
        int from = start;
        while (from > 0 && Text[from - 1] is ' ' or '\t')
        {
            from--;
        }
        int to = end;
        while (to < Text.Length && Text[to] is ' ' or '\t')
        {
            to++;
        }
        if ((from == 0 || Text[from - 1] == '\n') && (to == Text.Length || Text[to] is '\r' or '\n'))
        {
            to += to < Text.Length && Text[to] == '\r' ? 1 : 0;
            to += to < Text.Length && Text[to] == '\n' ? 1 : 0;
            Splice(from, to - from, string.Empty);
        }
        else
        {
            Splice(start, end - start, string.Empty);
        }
    }

    /// <summary>Writes an element as a parent's last child, on a line of its own at the indent given.</summary>
    private void Insert(XElement parent, string element, string indent)
    {
        (int start, int end) = Span(parent);
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            // An empty element written short is written out, with the child inside it.
            string head = Text[start..(open - 2)].TrimEnd();
            Splice(start, end - start, $"{head}>{NewLine}{indent}{element}{NewLine}{Indent(parent)}</{parent.Name.LocalName}>");
            return;
        }
        if (parent.Elements().LastOrDefault() is { } last)
        {
            Splice(Span(last).End, 0, NewLine + indent + element);
            return;
        }
        int close = Text.LastIndexOf("</", end - 1, StringComparison.Ordinal);
        string inside = Text[open..close];
        Splice(open, close - open, string.IsNullOrWhiteSpace(inside)
            ? $"{NewLine}{indent}{element}{NewLine}{Indent(parent)}"
            : $"{inside.TrimEnd(' ', '\t')}{indent}{element}{NewLine}{Indent(parent)}");
    }

    private void Splice(int at, int length, string with)
    {
        Text = string.Concat(Text.AsSpan(0, at), with, Text.AsSpan(at + length));
        Parse();
    }

    private void Parse()
    {
        root = XDocument.Parse(Text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace).Root
            ?? throw new XmlException("The file has no root element");
        List<int> starts = [0];
        for (int i = 0; i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        lines = [.. starts];
    }

    /// <summary>The whitespace the line holding a place begins with, never what stands on it before the place.</summary>
    private string LineIndent(int at)
    {
        int from = Text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int end = from;
        while (end < at && Text[end] is ' ' or '\t')
        {
            end++;
        }
        return Text[from..end];
    }

    /// <summary>The whitespace before an element on its line; empty when other text comes before it there.</summary>
    private string Indent(XElement element)
    {
        int start = Span(element).Start;
        int from = start;
        while (from > 0 && Text[from - 1] is ' ' or '\t')
        {
            from--;
        }
        return from == 0 || Text[from - 1] == '\n' ? Text[from..start] : string.Empty;
    }

    /// <summary>Where an element's text begins (its &lt;) and ends (after its end tag, or its /&gt;).</summary>
    private (int Start, int End) Span(XElement element)
    {
        IXmlLineInfo at = element;
        int start = lines[at.LineNumber - 1] + at.LinePosition - 1;
        while (start > 0 && Text[start] != '<')
        {
            start--;
        }
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            return (start, open);
        }
        int depth = 1;
        int i = open;
        while (i < Text.Length)
        {
            ReadOnlySpan<char> rest = Text.AsSpan(i);
            if (rest.StartsWith("<!--"))
            {
                i = Text.IndexOf("-->", i, StringComparison.Ordinal) + 3;
            }
            else if (rest.StartsWith("<![CDATA["))
            {
                i = Text.IndexOf("]]>", i, StringComparison.Ordinal) + 3;
            }
            else if (rest.StartsWith("<?"))
            {
                i = Text.IndexOf("?>", i, StringComparison.Ordinal) + 2;
            }
            else if (rest.StartsWith("</"))
            {
                int closed = TagEnd(i);
                if (--depth == 0)
                {
                    return (start, closed);
                }
                i = closed;
            }
            else if (Text[i] == '<')
            {
                int tag = TagEnd(i);
                depth += Text[tag - 2] == '/' ? 0 : 1;
                i = tag;
            }
            else
            {
                i++;
            }
        }
        throw new XmlException($"The {element.Name.LocalName} element has no end");
    }

    /// <summary>The position after a tag's &gt;, past what its quoted attribute values hold.</summary>
    private int TagEnd(int start)
    {
        char? quote = null;
        for (int i = start + 1; i < Text.Length; i++)
        {
            char c = Text[i];
            if (quote is { } open)
            {
                quote = c == open ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }
        throw new XmlException("A tag has no end");
    }
}
