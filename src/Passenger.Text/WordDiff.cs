namespace Passenger.Text;

/// <summary>Where two texts differ inside lines that changed: characters <see cref="OldStart"/> to <see cref="OldEnd"/>
/// of the first in place of <see cref="NewStart"/> to <see cref="NewEnd"/> of the second. Either side may be empty: a
/// word added, or one taken away.</summary>
public readonly record struct InlineDiff(int OldStart, int OldEnd, int NewStart, int NewEnd)
{
    public bool IsInsertion => OldStart == OldEnd;

    public bool IsDeletion => NewStart == NewEnd;
}

/// <summary>Changed lines compared again inside themselves: by words, the spaces and punctuation between matched words
/// compared character by character from both ends, so a changed word is marked alone and a comma or a space added is
/// marked as itself; or by characters. A word is a run of letters, digits and underscores (any character but a space
/// and ASCII punctuation), each character of a script written without spaces (Chinese, Japanese, Thai) a word of its
/// own, and each line break one too. Any thread may compare.</summary>
public static class WordDiff
{
    /// <summary>The most words (or characters) the two sides may have together: past it a change is shown by its lines
    /// alone, so a huge rewrite costs a bounded time.</summary>
    private const int MostTokens = 20_000;

    /// <summary>The words that differ, or null when the two are too big to compare. A difference covering both texts whole
    /// says no more than the lines do, so none is given for it.</summary>
    public static IReadOnlyList<InlineDiff>? Words(string before, string after)
    {
        List<(int Start, int End)> oldChunks = Chunks(before);
        List<(int Start, int End)> newChunks = Chunks(after);
        if (oldChunks.Count + newChunks.Count > MostTokens)
        {
            return null;
        }
        string[] oldWords = [.. oldChunks.Select(c => before[c.Start..c.End])];
        string[] newWords = [.. newChunks.Select(c => after[c.Start..c.End])];
        List<InlineDiff> found = [];
        int oldFrom = 0;
        int newFrom = 0;
        foreach ((int i, int j) in Matched(LineDiff.Hunks(oldWords, newWords), oldWords.Length, newWords.Length))
        {
            Gap(before, after, oldFrom, oldChunks[i].Start, newFrom, newChunks[j].Start, found);
            oldFrom = oldChunks[i].End;
            newFrom = newChunks[j].End;
        }
        Gap(before, after, oldFrom, before.Length, newFrom, after.Length, found);
        return Whole(found, before, after) ? [] : found;
    }

    /// <summary>The characters that differ, or null when the two are too big to compare.</summary>
    public static IReadOnlyList<InlineDiff>? Characters(string before, string after)
    {
        if (before.Length + after.Length > MostTokens)
        {
            return null;
        }
        string[] oldChars = [.. before.Select(c => c.ToString())];
        string[] newChars = [.. after.Select(c => c.ToString())];
        List<InlineDiff> found = [.. LineDiff.Hunks(oldChars, newChars)
            .Select(h => new InlineDiff(h.OldStart, h.OldStart + h.OldCount, h.NewStart, h.NewStart + h.NewCount))];
        return Whole(found, before, after) ? [] : found;
    }

    private static bool Whole(List<InlineDiff> found, string before, string after) =>
        found.Count == 1 && found[0] == new InlineDiff(0, before.Length, 0, after.Length);

    /// <summary>The pairs of tokens the comparison matched, in order: those outside its hunks.</summary>
    private static IEnumerable<(int Old, int New)> Matched(IReadOnlyList<DiffHunk> hunks, int oldCount, int newCount)
    {
        int i = 0;
        int j = 0;
        foreach (DiffHunk hunk in hunks)
        {
            for (; i < hunk.OldStart; i++, j++)
            {
                yield return (i, j);
            }
            i = hunk.OldStart + hunk.OldCount;
            j = hunk.NewStart + hunk.NewCount;
        }
        for (; i < oldCount && j < newCount; i++, j++)
        {
            yield return (i, j);
        }
    }

    /// <summary>What lies between two matched words on each side (unmatched words, spaces, punctuation), less the spaces
    /// and punctuation both sides begin and end it with, as one difference when anything is left.</summary>
    private static void Gap(string before, string after, int oldStart, int oldEnd, int newStart, int newEnd, List<InlineDiff> found)
    {
        while (oldStart < oldEnd && newStart < newEnd && before[oldStart] == after[newStart] && !IsWordPart(before[oldStart]))
        {
            oldStart++;
            newStart++;
        }
        while (oldEnd > oldStart && newEnd > newStart && before[oldEnd - 1] == after[newEnd - 1] && !IsWordPart(before[oldEnd - 1]))
        {
            oldEnd--;
            newEnd--;
        }
        if (oldStart < oldEnd || newStart < newEnd)
        {
            found.Add(new InlineDiff(oldStart, oldEnd, newStart, newEnd));
        }
    }

    /// <summary>A text's words and line breaks, where each begins and ends.</summary>
    private static List<(int Start, int End)> Chunks(string text)
    {
        List<(int Start, int End)> chunks = [];
        int word = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool part = IsWordPart(c) && !IsContinuousScript(c);
            if (part)
            {
                if (word < 0)
                {
                    word = i;
                }
                continue;
            }
            if (word >= 0)
            {
                chunks.Add((word, i));
                word = -1;
            }
            if (IsWordPart(c))
            {
                chunks.Add((i, i + 1));
            }
            else if (c == '\n')
            {
                chunks.Add((i, i + 1));
            }
        }
        if (word >= 0)
        {
            chunks.Add((word, text.Length));
        }
        return chunks;
    }

    /// <summary>A word's character: anything but a space or ASCII punctuation, the underscore a letter.</summary>
    private static bool IsWordPart(char c) => !char.IsWhiteSpace(c) && !IsPunctuation(c);

    private static bool IsPunctuation(char c) => c != '_' && c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// <summary>Scripts written without spaces between words, whose every character is a word: Thai, the kana and the
    /// CJK ideographs.</summary>
    private static bool IsContinuousScript(char c) => c is (>= '฀' and <= '๿') or (>= '぀' and <= 'ヿ')
        or (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿') or (>= '豈' and <= '﫿');
}
