using System.Collections.Concurrent;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>How well a breakpoint's source path names a document a PDB records. The same file is the best match, by the
/// name the file system gives it (macOS's /var is /private/var, Windows' C:\Users\RUNNER~1 a long name). A document
/// recorded under another root (a deterministic build's "/_/", a build on another machine) is not on this disk, and
/// matches by the folders and file name it shares from the end, at least the file and its folder. A document that is on
/// this disk as another file never matches while the breakpoint's file is on this disk too, so a same-named file in
/// another folder or project does not take a breakpoint; a breakpoint's file that is not here (the IDE's, this adapter
/// in a container) matches a document that is (one built in the image) by what they share from the end. A Windows path
/// (C:\…, \\server\…) compares ignoring case wherever the adapter runs, as Windows names files.</summary>
public static class SourcePaths
{
    public const int Same = int.MaxValue;

    private static readonly ConcurrentDictionary<string, string?> Canonicals = new();

    /// <summary>Paths compare as the file system does: ignoring case on Windows and macOS.</summary>
    public static StringComparison Comparison => PathNames.Comparison;

    public static int Match(string requested, string document)
    {
        StringComparison comparison = IsWindows(requested) || IsWindows(document) ? StringComparison.OrdinalIgnoreCase : Comparison;
        string want = Normalize(requested);
        string have = Normalize(document);
        if (string.Equals(want, have, comparison))
        {
            return Same;
        }
        if (Canonical(have) is { } real && Canonical(want) is { } wanted)
        {
            return string.Equals(wanted, real, comparison) ? Same : 0;
        }
        string[] asked = want.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] had = have.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int shared = 0;
        while (shared < asked.Length && shared < had.Length
            && string.Equals(asked[^(shared + 1)], had[^(shared + 1)], comparison))
        {
            shared++;
        }
        return shared >= 2 ? shared : 0;
    }

    /// <summary>A path a Windows machine gave, a drive's (C:\ or C:/) or a share's (\\server\share), on any system.</summary>
    private static bool IsWindows(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        string slashed = path.Replace('\\', '/');
        // "folder/../Program.cs" names Program.cs; a rooted path resolves its dots.
        return Path.IsPathRooted(slashed) && !slashed.StartsWith("/_/", StringComparison.Ordinal) ? Path.GetFullPath(slashed).Replace('\\', '/') : slashed;
    }

    /// <summary>A file on this disk as the file system names it, as the IDE names the breakpoints' paths it sends
    /// (<see cref="PathNames.Canonical"/>); null when there is no such file.</summary>
    private static string? Canonical(string path) => Canonicals.GetOrAdd(path, static p =>
        Path.IsPathFullyQualified(p) && File.Exists(p) ? PathNames.Canonical(p).Replace('\\', '/') : null);
}
