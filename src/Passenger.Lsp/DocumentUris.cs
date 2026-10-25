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
