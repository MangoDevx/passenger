namespace Passenger.Core;

/// <summary>How far apart two spellings of a name are, the one measure behind every "did you mean" Passenger offers.
/// Framework-free, since it is linked into the inspectors, inside an app that never loads Passenger's assemblies.</summary>
public static class Spelling
{
    /// <summary>How many letters added, taken away, changed or swapped with the next make one word the other, letter case
    /// counted (a caller that sets case aside folds both first).</summary>
    public static int Distance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }
        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }
        return d[a.Length, b.Length];
    }

    /// <summary>The names among <paramref name="candidates"/> near a name, letter case aside: an edit or two away (one for a
    /// name of three letters or fewer), nearest first, then by name, three at most, never the name itself.</summary>
    public static IReadOnlyList<string> Near(string name, IEnumerable<string> candidates)
    {
        int most = name.Length <= 3 ? 1 : 2;
        string folded = name.ToLowerInvariant();
        return [.. candidates.Where(c => c != name).Distinct(StringComparer.Ordinal)
            .Select(c => (Name: c, Apart: Distance(c.ToLowerInvariant(), folded)))
            .Where(c => c.Apart <= most)
            .OrderBy(c => c.Apart).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(c => c.Name)];
    }
}
