using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Text;

/// <summary>What a search looks for: the text, and its three toggles.</summary>
public readonly record struct SearchQuery(string Text, bool MatchCase = false, bool WholeWords = false, bool Regex = false)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>Finding every match of a query in a snapshot, and the replacement each one takes. A regex
/// that does not parse, or one that runs too long, finds nothing and says why instead of throwing. The text is
/// searched with each "\r\n" read as "\n", as the editor's lines are, so `$` finds a CRLF line's end and `\s`
/// never takes half of its break; `^` is every line's start but the empty end after the last break.</summary>
public static class TextSearch
{
    /// <summary>The most matches a search reports, so a one-letter query in a huge file stays quick;
    /// the count says "1000+" past it.</summary>
    public const int MaxMatches = 1000;

    private static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);

    public static SearchResult FindAll(TextSnapshot snapshot, SearchQuery query, int limit = MaxMatches)
    {
        if (query.IsEmpty)
        {
            return SearchResult.None;
        }
        (string text, List<int> returns) = AsLines(snapshot.ToString());
        Regex? pattern;
        try
        {
            pattern = Build(query);
        }
        catch (ArgumentException error)
        {
            return new SearchResult([], false, error.Message);
        }
        List<TextRange> matches = [];
        // Kept whole, so a replacement's groups come from the match where it stood, its lookarounds and anchors held.
        List<Match>? found = query.Regex ? [] : null;
        bool crlf = snapshot.LineCount > 1 && snapshot.GetLine(0).BreakLength == 2;
        try
        {
            // An empty match counts (`^` is a line's start), and NextMatch steps on past it.
            for (Match match = pattern.Match(text); match.Success; match = match.NextMatch())
            {
                if (matches.Count == limit)
                {
                    return new SearchResult(matches, true, null) { Found = found, Crlf = crlf };
                }
                matches.Add(TextRange.FromBounds(Original(match.Index, returns), Original(match.Index + match.Length, returns)));
                found?.Add(match);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchResult(matches, true, "The search took too long and stopped.") { Found = found, Crlf = crlf };
        }
        return new SearchResult(matches, false, null) { Found = found, Crlf = crlf };
    }

    /// <summary>The text with each "\r\n" read as "\n", and where each '\r' left out was, in offsets of the text read.</summary>
    private static (string Text, List<int> Returns) AsLines(string text)
    {
        List<int> returns = [];
        if (!text.Contains("\r\n", StringComparison.Ordinal))
        {
            return (text, returns);
        }
        StringBuilder read = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                returns.Add(read.Length);
                continue;
            }
            read.Append(text[i]);
        }
        return (read.ToString(), returns);
    }

    /// <summary>An offset of the text read as lines in the text itself: past every '\r' left out before it, so a match
    /// that ends at a line's end ends before its whole break and one that takes the break takes both halves.</summary>
    private static int Original(int offset, List<int> returns)
    {
        int index = returns.BinarySearch(offset);
        return offset + (index >= 0 ? index : ~index);
    }

    /// <summary>The regex a query finds with, for text searched piece by piece (a table's cells); a pattern that does not
    /// parse throws an <see cref="ArgumentException"/> whose message says why.</summary>
    public static Regex Pattern(SearchQuery query) => Build(query);

    private static Regex Build(SearchQuery query)
    {
        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        string pattern = query.Regex ? RiderCarets(query.Text) : System.Text.RegularExpressions.Regex.Escape(query.Text);
        if (query.WholeWords)
        {
            // A group of its own, so both ends of an alternation are whole words: cat|dog finds no "catalog" or "hotdog".
            pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        }
        if (query.Regex && pattern != query.Text)
        {
            // Read as typed first, so a problem quotes the person's pattern and a stray ')' cannot close the group above.
            _ = new Regex(query.Text, options);
        }
        return new Regex(pattern, options, RegexLimit);
    }

    /// <summary>The pattern with each `^` anchor read as a line's start, but not the empty end after the text's last
    /// break, so prefixing every line leaves a file's final newline as it was.</summary>
    private static string RiderCarets(string pattern)
    {
        if (!pattern.Contains('^', StringComparison.Ordinal))
        {
            return pattern;
        }
        StringBuilder read = new(pattern.Length + 16);
        int classes = 0;
        bool dash = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                // An escape is the character after it, or the two after \c (\c^ is a control character).
                int length = pattern[i + 1] == 'c' && i + 2 < pattern.Length ? 3 : 2;
                read.Append(pattern, i, length);
                i += length - 1;
                dash = false;
                continue;
            }
            if (c == '[' && (classes == 0 || dash))
            {
                // A class's own ^ negates it and a ] first in it is a literal; "-[" inside one subtracts a class.
                int start = i;
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                {
                    i++;
                }
                if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                {
                    i++;
                }
                read.Append(pattern, start, i - start + 1);
                classes++;
            }
            else if (c == ']' && classes > 0)
            {
                read.Append(c);
                classes--;
            }
            else if (c == '^' && classes == 0)
            {
                read.Append(@"(?:^(?!\z)|\A)");
            }
            else
            {
                read.Append(c);
            }
            dash = c == '-' && classes > 0;
        }
        return read.ToString();
    }
}

/// <summary>Every match (sorted), whether there were more than were kept, and a problem with the
/// query itself (a regex that does not parse) if there was one; and what each match is replaced with.</summary>
public sealed record SearchResult(IReadOnlyList<TextRange> Matches, bool Truncated, string? Problem)
{
    public static readonly SearchResult None = new([], false, null);

    /// <summary>A regex's matches as found in the whole text, one for each of <see cref="Matches"/>; null for plain
    /// text, which is replaced as typed.</summary>
    internal IReadOnlyList<Match>? Found { get; init; }

    /// <summary>Whether the text's breaks are "\r\n", as a break a group carries over is then written.</summary>
    internal bool Crlf { get; init; }

    /// <summary>What match <paramref name="index"/> is replaced with: the replacement as typed, or for a regex with its
    /// groups ($1, ${name}) filled in from that match where it stood, its lookarounds, anchors and \b as they held.</summary>
    public string Replacement(int index, string replacement)
    {
        if (Found is null)
        {
            return replacement;
        }
        string replaced = Found[index].Result(replacement);
        // A break a group carried over is the file's own, as the text it replaces had.
        return Crlf && replaced.Contains('\n', StringComparison.Ordinal)
            ? replaced.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : replaced;
    }

    /// <summary>Every match's replacement, as edits of the text searched.</summary>
    public IReadOnlyList<TextEdit> Replacements(string replacement) =>
        [.. Matches.Select((match, i) => new TextEdit(match.Start, match.Length, Replacement(i, replacement)))];

    /// <summary>The first match at or after <paramref name="offset"/>, wrapping to the first.</summary>
    public int NextFrom(int offset)
    {
        for (int i = 0; i < Matches.Count; i++)
        {
            if (Matches[i].Start >= offset)
            {
                return i;
            }
        }
        return Matches.Count > 0 ? 0 : -1;
    }

    /// <summary>The last match before <paramref name="offset"/>, wrapping to the last.</summary>
    public int PreviousFrom(int offset)
    {
        for (int i = Matches.Count - 1; i >= 0; i--)
        {
            if (Matches[i].Start < offset)
            {
                return i;
            }
        }
        return Matches.Count - 1;
    }
}
