using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Passenger.Dap;

/// <summary>The hit count modes, as a breakpoint's hitCondition writes them.</summary>
public enum HitConditionMode
{
    /// <summary>==N, or N alone, or Visual Studio's =N.</summary>
    EqualTo,

    /// <summary>&gt;N</summary>
    GreaterThan,

    /// <summary>&gt;=N</summary>
    GreaterThanOrEqualTo,

    /// <summary>&lt;N</summary>
    LessThan,

    /// <summary>&lt;=N</summary>
    LessThanOrEqualTo,

    /// <summary>%N: every Nth hit.</summary>
    MultipleOf,
}

/// <summary>A breakpoint's hit count, read from the protocol's hitCondition (whose form the protocol leaves to the
/// adapter): a mode and a number of hits from 1.</summary>
public readonly record struct HitCondition(HitConditionMode Mode, int Count)
{
    /// <summary>The modes as written, longest first so ">=" is not read as ">".</summary>
    private static readonly (string Text, HitConditionMode Mode)[] Modes =
    [
        (">=", HitConditionMode.GreaterThanOrEqualTo),
        ("<=", HitConditionMode.LessThanOrEqualTo),
        ("==", HitConditionMode.EqualTo),
        (">", HitConditionMode.GreaterThan),
        ("<", HitConditionMode.LessThan),
        ("=", HitConditionMode.EqualTo),
        ("%", HitConditionMode.MultipleOf),
    ];

    /// <summary>Reads a hit count ("==5", ">= 10", "%2", "3"), or says why it cannot be read.</summary>
    public static bool TryParse(string text, out HitCondition condition, [NotNullWhen(false)] out string? problem)
    {
        ReadOnlySpan<char> rest = text.AsSpan().Trim();
        HitConditionMode mode = HitConditionMode.EqualTo;
        foreach ((string written, HitConditionMode named) in Modes)
        {
            if (rest.StartsWith(written, StringComparison.Ordinal))
            {
                mode = named;
                rest = rest[written.Length..].TrimStart();
                break;
            }
        }
        if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0)
        {
            condition = new HitCondition(mode, count);
            problem = null;
            return true;
        }
        condition = default;
        problem = $"The hit count \"{text.Trim()}\" is not one the debugger can read: a hit count is a number from 1, alone or after "
            + "== (equal to), > (greater than), >= (greater than or equal to), < (less than), <= (less than or equal to) or % (multiple of).";
        return false;
    }

    /// <summary>Whether the breakpoint stops on this hit of it, counting from 1.</summary>
    public bool IsMet(int hits) => Mode switch
    {
        HitConditionMode.EqualTo => hits == Count,
        HitConditionMode.GreaterThan => hits > Count,
        HitConditionMode.GreaterThanOrEqualTo => hits >= Count,
        HitConditionMode.LessThan => hits < Count,
        HitConditionMode.LessThanOrEqualTo => hits <= Count,
        HitConditionMode.MultipleOf => hits % Count == 0,
        _ => false,
    };
}
