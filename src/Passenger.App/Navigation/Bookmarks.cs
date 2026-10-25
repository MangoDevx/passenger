using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A bookmark: a file and a line (from zero) in it.</summary>
public sealed class Bookmark(string path, int line)
{
    public string Path { get; internal set; } = path;

    public int Line { get; internal set; } = line;

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>A folder's bookmarks as the store keeps them.</summary>
public sealed record BookmarksState : IStateDocument<BookmarksState>
{
    public List<KeptBookmark> Bookmarks { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>One without a file is left out, and a line before the first is the first.</summary>
    public BookmarksState Clamped() => this with
    {
        Bookmarks = [.. (Bookmarks ?? []).Where(b => b is { Path.Length: > 0 }).Select(b => b with { Line = Math.Max(0, b.Line) })],
    };
}

/// <summary>A bookmark as kept: its file and its line from zero.</summary>
public sealed record KeptBookmark(string Path, int Line);

/// <summary>Bookmarks: lines marked to come back to, Toggle Bookmark on the caret's line, Next and Previous across
/// every file in the order they were set. A bookmark in an open file moves with its line.</summary>
public sealed class BookmarkStore
{
    private readonly List<Bookmark> all = [];

    /// <summary>A file's bookmarks changed: set, removed or moved.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Every bookmark, in the order they were set.</summary>
    public IReadOnlyList<Bookmark> All => all;

    public IReadOnlyList<int> LinesIn(string path) => [.. all.Where(b => b.Path == path).Select(b => b.Line)];

    /// <summary>Every bookmark as the folder keeps it, in the order they were set.</summary>
    public BookmarksState Capture() => new() { Bookmarks = [.. all.Select(b => new KeptBookmark(b.Path, b.Line))] };

    /// <summary>Sets the bookmarks a folder kept after those there are; a line with one already keeps it.</summary>
    public void Restore(BookmarksState kept)
    {
        foreach (KeptBookmark bookmark in kept.Bookmarks.Where(k => !all.Any(b => b.Path == k.Path && b.Line == k.Line)))
        {
            all.Add(new Bookmark(bookmark.Path, bookmark.Line));
        }
        foreach (string path in kept.Bookmarks.Select(b => b.Path).Distinct())
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Takes every bookmark away, as another folder opens: each folder's are its own.</summary>
    public void Clear()
    {
        List<string> paths = [.. all.Select(b => b.Path).Distinct()];
        all.Clear();
        foreach (string path in paths)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Sets a bookmark on a line, or takes away the one there.</summary>
    public void Toggle(string path, int line)
    {
        if (all.FirstOrDefault(b => b.Path == path && b.Line == line) is { } there)
        {
            all.Remove(there);
        }
        else
        {
            all.Add(new Bookmark(path, line));
        }
        Changed?.Invoke(this, path);
    }

    public void Remove(Bookmark bookmark)
    {
        if (all.Remove(bookmark))
        {
            Changed?.Invoke(this, bookmark.Path);
        }
    }

    /// <summary>The bookmark after (or before) a place, through every file in their order, round past the last.</summary>
    public Bookmark? Next(string? path, int line, int step)
    {
        if (all.Count == 0)
        {
            return null;
        }
        List<Bookmark> ordered = [.. all.OrderBy(b => b.Path, StringComparer.Ordinal).ThenBy(b => b.Line)];
        int Compare(Bookmark b) => b.Path == path ? b.Line.CompareTo(line) : string.CompareOrdinal(b.Path, path);
        return step > 0
            ? ordered.FirstOrDefault(b => Compare(b) > 0) ?? ordered[0]
            : ordered.LastOrDefault(b => Compare(b) < 0) ?? ordered[^1];
    }

    /// <summary>Carries the bookmarks of a file, or of every file in a folder, to where it moved.</summary>
    public void Move(string from, string to)
    {
        HashSet<string> changed = [];
        foreach (Bookmark bookmark in all.Where(b => PathNames.Within(b.Path, from)))
        {
            changed.Add(bookmark.Path);
            bookmark.Path = PathNames.Moved(bookmark.Path, from, to);
            changed.Add(bookmark.Path);
        }
        foreach (string path in changed)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>Carries a file's bookmarks through an edit to it, with their lines' first characters.</summary>
    public void Follow(string path, DocumentChangedEventArgs change)
    {
        bool moved = false;
        foreach (Bookmark bookmark in all.Where(b => b.Path == path))
        {
            int start = change.Before.GetLineStart(Math.Min(bookmark.Line, change.Before.LineCount - 1));
            int mapped = TextEditing.MapOffset(start, change.Edits, afterInsertions: true);
            int line = change.After.GetLineFromOffset(Math.Min(mapped, change.After.Length));
            if (line != bookmark.Line)
            {
                bookmark.Line = line;
                moved = true;
            }
        }
        if (moved)
        {
            // Two on one line (their lines joined) are one.
            List<Bookmark> twice = [.. all.Where(b => b.Path == path).GroupBy(b => b.Line).SelectMany(g => g.Skip(1))];
            foreach (Bookmark extra in twice)
            {
                all.Remove(extra);
            }
            Changed?.Invoke(this, path);
        }
    }
}
