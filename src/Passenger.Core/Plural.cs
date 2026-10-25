namespace Passenger.Core;

/// <summary>A count and its noun: "1 file", "3 files", "0 files", never "1 files" or "2 file".</summary>
public static class Plural
{
    /// <summary>The count and its noun, the noun's plural <paramref name="many"/> or else the noun with an s.</summary>
    public static string Of(long count, string one, string? many = null) => $"{count} {Noun(count, one, many)}";

    /// <summary>The noun alone, for a count written its own way ("1,024 objects").</summary>
    public static string Noun(long count, string one, string? many = null) => count == 1 ? one : many ?? one + "s";
}
