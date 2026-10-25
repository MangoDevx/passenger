using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>A document's URI as the protocol has it and its path as the IDE has it, one from the other. The path is
/// always the canonical one (<see cref="PathNames.Canonical"/>), whichever spelling the server used, so a location it
/// gives finds the open file it names.</summary>
public static class DocumentUris
{
    public static string FromPath(string path) => new Uri(PathNames.Canonical(path)).AbsoluteUri;

    public static string ToPath(string uri)
    {
        string local = new Uri(uri).LocalPath;
        // A Windows drive as vscode-uri writes it, file:///c%3A/..., which System.Uri leaves as /c:/...
        if (OperatingSystem.IsWindows() && local.Length >= 3 && local[0] is '/' or '\\' && char.IsAsciiLetter(local[1]) && local[2] == ':')
        {
            local = local[1..];
        }
        return PathNames.Canonical(local);
    }

    /// <summary>The file name a source generator gave a document Roslyn's server names by a URI of its own (no file on
    /// disk, its hint name in the query), else null.</summary>
    public static string? GeneratedName(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != "roslyn-source-generated")
        {
            return null;
        }
        string? hint = parsed.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "hintName")?[1];
        return Uri.UnescapeDataString(hint ?? parsed.Segments[^1]);
    }

    /// <summary>Each URI's path, made once however many places name it: making a path canonical asks the disk.</summary>
    public static Dictionary<string, string> ToPaths(IEnumerable<string> uris)
    {
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        foreach (string uri in uris)
        {
            if (!paths.ContainsKey(uri))
            {
                paths[uri] = ToPath(uri);
            }
        }
        return paths;
    }
}
