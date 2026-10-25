namespace Passenger.Text;

/// <summary>Which markup a file is, for the editing that reads its tags: XML (XAML, MSBuild), or HTML, whose void
/// elements never close and whose script and style hold text that is not markup.</summary>
public enum MarkupKind
{
    None,
    Xml,
    Html,
}

/// <summary>The indentation Enter gives a new line in markup, from the tags before the caret: a level in from the
/// element the caret is inside, the element's own on a line its end tag starts, and under a tag's first attribute
/// inside the tag. HTML's elements whose end tag may be left out (li, p, td, ...) close as its parser has them.</summary>
internal static class MarkupIndent
{
    /// <summary>The text an XML file's Enter reads back from the caret first, then four times as much, and so on, until it
    /// reaches the elements that hold the caret: a key costs the elements it is in, not the file before it.</summary>
    private const int FirstReach = 4096;

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "textarea", "title" };

    /// <summary>The start tags that close an open p (HTML's "close a p element" in button scope).</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "dd", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "listing",
        "main", "menu", "nav", "ol", "p", "plaintext", "pre", "search", "section", "summary", "table", "ul", "xmp",
    };

    /// <summary>The elements no implied end reaches past (most of HTML's scope markers).</summary>
    private static readonly string[] Scopes = ["html", "body", "table", "td", "th", "caption", "template", "object", "applet", "marquee"];

    /// <summary>The new line's indentation for Enter at <paramref name="at"/> whose line goes on at <paramref name="rest"/>,
    /// with the end tag's own line's when the caret sits between a start tag and an end tag; null where Enter keeps the
    /// line's indentation (in a comment, a quoted value, a declaration, a script).</summary>
    public static (string Indent, string? EndTagIndent)? ForNewLine(TextSnapshot text, int at, int rest, int limit, MarkupKind kind, IndentOptions indent)
    {
        bool endTagFollows = limit - rest >= 2 && text[rest] == '<' && text[rest + 1] == '/';
        string? following = endTagFollows ? EndTagAt(text, rest, limit) : null;
        // HTML's left-out end tags depend on what holds an element as far out as the file's start; XML's tags pair up.
        Place place = kind == MarkupKind.Xml ? Back(text, at, following) : Read(text.GetText(0, at), 0, kind, following);
        switch (place.Kind)
        {
            case PlaceKind.Content:
            {
                string parent = place.Parent < 0 ? string.Empty : LineIndent(text, place.Parent);
                string child = place.Parent < 0 ? string.Empty : parent + indent.Unit;
                if (!endTagFollows)
                {
                    return (child, null);
                }
                // The end tag goes to its own element's indentation, which an HTML element's left-out end tag makes an outer one's.
                string closing = place.Closer < 0 ? parent : LineIndent(text, place.Closer);
                return place.Opened ? (child, closing) : (closing, null);
            }
            case PlaceKind.Tag:
            {
                LineSpan line = text.GetLine(text.GetLineFromOffset(place.Tag));
                return place.Attribute >= 0
                    ? (indent.Whitespace(indent.Columns(text.GetText(line.Start, place.Attribute - line.Start))), null)
                    : (LineIndent(text, place.Tag) + indent.Unit, null);
            }
            default:
                return null;
        }
    }

    private enum PlaceKind
    {
        Content,
        Tag,
        Other,
    }

    /// <summary>Where a caret is among the tags: in an element's content (<see cref="Parent"/> its start tag's offset, -1 at
    /// the top; <see cref="Opened"/> when only whitespace follows that start tag; <see cref="Closer"/> the start tag of the
    /// open element the end tag after the caret closes, -1 with none), in a start tag (<see cref="Tag"/> its offset;
    /// <see cref="Attribute"/> its first attribute's, when that is on the tag's line before the caret), or other.</summary>
    private readonly record struct Place(PlaceKind Kind, int Parent = -1, bool Opened = false, int Tag = -1, int Attribute = -1, int Closer = -1);

    /// <summary>The name of the end tag that starts at <paramref name="at"/>, null while it has none yet.</summary>
    private static string? EndTagAt(TextSnapshot text, int at, int limit)
    {
        int end = at + 2;
        while (end < limit && end - at < 256 && IsNameChar(text[end]))
        {
            end++;
        }
        return end > at + 2 ? text.GetText(at + 2, end - at - 2) : null;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>An XML file's place read back from the caret, a stretch at a time, as far as the elements that hold it; read
    /// from the file's start as a whole where what it found back there does not pair up (an end tag with no start).</summary>
    private static Place Back(TextSnapshot text, int at, string? following)
    {
        for (int reach = FirstReach; ; reach *= 4)
        {
            int from = Math.Max(0, at - reach);
            string s = text.GetText(from, at - from);
            if (Back(s, from, from == 0, following) is { } place && (from == 0 || !Closes(text.GetText(at, Math.Min(reach, text.Length - at)))))
            {
                return place;
            }
            if (from == 0)
            {
                return Read(s, 0, MarkupKind.Xml, following);
            }
        }
    }

    /// <summary>Whether the text after the caret ends a comment or CDATA section it does not begin, one the caret may be in
    /// though its beginning is further back than was read. Only a caret further than a stretch from both ends of one (an
    /// inline task's long code) is read as the markup it holds.</summary>
    private static bool Closes(string after) => Ends(after, "<!--", "-->") || Ends(after, "<![CDATA[", "]]>");

    private static bool Ends(string after, string opener, string closer)
    {
        int end = after.IndexOf(closer, StringComparison.Ordinal);
        return end >= 0 && after.AsSpan(0, end).IndexOf(opener, StringComparison.Ordinal) < 0;
    }

    /// <summary>Whether the '&lt;' at <paramref name="lt"/> follows a tag that has ended (its '>' outside its quoted values,
    /// which MSBuild's item transforms put '->' in), not one it would be inside of (typed among another tag's attributes);
    /// unknown, and so false, where the stretch holds no tag before it.</summary>
    private static bool AfterTag(string s, int lt, bool whole)
    {
        int before = lt == 0 ? -1 : s.LastIndexOf('<', lt - 1);
        if (before < 0)
        {
            return whole;
        }
        string? closer = s.AsSpan(before).StartsWith("<!--") ? "-->" : s.AsSpan(before).StartsWith("<![CDATA[") ? "]]>" : s.AsSpan(before).StartsWith("<?") ? "?>" : null;
        if (closer is not null)
        {
            return s.IndexOf(closer, before + 2, lt - before - 2, StringComparison.Ordinal) >= 0;
        }
        return TagEnd(s, before + 1, lt).End < lt;
    }

    /// <summary>The place at the end of <paramref name="s"/> (the text from <paramref name="from"/> to the caret), read tag
    /// by tag backwards; null where it needs the text before <paramref name="s"/>, or (<paramref name="whole"/>) where the
    /// tags do not pair up, which only reading forwards from the start settles.</summary>
    private static Place? Back(string s, int from, bool whole, string? following)
    {
        // The caret inside a comment, CDATA section or processing instruction begun in the stretch: the line's indentation.
        foreach ((string opener, string ender) in (ReadOnlySpan<(string, string)>)[("<!--", "-->"), ("<![CDATA[", "]]>"), ("<?", "?>")])
        {
            if (Unclosed(s, opener, ender) is >= 0 and var begun)
            {
                return AfterTag(s, begun, whole) ? new Place(PlaceKind.Other) : null;
            }
        }
        List<string> closed = [];
        int innermost = -1;
        int inside = -1;
        int p = s.Length;
        while (true)
        {
            int lt = p == 0 ? -1 : s.LastIndexOf('<', p - 1);
            if (lt < 0)
            {
                // The file's start: whatever was found open, or the top level; an end tag left unpaired needs the forward read.
                return !whole || closed.Count > 0 ? null : Found(innermost, -1);
            }
            // A comment, CDATA section or processing instruction that ends before what was read after it, or a '<' inside one.
            if (Within(s, lt, p, "<!--", "-->") is { } comment)
            {
                if (comment < 0)
                {
                    return null;
                }
                p = comment;
                continue;
            }
            if ((Within(s, lt, p, "<![CDATA[", "]]>") ?? Within(s, lt, p, "<?", "?>")) is { } section)
            {
                if (section < 0)
                {
                    return null;
                }
                p = section;
                continue;
            }
            // Any other '<' follows a tag that has ended: one among a tag's attributes is that tag's, which reads forwards.
            if (!AfterTag(s, lt, whole))
            {
                return null;
            }
            if (s.AsSpan(lt).StartsWith("<!"))
            {
                // A declaration (DOCTYPE), whose '>' ends it before what was read after it.
                if (s.IndexOf('>', lt + 2, p - lt - 2) < 0)
                {
                    return p == s.Length ? new Place(PlaceKind.Other) : null;
                }
                p = lt;
                continue;
            }
            bool endTag = lt + 1 < p && s[lt + 1] == '/';
            int nameStart = lt + (endTag ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < p && IsNameChar(s[nameEnd]))
            {
                nameEnd++;
            }
            if (nameEnd == nameStart)
            {
                // A '<' just typed is a tag begun, where Enter keeps the line's indentation; any other is text.
                if (nameStart >= s.Length)
                {
                    return new Place(PlaceKind.Other);
                }
                p = lt;
                continue;
            }
            (int gt, char quote, int attribute) = TagEnd(s, nameEnd, p);
            if (gt == p)
            {
                // A tag that runs into what was read after it was misread; one that runs to the caret holds it.
                if (p != s.Length)
                {
                    return null;
                }
                bool onTagLine = attribute >= 0 && s.AsSpan(lt, attribute - lt).IndexOfAny('\r', '\n') < 0;
                return quote != '\0' || endTag ? new Place(PlaceKind.Other) : new Place(PlaceKind.Tag, Tag: from + lt, Attribute: onTagLine ? from + attribute : -1);
            }
            p = lt;
            string name = s[nameStart..nameEnd];
            if (endTag)
            {
                closed.Add(name);
                continue;
            }
            if (s[gt - 1] == '/')
            {
                continue;
            }
            if (closed.Count > 0)
            {
                // An end tag that is not this element's: only the whole file read forwards says which it closes.
                if (!string.Equals(closed[^1], name, StringComparison.Ordinal))
                {
                    return null;
                }
                closed.RemoveAt(closed.Count - 1);
                continue;
            }
            // Open at the caret: the innermost first, then those holding it until the one the end tag after the caret closes.
            if (innermost < 0)
            {
                innermost = lt;
                inside = gt + 1;
            }
            if (following is null || string.Equals(name, following, StringComparison.Ordinal))
            {
                return Found(innermost, following is null ? -1 : lt);
            }
        }

        Place Found(int start, int closer) => start < 0
            ? new Place(PlaceKind.Content)
            : new Place(PlaceKind.Content, Parent: from + start, Opened: s.AsSpan(inside).IsWhiteSpace(), Closer: closer < 0 ? -1 : from + closer);
    }

    /// <summary>Where the last <paramref name="opener"/> in <paramref name="s"/> is when no <paramref name="closer"/> follows
    /// it, else -1.</summary>
    private static int Unclosed(string s, string opener, string closer)
    {
        int open = s.LastIndexOf(opener, StringComparison.Ordinal);
        return open >= 0 && s.IndexOf(closer, open + opener.Length, StringComparison.Ordinal) < 0 ? open : -1;
    }

    /// <summary>Where the construct holding a '&lt;' at <paramref name="lt"/> begins, when one ends between it and
    /// <paramref name="p"/>; -1 when its beginning is not in <paramref name="s"/> or does not pair with that end, null with none.</summary>
    private static int? Within(string s, int lt, int p, string opener, string closer)
    {
        int end = s.IndexOf(closer, lt + 1, p - lt - 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }
        int start = s.LastIndexOf(opener, lt, StringComparison.Ordinal);
        return start >= 0 && s.IndexOf(closer, start + opener.Length, StringComparison.Ordinal) == end ? start : -1;
    }

    /// <summary>A tag's '>' (outside its quoted values) from its name's end, <paramref name="limit"/> where it has none
    /// before it, with the quote still open there and its first attribute's offset (-1 with none).</summary>
    private static (int End, char Quote, int Attribute) TagEnd(string s, int from, int limit)
    {
        char quote = '\0';
        int attribute = -1;
        int gt = from;
        for (; gt < limit; gt++)
        {
            char c = s[gt];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                break;
            }
            else if (attribute < 0 && !char.IsWhiteSpace(c) && c != '/')
            {
                attribute = gt;
            }
        }
        return (gt, quote, attribute);
    }

    /// <summary>The place at the end of <paramref name="s"/>, read forwards from the file's start (<paramref name="from"/> 0).</summary>
    private static Place Read(string s, int from, MarkupKind kind, string? following)
    {
        StringComparison names = kind == MarkupKind.Html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<(string Name, int Start, int Inside)> open = [];
        int i = 0;
        while (i < s.Length && s.IndexOf('<', i) is var lt and >= 0)
        {
            if (Skip(s, lt, "<!--", "-->") is { } comment)
            {
                if (comment < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = comment;
                continue;
            }
            if ((Skip(s, lt, "<![CDATA[", "]]>") ?? Skip(s, lt, "<?", "?>") ?? Skip(s, lt, "<!", ">")) is { } declaration)
            {
                if (declaration < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = declaration;
                continue;
            }
            bool endTag = lt + 1 < s.Length && s[lt + 1] == '/';
            int nameStart = lt + (endTag ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < s.Length && IsNameChar(s[nameEnd]))
            {
                nameEnd++;
            }
            if (nameEnd == nameStart)
            {
                // A '<' just typed is a tag begun, where Enter keeps the line's indentation; any other is text.
                if (nameStart >= s.Length)
                {
                    return new Place(PlaceKind.Other);
                }
                i = lt + 1;
                continue;
            }
            string name = s[nameStart..nameEnd];
            (int gt, char quote, int attribute) = TagEnd(s, nameEnd, s.Length);
            if (gt == s.Length)
            {
                bool onTagLine = attribute >= 0 && s.AsSpan(lt, attribute - lt).IndexOfAny('\r', '\n') < 0;
                return quote != '\0' || endTag ? new Place(PlaceKind.Other) : new Place(PlaceKind.Tag, Tag: from + lt, Attribute: onTagLine ? from + attribute : -1);
            }
            i = gt + 1;
            if (endTag)
            {
                int match = open.FindLastIndex(e => string.Equals(e.Name, name, names));
                if (match >= 0)
                {
                    open.RemoveRange(match, open.Count - match);
                }
                continue;
            }
            if (kind == MarkupKind.Html)
            {
                CloseImplied(open, name);
            }
            if (s[gt - 1] == '/' || kind == MarkupKind.Html && VoidElements.Contains(name))
            {
                continue;
            }
            open.Add((name, lt, gt + 1));
            if (kind == MarkupKind.Html && RawTextElements.Contains(name))
            {
                int close = s.IndexOf("</" + name, gt + 1, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                {
                    return new Place(PlaceKind.Other);
                }
                i = close;
            }
        }
        if (open.Count == 0)
        {
            return new Place(PlaceKind.Content);
        }
        (_, int start, int inside) = open[^1];
        int closer = following is null ? -1 : open.FindLastIndex(e => string.Equals(e.Name, following, names));
        return new Place(PlaceKind.Content, Parent: from + start, Opened: s.AsSpan(inside).IsWhiteSpace(), Closer: closer < 0 ? -1 : from + open[closer].Start);
    }

    /// <summary>The open elements a start tag ends first, as HTML's parser ends those whose end tag may be left out: an li
    /// by the next li, a p by a block, a cell by the next cell or row, and so on, never past what bounds each (the list,
    /// the table, the select).</summary>
    private static void CloseImplied(List<(string Name, int Start, int Inside)> open, string started)
    {
        foreach ((string[] ends, string[] bounds) in ImpliedEnds(started.ToLowerInvariant()))
        {
            for (int k = open.Count - 1; k >= 0; k--)
            {
                string name = open[k].Name.ToLowerInvariant();
                if (ends.Contains(name))
                {
                    open.RemoveRange(k, open.Count - k);
                    break;
                }
                if (bounds.Contains(name))
                {
                    break;
                }
            }
        }
    }

    /// <summary>For a start tag, each set of elements it ends with what that search stops at, in the order HTML's parser
    /// ends them.</summary>
    private static IEnumerable<(string[] Ends, string[] Bounds)> ImpliedEnds(string started)
    {
        string[] list = ["ul", "ol", "menu", .. Scopes];
        string[] table = ["table", "template", "html"];
        switch (started)
        {
            case "li":
                yield return (["li"], list);
                break;
            case "dt" or "dd":
                yield return (["dt", "dd"], ["dl", .. Scopes]);
                break;
            case "td" or "th":
                yield return (["td", "th"], table);
                break;
            case "tr":
                yield return (["td", "th"], table);
                yield return (["tr"], table);
                break;
            case "tbody" or "thead" or "tfoot":
                yield return (["td", "th"], table);
                yield return (["tr"], table);
                yield return (["tbody", "thead", "tfoot"], table);
                break;
            case "option":
                yield return (["option"], ["select", "datalist", "template"]);
                break;
            case "optgroup":
                yield return (["option"], ["select", "datalist", "template"]);
                yield return (["optgroup"], ["select", "template"]);
                break;
            case "rt" or "rp":
                yield return (["rt", "rp"], ["ruby"]);
                break;
        }
        if (ClosesParagraph.Contains(started))
        {
            yield return (["p"], ["button", .. Scopes]);
        }
    }

    /// <summary>Past a construct starting at <paramref name="at"/> with <paramref name="opener"/>: after its
    /// <paramref name="closer"/>, -1 when the text ends inside it, null when it does not start there.</summary>
    private static int? Skip(string s, int at, string opener, string closer)
    {
        if (string.CompareOrdinal(s, at, opener, 0, opener.Length) != 0 || s.Length - at < opener.Length)
        {
            return null;
        }
        int end = s.IndexOf(closer, at + opener.Length, StringComparison.Ordinal);
        return end < 0 ? -1 : end + closer.Length;
    }

    private static string LineIndent(TextSnapshot text, int offset)
    {
        LineSpan line = text.GetLine(text.GetLineFromOffset(offset));
        return text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);
    }
}
