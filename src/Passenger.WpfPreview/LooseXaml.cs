using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Xaml;
using System.Xaml.Schema;
using System.Xml;

namespace Passenger.WpfPreview;

/// <summary>A XAML file made loadable as loose XAML: the root is an instance of its element's own type (Window,
/// UserControl, a base class), never its x:Class, so <c>x:Class</c> and its kin, event handlers (attributes naming an
/// event, <c>EventSetter</c>s), <c>x:Code</c> and a root window's Visibility (which would show it) are blanked, every
/// line and column kept where the editor has it. Read with it, from the designer's namespace: the root's
/// <c>d:DesignWidth</c> and <c>d:DesignHeight</c>, and each element's <c>d:DataContext</c>.</summary>
internal sealed class LooseXaml
{
    public const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string Designer = "http://schemas.microsoft.com/expression/blend/2008";
    private const string Xmlns = "http://www.w3.org/2000/xmlns/";

    /// <summary>The text with what needs code-behind blanked, its lines and columns as they were.</summary>
    public required string Text { get; init; }

    public required string RootName { get; init; }

    public required string RootNamespace { get; init; }

    public double? DesignWidth { get; init; }

    public double? DesignHeight { get; init; }

    public required IReadOnlyList<DesignContext> Contexts { get; init; }

    /// <summary>Reads a file's text; an <see cref="XmlException"/> (with its line) when it is not well-formed XML.</summary>
    public static LooseXaml Read(string text, XamlSchemaContext schema, string localAssembly)
    {
        char[] blanked = text.ToCharArray();
        LineStarts lines = new(text);
        List<DesignContext> contexts = [];
        string? rootName = null;
        string rootNamespace = string.Empty;
        double? width = null;
        double? height = null;
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using XmlReader reader = XmlReader.Create(new StringReader(text), settings);
        IXmlLineInfo place = (IXmlLineInfo)reader;
        IXmlNamespaceResolver scope = (IXmlNamespaceResolver)reader;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            bool isRoot = rootName is null;
            if (isRoot)
            {
                rootName = reader.LocalName;
                rootNamespace = reader.NamespaceURI;
            }
            int line = place.LineNumber;
            int column = place.LinePosition;
            // The element's '<' is just before its name, where a reader places an element.
            int tagStart = lines.Offset(line, column) - 1;
            if (!isRoot && ((reader.NamespaceURI == Presentation && reader.LocalName == "EventSetter") || (reader.NamespaceURI == Language && reader.LocalName == "Code")))
            {
                Blank(blanked, text, tagStart, ElementEnd(reader, place, lines, text, tagStart));
                continue;
            }
            Type? type = reader.LocalName.Contains('.', StringComparison.Ordinal) ? null : TypeOf(schema, reader.NamespaceURI, reader.LocalName, localAssembly);
            string defaultNamespace = reader.LookupNamespace(string.Empty) ?? string.Empty;
            if (!reader.MoveToFirstAttribute())
            {
                continue;
            }
            do
            {
                string ns = reader.NamespaceURI;
                string name = reader.LocalName;
                bool blank = false;
                if (ns == Xmlns)
                {
                    continue;
                }
                if (ns == Language)
                {
                    blank = name is "Class" or "Subclass" or "ClassModifier" or "FieldModifier";
                }
                else if (ns == Designer)
                {
                    if (isRoot && name == "DesignWidth")
                    {
                        width = Number(reader.Value);
                    }
                    else if (isRoot && name == "DesignHeight")
                    {
                        height = Number(reader.Value);
                    }
                    else if (name == "DataContext")
                    {
                        // The element's scope, which its attributes share.
                        contexts.Add(new DesignContext(line, column, reader.Value, scope.GetNamespacesInScope(XmlNamespaceScope.All)));
                    }
                }
                else
                {
                    blank = IsEvent(schema, type, ns.Length == 0 ? defaultNamespace : ns, name, ns.Length == 0, localAssembly)
                        || (isRoot && ns.Length == 0 && name == "Visibility" && type is not null && typeof(Window).IsAssignableFrom(type));
                }
                if (blank)
                {
                    int start = lines.Offset(place.LineNumber, place.LinePosition);
                    Blank(blanked, text, start, AttributeEnd(text, start));
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        return new LooseXaml
        {
            Text = new string(blanked),
            RootName = rootName ?? throw new XmlException("The file has no root element.", null, 1, 1),
            RootNamespace = rootNamespace,
            DesignWidth = width,
            DesignHeight = height,
            Contexts = contexts,
        };
    }

    /// <summary>A CLR type a XAML namespace and name resolve to, a namespace with no assembly being the project's, as the
    /// build takes it.</summary>
    public static Type? TypeOf(XamlSchemaContext schema, string ns, string name, string localAssembly)
    {
        if (ns.StartsWith("clr-namespace:", StringComparison.Ordinal) && !ns.Contains(";assembly=", StringComparison.Ordinal))
        {
            ns += ";assembly=" + localAssembly;
        }
        try
        {
            return schema.GetXamlType(new XamlTypeName(ns, name))?.UnderlyingType;
        }
        catch (Exception error) when (error is XamlException or IOException or BadImageFormatException or ArgumentException or TypeLoadException)
        {
            // A type that does not resolve is the loader's to report, where it has the line.
            return null;
        }
    }

    /// <summary>Whether an attribute hooks an event, which only compiled XAML can: one of the element's own (Click), or a
    /// routed event qualified by its owner (Button.Click).</summary>
    private static bool IsEvent(XamlSchemaContext schema, Type? element, string ns, string name, bool unprefixed, string localAssembly)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return unprefixed && HasEvent(element, name);
        }
        return HasEvent(TypeOf(schema, ns, name[..dot], localAssembly), name[(dot + 1)..]);
    }

    private static bool HasEvent(Type? type, string name) =>
        type is not null && (type.GetEvent(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null
            || type.GetField(name + "Event", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) is { } field && typeof(RoutedEvent).IsAssignableFrom(field.FieldType));

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number > 0 && double.IsFinite(number) ? number : null;

    /// <summary>After an attribute that starts at an offset: its name, the equals sign and its quoted value.</summary>
    private static int AttributeEnd(string text, int start)
    {
        int at = text.IndexOf('=', start);
        while (at >= 0 && ++at < text.Length && text[at] is not ('"' or '\''))
        {
        }
        if (at < 0 || at >= text.Length)
        {
            return start;
        }
        int close = text.IndexOf(text[at], at + 1);
        return close < 0 ? start : close + 1;
    }

    /// <summary>After an element the reader is on, which it reads to its end: its "/>" or its end tag's ">".</summary>
    private static int ElementEnd(XmlReader reader, IXmlLineInfo place, LineStarts lines, string text, int tagStart)
    {
        if (reader.IsEmptyElement)
        {
            return TagEnd(text, tagStart);
        }
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return TagEnd(text, lines.Offset(place.LineNumber, place.LinePosition));
            }
        }
        return tagStart;
    }

    /// <summary>After the '>' that closes the tag at an offset, quoted values skipped.</summary>
    private static int TagEnd(string text, int from)
    {
        char quote = '\0';
        for (int at = from; at < text.Length; at++)
        {
            char c = text[at];
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
                return at + 1;
            }
        }
        return text.Length;
    }

    private static void Blank(char[] blanked, string text, int start, int end)
    {
        for (int at = Math.Max(0, start); at < Math.Min(end, text.Length); at++)
        {
            if (text[at] is not ('\r' or '\n'))
            {
                blanked[at] = ' ';
            }
        }
    }

    /// <summary>Where each line starts, lines broken as XML breaks them ("\r\n", "\r" or "\n"), so a reader's line and
    /// column (from 1) become an offset.</summary>
    private sealed class LineStarts
    {
        private readonly List<int> starts = [0];

        public LineStarts(string text)
        {
            for (int at = 0; at < text.Length; at++)
            {
                if (text[at] == '\r')
                {
                    if (at + 1 < text.Length && text[at + 1] == '\n')
                    {
                        at++;
                    }
                    starts.Add(at + 1);
                }
                else if (text[at] == '\n')
                {
                    starts.Add(at + 1);
                }
            }
        }

        public int Offset(int line, int column) => starts[Math.Clamp(line - 1, 0, starts.Count - 1)] + Math.Max(0, column - 1);
    }
}

/// <summary>An element's <c>d:DataContext</c>: where the element is (as a reader places it), the value as written and
/// the namespaces it is written with.</summary>
internal sealed record DesignContext(int Line, int Column, string Value, IDictionary<string, string> Namespaces);
