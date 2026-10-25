using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Workspace;

/// <summary>Add Reference and Remove on a project reference, its items following a file moved, and a property set (Add
/// > Docker Support's container properties), made to the project file's text: a reference goes beside the project's
/// others, or in an item group of its own before &lt;/Project&gt;, written relative with backslashes; taking it out
/// takes the group it leaves empty, so the file is as it was. The rest of the file keeps its bytes, its byte order
/// mark, line endings and indentation. Paths are canonical. A reference's edit returns null when it was done, else why
/// not; a file that cannot be read or written throws.</summary>
public static partial class ProjectEdits
{
    public static string? AddReference(string project, string referenced)
    {
        ProjectText file = new(project);
        if (file.References().Any(r => PathNames.Same(r.Path, referenced)))
        {
            return $"{Path.GetFileNameWithoutExtension(project)} already references {Path.GetFileNameWithoutExtension(referenced)}";
        }
        string include = Path.GetRelativePath(Path.GetDirectoryName(project)!, referenced).Replace('/', '\\');
        string text = file.Text;
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string indent = IndentUnit(text);
        string element = $"<ProjectReference Include=\"{include}\" />";
        if (file.References().LastOrDefault(r => r.Group is { Conditioned: false }) is { Group: not null } last)
        {
            int lineEnd = text.IndexOf('\n', last.End);
            int at = lineEnd < 0 ? text.Length : lineEnd + 1;
            if (text[last.End..(lineEnd < 0 ? text.Length : lineEnd)].Trim().Length > 0)
            {
                // The group's close (or more) follows it on its line: the new one goes between them, on that line.
                file.Write(text.Insert(last.End, element));
                return null;
            }
            // On a line of its own, indented as the last one's, a level deeper when that line begins with its group.
            int lineStart = text.LastIndexOf('\n', Math.Max(0, last.Start - 1)) + 1;
            string under = text[lineStart..last.Start].Trim().Length > 0 ? indent : string.Empty;
            file.Write(text.Insert(at, $"{LineIndent(text, last.Start)}{under}{element}{eol}"));
            return null;
        }
        return InGroupOfItsOwn(file, project, element);
    }

    /// <summary>A package reference added as dotnet add package writes one, its version given (none under central package
    /// management, whose Directory.Packages.props holds it): on a line of its own after the project's last package reference,
    /// indented as it is, else in an item group of its own before &lt;/Project&gt;. Null once done, else why not.</summary>
    public static string? AddPackage(string project, string id, string? version)
    {
        ProjectText file = new(project);
        string text = file.Text;
        if (Regex.IsMatch(text, $"<PackageReference\\s+Include=\"{Regex.Escape(id)}\"", RegexOptions.IgnoreCase))
        {
            return $"{Path.GetFileNameWithoutExtension(project)} already references {id}";
        }
        string element = version is null ? $"<PackageReference Include=\"{id}\" />" : $"<PackageReference Include=\"{id}\" Version=\"{version}\" />";
        if (Regex.Matches(text, "<PackageReference\\b[^>]*/>").LastOrDefault() is { } last)
        {
            int end = last.Index + last.Length;
            int lineEnd = text.IndexOf('\n', end);
            string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (text[end..(lineEnd < 0 ? text.Length : lineEnd)].Trim().Length > 0)
            {
                file.Write(text.Insert(end, element));
                return null;
            }
            file.Write(text.Insert(lineEnd < 0 ? text.Length : lineEnd + 1, $"{LineIndent(text, last.Index)}{element}{eol}"));
            return null;
        }
        return InGroupOfItsOwn(file, project, element);
    }

    /// <summary>An item put in an item group of its own before &lt;/Project&gt;, indented as the file indents.</summary>
    private static string? InGroupOfItsOwn(ProjectText file, string project, string element)
    {
        string text = file.Text;
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string indent = IndentUnit(text);
        int close = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
        {
            return $"{Path.GetFileName(project)} is not a project file";
        }
        int closeLine = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        bool inline = text[closeLine..close].Trim().Length > 0;
        if (inline)
        {
            closeLine = close;
        }
        // A file that leaves a blank line between its groups gets one after the new group too.
        bool spaced = closeLine >= 2 && text[..closeLine].TrimEnd('\r', '\n').Length < text[..closeLine].Length - eol.Length;
        string group = $"{indent}<ItemGroup>{eol}{indent}{indent}{element}{eol}{indent}</ItemGroup>{eol}{(spaced ? eol : string.Empty)}";
        file.Write(text.Insert(closeLine, (inline ? eol : string.Empty) + group));
        return null;
    }

    public static string? RemoveReference(string project, string referenced)
    {
        ProjectText file = new(project);
        if (file.References().FirstOrDefault(r => PathNames.Same(r.Path, referenced)) is not { } reference)
        {
            return $"{Path.GetFileNameWithoutExtension(project)} does not reference {Path.GetFileNameWithoutExtension(referenced)}";
        }
        string text = file.Text;
        (int start, int end) = WholeLines(text, reference.Start, reference.End);
        if (reference.Group is { } group && (text[group.OpenEnd..start] + text[end..group.CloseStart]).Trim().Length == 0)
        {
            // The group is left empty: it goes too, with the blank line that set it apart from the next.
            (int groupStart, int groupEnd) = WholeLines(text, group.OpenStart, group.CloseEnd);
            if (IsBlankLineBefore(text, groupStart) && BlankLineAt(text, groupEnd) is int blankEnd)
            {
                groupEnd = blankEnd;
            }
            file.Write(text.Remove(groupStart, groupEnd - groupStart));
            return null;
        }
        file.Write(text.Remove(start, end - start));
        return null;
    }

    /// <summary>Sets a property in the project file, as Add > Docker Support sets the SDK's container properties and Manage User
    /// Secrets a UserSecretsId (<see cref="WithProperty"/>). Null once done, else why not.</summary>
    public static string? SetProperty(string project, string name, string value)
    {
        ProjectText file = new(project);
        (string? text, string? problem) = WithProperty(file.Text, project, name, value);
        if (text is not null && text != file.Text)
        {
            file.Write(text);
        }
        return problem;
    }

    /// <summary>A project file's text with a property set: where an unconditioned property group sets it already, its value
    /// there; else on a line of its own at the end of the first unconditioned property group, indented as that group's last
    /// property; else in a property group of its own before &lt;/Project&gt;. The text, else why not.</summary>
    public static (string? Text, string? Problem) WithProperty(string text, string project, string name, string value)
    {
        string written = System.Security.SecurityElement.Escape(value);
        List<(int Start, int End)> comments = [.. Comment().Matches(text).Select(m => (m.Index, m.Index + m.Length))];
        List<Match> groups = [.. PropertyGroup().Matches(text).Where(m => !comments.Any(c => m.Index >= c.Start && m.Index < c.End)
            && !m.Groups["attributes"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase))];
        Regex element = new($@"<{Regex.Escape(name)}\s*>(?<value>[^<]*)</{Regex.Escape(name)}\s*>");
        foreach (Match group in groups)
        {
            Capture body = group.Groups["body"];
            // One commented out sets nothing: the one MSBuild reads is outside every comment.
            Match set = element.Match(text, body.Index, body.Length);
            while (set.Success && comments.Any(c => set.Index >= c.Start && set.Index < c.End))
            {
                set = set.NextMatch();
            }
            if (set.Success)
            {
                Capture old = set.Groups["value"];
                return (old.Value == written ? text : text.Remove(old.Index, old.Length).Insert(old.Index, written), null);
            }
        }
        string eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string indent = IndentUnit(text);
        string line = $"<{name}>{written}</{name}>";
        if (groups.FirstOrDefault() is { } first)
        {
            Capture body = first.Groups["body"];
            int closeLine = text.LastIndexOf('\n', body.Index + body.Length - 1) + 1;
            if (closeLine <= body.Index || text[closeLine..(body.Index + body.Length)].Trim().Length > 0)
            {
                // The group closes on a line it shares: the property goes just before its close.
                return (text.Insert(body.Index + body.Length, line), null);
            }
            // Indented as the group's last property, or a level under the group with none.
            int previous = text.LastIndexOf('\n', Math.Max(0, closeLine - 2)) + 1;
            string under = previous <= first.Index ? LineIndent(text, first.Index) + indent : LineIndent(text, closeLine - 1);
            return (text.Insert(closeLine, $"{under}{line}{eol}"), null);
        }
        int close = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (close < 0)
        {
            return (null, $"{Path.GetFileName(project)} is not a project file");
        }
        int closeAt = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        bool spaced = closeAt >= 2 && text[..closeAt].TrimEnd('\r', '\n').Length < text[..closeAt].Length - eol.Length;
        return (text.Insert(closeAt, $"{indent}<PropertyGroup>{eol}{indent}{indent}{line}{eol}{indent}</PropertyGroup>{eol}{(spaced ? eol : string.Empty)}"), null);
    }

    /// <summary>A property's value as the project file's own text sets it, outside comments and conditioned groups; null where it
    /// sets none.</summary>
    public static string? PropertyIn(string text, string name)
    {
        List<(int Start, int End)> comments = [.. Comment().Matches(text).Select(m => (m.Index, m.Index + m.Length))];
        Regex element = new($@"<{Regex.Escape(name)}\s*>(?<value>[^<]*)</{Regex.Escape(name)}\s*>");
        foreach (Match group in PropertyGroup().Matches(text).Where(m => !comments.Any(c => m.Index >= c.Start && m.Index < c.End)
            && !m.Groups["attributes"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase)))
        {
            Capture body = group.Groups["body"];
            for (Match set = element.Match(text, body.Index, body.Length); set.Success; set = set.NextMatch())
            {
                if (!comments.Any(c => set.Index >= c.Start && set.Index < c.End))
                {
                    return System.Net.WebUtility.HtmlDecode(set.Groups["value"].Value.Trim());
                }
            }
        }
        return null;
    }

    /// <summary>The project (at its path now) following files and folders that moved or were renamed: an item's Include,
    /// Update, Remove or Exclude naming a file, or a glob's folder, names where it went (a list or a property stays as
    /// written), and a DependentUpon or LastGenOutput naming one (relative to its item's file) names it there. A project
    /// that moved itself reads its paths from where it was, so one naming a file outside it still finds it. Whether it
    /// changed.</summary>
    public static bool MoveItems(string project, IReadOnlyList<(string From, string To)> moves)
    {
        ProjectText file = new(project);
        string folder = Path.GetDirectoryName(project)!;
        string Moved(string path) => moves.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path;
        string before = Path.GetDirectoryName(moves.FirstOrDefault(m => PathNames.Within(project, m.To)) is { To: not null } back ? PathNames.Moved(project, back.To, back.From) : project)!;
        List<(int Start, int End)> comments = [.. Comment().Matches(file.Text).Select(m => (m.Index, m.Index + m.Length))];
        string text = ItemElement().Replace(file.Text, item =>
        {
            if (comments.Any(c => item.Index >= c.Start && item.Index < c.End))
            {
                return item.Value;
            }
            // Each path names where its file is now, from where the project is now; a glob by the folder it begins in.
            string start = ItemAttribute().Replace(item.Groups["start"].Value, attribute =>
            {
                string items = attribute.Groups["items"].Value;
                int wild = items.IndexOfAny(['*', '?']);
                string path = (wild < 0 ? items : items[..Math.Max(0, items.LastIndexOfAny(['\\', '/'], wild))]).TrimEnd('\\', '/');
                if (path.Trim().Length == 0 || items.IndexOfAny([';', '$', '@', '%']) >= 0)
                {
                    return attribute.Value;
                }
                string now = Moved(Full(before, path));
                return PathNames.Same(Full(folder, path), now) ? attribute.Value
                    : Spliced(attribute, attribute.Groups["items"], Written(Path.GetRelativePath(folder, now), path) + items[path.Length..]);
            });
            string element = $"<{item.Groups["tag"].Value}{start}{item.Groups["end"].Value}";
            if (ItemAttribute().Matches(item.Groups["start"].Value).FirstOrDefault(a => a.Groups["kind"].Value is "Include" or "Update" && OneFile(a.Groups["items"].Value)) is not { } own)
            {
                return element;
            }
            // What the item names beside it, relative to its file: where that is now, from where its file is now.
            string was = Path.GetDirectoryName(Full(before, own.Groups["items"].Value))!;
            string at = Path.GetDirectoryName(Moved(Full(before, own.Groups["items"].Value)))!;
            return RelatedName().Replace(element, related =>
            {
                Capture value = related.Groups["value"];
                string target = Moved(Full(was, value.Value.Trim()));
                return PathNames.Same(Full(at, value.Value.Trim()), target) ? related.Value
                    : Spliced(related, value, Written(Path.GetRelativePath(at, target), value.Value));
            });
        });
        if (text == file.Text)
        {
            return false;
        }
        file.Write(text);
        return true;

        static bool OneFile(string items) => items.Trim().Length > 0 && items.IndexOfAny(['*', '?', ';', '$', '@', '%']) < 0;

        static string Full(string folder, string relative) => Path.GetFullPath(Path.Combine(folder, relative.Replace('\\', '/')));
    }

    /// <summary>A path as a project writes it, with the separators the value it replaces had.</summary>
    private static string Written(string relative, string was) => was.Contains('\\') ? relative.Replace('/', '\\') : relative.Replace('\\', '/');

    /// <summary>A match's text with one of its groups' text replaced.</summary>
    private static string Spliced(Match match, Capture group, string replacement) =>
        string.Concat(match.Value.AsSpan(0, group.Index - match.Index), replacement, match.Value.AsSpan(group.Index - match.Index + group.Length));

    /// <summary>The line or lines a span stands on, their indentation and line break included, when nothing else is on
    /// them; the span alone otherwise.</summary>
    private static (int Start, int End) WholeLines(string text, int start, int end)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start > 0 && text[lineStart..start].Trim().Length > 0)
        {
            return (start, end);
        }
        int lineEnd = text.IndexOf('\n', end);
        int stop = lineEnd < 0 ? text.Length : lineEnd + 1;
        return text[end..(lineEnd < 0 ? text.Length : lineEnd)].Trim().Length > 0 ? (start, end) : (lineStart, stop);
    }

    private static bool IsBlankLineBefore(string text, int lineStart)
    {
        if (lineStart < 2)
        {
            return false;
        }
        int previous = text.LastIndexOf('\n', lineStart - 2) + 1;
        return text[previous..(lineStart - 1)].Trim().Length == 0;
    }

    /// <summary>The end of a blank line beginning at a place, line break included, or null when the line there is not blank.</summary>
    private static int? BlankLineAt(string text, int at)
    {
        int lineEnd = text.IndexOf('\n', at);
        return lineEnd >= 0 && text[at..lineEnd].Trim().Length == 0 ? lineEnd + 1 : null;
    }

    /// <summary>The indentation one level of the file takes: its first indented element's.</summary>
    private static string IndentUnit(string text) => IndentedLine().Match(text) is { Success: true } line ? line.Groups[1].Value : "  ";

    /// <summary>The whitespace the line holding a place begins with, never what stands on it before the place.</summary>
    private static string LineIndent(string text, int at)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int end = lineStart;
        while (end < at && text[end] is ' ' or '\t')
        {
            end++;
        }
        return text[lineStart..end];
    }

    [GeneratedRegex(@"\n([ \t]+)<")]
    private static partial Regex IndentedLine();

    [GeneratedRegex(@"<ProjectReference\b[^>]*?\bInclude\s*=\s*""(?<include>[^""]*)""[^>]*?(/>|>.*?</ProjectReference\s*>)", RegexOptions.Singleline)]
    private static partial Regex ReferenceElement();

    [GeneratedRegex(@"<ItemGroup\b(?<attributes>[^>]*?)>(?<body>.*?)</ItemGroup\s*>", RegexOptions.Singleline)]
    private static partial Regex ItemGroup();

    [GeneratedRegex(@"<PropertyGroup\b(?<attributes>[^>]*?)>(?<body>.*?)</PropertyGroup\s*>", RegexOptions.Singleline)]
    private static partial Regex PropertyGroup();

    /// <summary>An item that names files: its tag, the rest of its start tag (an Include, Update, Remove or Exclude among
    /// it), and its end.</summary>
    [GeneratedRegex(@"<(?<tag>[A-Za-z_][\w.-]*)(?<start>(?=[^>]*\b(?:Include|Update|Remove|Exclude)\s*=)[^>]*?)(?<end>/>|>.*?</\k<tag>\s*>)", RegexOptions.Singleline)]
    private static partial Regex ItemElement();

    [GeneratedRegex(@"\b(?<kind>Include|Update|Remove|Exclude)\s*=\s*""(?<items>[^""]*)""")]
    private static partial Regex ItemAttribute();

    /// <summary>A file an item names beside it: what it depends on, or what generates from it, as an attribute or an element.</summary>
    [GeneratedRegex(@"\b(?:DependentUpon|LastGenOutput)\s*=\s*""(?<value>[^""]+)""|<(?<name>DependentUpon|LastGenOutput)>(?<value>[^<]+)</\k<name>\s*>")]
    private static partial Regex RelatedName();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>An item group's tags, by where they start and end in the file.</summary>
    private sealed record Group(int OpenStart, int OpenEnd, int CloseStart, int CloseEnd, bool Conditioned);

    /// <summary>A project reference in the file: the project it names, where its element is, and the group holding it.</summary>
    private sealed record Reference(string Path, int Start, int End, Group? Group);

    /// <summary>A project file read for one change as a save reads a file: its encoding, byte order mark and line breaks,
    /// which go back as they came.</summary>
    private sealed class ProjectText
    {
        private readonly string path;
        private readonly FileText file;

        public ProjectText(string path)
        {
            this.path = path;
            file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
            Text = file.Text;
        }

        public string Text { get; }

        /// <summary>The project's references outside comments, in the file's order.</summary>
        public List<Reference> References()
        {
            string folder = Path.GetDirectoryName(path)!;
            List<(int Start, int End)> comments = [.. Comment().Matches(Text).Select(m => (m.Index, m.Index + m.Length))];
            bool Commented(int at) => comments.Any(c => at >= c.Start && at < c.End);
            List<Group> groups = [.. ItemGroup().Matches(Text).Where(m => !Commented(m.Index)).Select(m => new Group(
                m.Index,
                m.Groups["body"].Index,
                m.Groups["body"].Index + m.Groups["body"].Length,
                m.Index + m.Length,
                m.Groups["attributes"].Value.Contains("Condition", StringComparison.OrdinalIgnoreCase)))];
            return [.. ReferenceElement().Matches(Text).Where(m => !Commented(m.Index)).Select(m => new Reference(
                PathNames.Canonical(Path.Combine(folder, m.Groups["include"].Value.Replace('\\', '/'))),
                m.Index,
                m.Index + m.Length,
                groups.FirstOrDefault(g => m.Index >= g.OpenEnd && m.Index < g.CloseStart)))];
        }

        /// <summary>Writes the project back as an editor's save does (<see cref="FileText.Save"/>), in the encoding and
        /// line breaks it came with.</summary>
        public void Write(string text) => FileText.Save(path, text, file.Encoding, file.HasBom, file.CrBreaks);
    }
}
