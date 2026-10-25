namespace Passenger.Text;

/// <summary>How well a typed query names a file: the file's own name (exact, then prefix, then CamelHumps initials like
/// "CE" for CodeEditor, then anywhere in it, then its letters close together and in order), or its path when the query
/// has a slash. Higher is better; null is no match. A name is read where it is in its path, nothing copied, since this
/// is asked of every file in the folder for every key.</summary>
public static class FuzzyMatch
{
    public static int? Score(string query, string relativePath)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        if (query.Contains('/') || query.Contains('\\'))
        {
            string normalized = relativePath.Replace('\\', '/');
            string wanted = query.Replace('\\', '/');
            if (normalized.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return 600 - normalized.Length;
            }
            return InOrder(wanted, normalized) is { } spread ? 300 - spread - normalized.Length / 4 : null;
        }
        ReadOnlySpan<char> name = Path.GetFileName(relativePath.AsSpan());
        // Each way of matching below finds the query's letters in the name in order: most names fail this one look.
        if (!Within(query, name))
        {
            return null;
        }
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(name);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase) || stem.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 - relativePath.Length / 8;
        }
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 900 - name.Length - relativePath.Length / 8;
        }
        if (Humps(query, stem))
        {
            return 850 - name.Length - relativePath.Length / 8;
        }
        int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return 700 - at - name.Length - relativePath.Length / 8;
        }
        // Letters in order only once there are enough to mean something: two found anywhere match nearly every file.
        if (query.Length >= 3 && InOrder(query, name) is { } nameSpread && nameSpread <= name.Length / 2)
        {
            return 500 - nameSpread - name.Length;
        }
        return null;
    }

    /// <summary>How well a typed word names a completion item's label, which is no path ('/', '\' and '.' are its own
    /// characters): a prefix in its own case, then in any case, then CamelHumps from its first word ("SB" for
    /// StringBuilder, "gTOD" for GetTypeOrDefault), then from a later one, then two letters or more anywhere in it;
    /// within each band the shorter label first. Higher is better; null is no match. Nothing is copied, as completion
    /// asks this of every item on every key.</summary>
    public static int? Label(string query, string label)
    {
        if (query.Length == 0)
        {
            return 0;
        }
        if (label.StartsWith(query, StringComparison.Ordinal))
        {
            return 4000 - label.Length;
        }
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3000 - label.Length;
        }
        if (!Within(query, label))
        {
            return null;
        }
        if (Humps(query, label))
        {
            // The humps take the label's first word whenever its first letter is the query's.
            return (char.ToLowerInvariant(label[0]) == char.ToLowerInvariant(query[0]) ? 2000 : 1500) - label.Length;
        }
        if (query.Length >= 2 && label.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 - label.Length;
        }
        return null;
    }

    /// <summary>Whether the query's letters are in the name in order, a letter matching in either case (as each way of
    /// matching above takes it); a query with a surrogate pair, which ignoring case compares whole, always is.</summary>
    private static bool Within(string query, ReadOnlySpan<char> name)
    {
        foreach (char c in query)
        {
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }
        int q = 0;
        for (int i = 0; i < name.Length && q < query.Length; i++)
        {
            char a = name[i];
            char b = query[q];
            bool same = a == b || (char.IsAscii(a) && char.IsAscii(b)
                ? (a | 0x20) == (b | 0x20) && char.IsAsciiLetter(a)
                : char.ToLowerInvariant(a) == char.ToLowerInvariant(b) || char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
            if (same)
            {
                q++;
            }
        }
        return q == query.Length;
    }

    /// <summary>Whether the query's letters are the starts of the name's words in order: "CE" and
    /// "CoEd" for CodeEditor, "fm" for fuzzy_match.</summary>
    private static bool Humps(string query, ReadOnlySpan<char> name)
    {
        int q = 0;
        for (int start = 0; start < name.Length && q < query.Length; start++)
        {
            if (!Starts(name, start))
            {
                continue;
            }
            int i = start;
            if (char.ToLowerInvariant(name[i]) != char.ToLowerInvariant(query[q]))
            {
                continue;
            }
            // A hump may take more than its first letter: "CoEd" is Co + Ed.
            while (q < query.Length && i < name.Length && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(query[q])
                   && (i == start || !Starts(name, i)))
            {
                q++;
                i++;
            }
        }
        return q == query.Length && query.Length > 1;
    }

    /// <summary>Whether a letter or digit starts a word: the first character, one after a separator,
    /// or a capital after a non-capital or before lowercase (the A in IPAddress).</summary>
    private static bool Starts(ReadOnlySpan<char> name, int i) =>
        (i == 0 || char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]))
            || name[i - 1] is '_' or '-' or '.' or ' ') && char.IsLetterOrDigit(name[i]);

    /// <summary>How spread out the query's letters are when found in order, or null if they are not.</summary>
    private static int? InOrder(string query, ReadOnlySpan<char> text)
    {
        int q = 0;
        int first = -1;
        int last = -1;
        for (int i = 0; i < text.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(query[q]))
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
                q++;
            }
        }
        return q == query.Length ? last - first + 1 - query.Length : null;
    }
}
