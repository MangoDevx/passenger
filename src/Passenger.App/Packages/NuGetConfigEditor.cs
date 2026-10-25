using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NuGet.Configuration;
using Passenger.App.Workspace;

namespace Passenger.App.Packages;

/// <summary>A feed as the Sources tab's dialog gives it: its name and URL, whether it is enabled and may use HTTP, and
/// the credentials to keep for it (a password of null or empty keeps the one the file has; no user name takes the
/// credentials out).</summary>
public sealed record FeedEdit(string Name, string Url, bool Enabled, bool AllowInsecure, string? Username, string? Password)
{
    /// <summary>Leaves the password out, so a record printed never shows it.</summary>
    public override string ToString() => $"{Name} ({Url})";
}

/// <summary>Changes one nuget.config: a feed added, edited, removed, enabled or disabled, and its credentials. Only the
/// elements changed are written, indented as their neighbours are; every other character of the file stays as it was
/// (its declaration or none, comments, quotes, byte order mark, line ends and permissions). A password goes in clear
/// text but on Windows, where NuGet encrypts it for the user.</summary>
public static class NuGetConfigEditor
{
    private const string Sources = "packageSources";
    private const string Disabled = "disabledPackageSources";
    private const string Credentials = "packageSourceCredentials";

    public static void Add(string path, FeedEdit feed) => Change(path, text =>
    {
        text.AddChild(Sources, FeedElement(feed, null));
        Credential(text, feed, null);
        Enable(text, feed.Name, feed.Enabled);
    });

    /// <summary>Changes a feed the file names, under its old name; a new name follows it into the file's other sections.</summary>
    public static void Update(string path, string name, FeedEdit feed) => Change(path, text =>
    {
        XElement element = text.Child(Sources, name) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} names no feed {name}");
        text.Replace(element, FeedElement(feed, element));
        if (!feed.Name.Equals(name, StringComparison.Ordinal))
        {
            if (text.Child(Disabled, name) is { } disabled)
            {
                text.Remove(disabled);
            }
            while (text.Section("packageSourceMapping")?.Elements("packageSource").FirstOrDefault(e => (string?)e.Attribute("key") == name) is { } mapped)
            {
                text.SetAttribute(mapped, "key", feed.Name);
            }
        }
        Credential(text, feed, name);
        Enable(text, feed.Name, feed.Enabled);
    });

    /// <summary>Takes a feed out of the file, with what the file says of it elsewhere.</summary>
    public static void Remove(string path, string name) => Change(path, text =>
    {
        while ((text.Child(Sources, name) ?? text.Child(Disabled, name) ?? Held(text, name)) is { } element)
        {
            text.Remove(element);
        }
    });

    /// <summary>Disables a feed in this file, or takes this file's disabling of it away.</summary>
    public static void SetEnabled(string path, string name, bool enabled) => Change(path, text => Enable(text, name, enabled));

    private static void Enable(ConfigText text, string name, bool enabled)
    {
        XElement? disabled = text.Child(Disabled, name);
        if (enabled && disabled is not null)
        {
            text.Remove(disabled);
        }
        else if (!enabled && disabled is null)
        {
            text.AddChild(Disabled, $"<add key=\"{Escape(name)}\" value=\"true\" />");
        }
    }

    private static XElement? Held(ConfigText text, string name) => text.Section(Credentials)?.Element(XmlConvert.EncodeLocalName(name));

    /// <summary>Writes a feed's credentials, as the dialog left them, under its name in packageSourceCredentials.</summary>
    private static void Credential(ConfigText text, FeedEdit feed, string? oldName)
    {
        XElement? held = Held(text, feed.Name) ?? (oldName is null ? null : Held(text, oldName));
        if (string.IsNullOrEmpty(feed.Username))
        {
            if (held is not null)
            {
                text.Remove(held);
            }
            return;
        }
        (string Key, string Value)? password = null;
        if (feed.Password is { Length: > 0 } given)
        {
            PackageSourceCredential secret = PackageSourceCredential.FromUserInput(feed.Name, feed.Username, given,
                storePasswordInClearText: !OperatingSystem.IsWindows(), validAuthenticationTypesText: null);
            password = (secret.IsPasswordClearText ? "ClearTextPassword" : "Password", secret.PasswordText);
        }
        else if (held?.Elements("add").FirstOrDefault(a => (string?)a.Attribute("key") is "Password" or "ClearTextPassword") is { } kept)
        {
            password = ((string)kept.Attribute("key")!, (string?)kept.Attribute("value") ?? string.Empty);
        }
        string indent = text.ChildIndent(Credentials);
        string name = XmlConvert.EncodeLocalName(feed.Name);
        List<string> values = [$"<add key=\"Username\" value=\"{Escape(feed.Username)}\" />"];
        if (password is { } secretValue)
        {
            values.Add($"<add key=\"{secretValue.Key}\" value=\"{Escape(secretValue.Value)}\" />");
        }
        foreach (XElement other in held?.Elements("add").Where(a => (string?)a.Attribute("key") is not ("Username" or "Password" or "ClearTextPassword")) ?? [])
        {
            values.Add($"<add key=\"{Escape((string?)other.Attribute("key") ?? string.Empty)}\" value=\"{Escape((string?)other.Attribute("value") ?? string.Empty)}\" />");
        }
        string element = $"<{name}>{string.Concat(values.Select(v => text.NewLine + indent + text.Unit + v))}{text.NewLine}{indent}</{name}>";
        if (held is not null)
        {
            text.Replace(held, element);
        }
        else
        {
            text.AddChild(Credentials, element);
        }
    }

    /// <summary>A feed's add element: its key and value, what else the element had, and its insecure switch.</summary>
    private static string FeedElement(FeedEdit feed, XElement? was)
    {
        StringBuilder element = new($"<add key=\"{Escape(feed.Name)}\" value=\"{Escape(feed.Url)}\"");
        foreach (XAttribute other in was?.Attributes().Where(a => a.Name.LocalName is not ("key" or "value" or "allowInsecureConnections")) ?? [])
        {
            element.Append($" {other.Name.LocalName}=\"{Escape(other.Value)}\"");
        }
        if (feed.AllowInsecure)
        {
            element.Append(" allowInsecureConnections=\"true\"");
        }
        return element.Append(" />").ToString();
    }

    private static string Escape(string value) => SecurityElement.Escape(value);

    /// <summary>Reads the file, makes the changes on its text, and writes it back as it came through
    /// <see cref="FileText.Save"/>. A file not there yet, or empty, is made with NuGet's empty configuration.</summary>
    private static void Change(string path, Action<ConfigText> change)
    {
        FileText? file = File.Exists(path) ? FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file") : null;
        ConfigText text = new(file is { Text.Length: > 0 } read ? read.Text : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n");
        change(text);
        FileText.Save(path, text.Text, file?.Encoding ?? new UTF8Encoding(false), file?.HasBom ?? false, file?.CrBreaks ?? false);
    }

    /// <summary>The file's text and the elements in it, changed in place: each change is a splice of the text, after
    /// which the elements are read from it again, so an element is looked up afresh for each change.</summary>
    private sealed class ConfigText
    {
        private XElement root = null!;
        private int[] lines = [];

        public ConfigText(string text)
        {
            Text = text;
            NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            Parse();
            // The step one level of the file is indented by, as its configuration's first child has it.
            string step = root.Elements().FirstOrDefault() is { } first ? Indent(first) : string.Empty;
            Unit = step.Length > Indent(root).Length ? step[Indent(root).Length..] : "  ";
        }

        public string Text { get; private set; }

        public string NewLine { get; }

        public string Unit { get; }

        public XElement? Section(string name) => root.Element(name);

        /// <summary>The add element a section has for a name, as NuGet matches keys (ignoring case).</summary>
        public XElement? Child(string section, string name) =>
            Section(section)?.Elements("add").FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The indent a section's children have: its last child's, else one step in from the section.</summary>
        public string ChildIndent(string section) => Section(section) is not { } found
            ? Indent(root) + Unit + Unit
            : found.Elements().LastOrDefault() is { } last && Indent(last) is { Length: > 0 } own ? own : Indent(found) + Unit;

        /// <summary>Adds an element at the end of a section, the section made at the configuration's end if there is none.</summary>
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
                ?? throw new XmlException("The file has no configuration element");
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
}
