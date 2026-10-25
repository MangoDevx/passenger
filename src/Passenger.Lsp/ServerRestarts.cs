namespace Passenger.Lsp;

/// <summary>How often a language server that keeps ending or hanging is started again in its place: three times within
/// five minutes, after which it is left stopped. Asked on its owner's thread alone.</summary>
public sealed class ServerRestarts
{
    private const int Limit = 3;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly List<DateTime> restarts = [];

    /// <summary>How often a server left stopped stopped: "4 times in 5 minutes".</summary>
    public static string Often => $"{Limit + 1} times in {Window.TotalMinutes:0} minutes";

    /// <summary>Whether a server lost now is started again, which counts as one of its restarts.</summary>
    public bool Again()
    {
        DateTime now = DateTime.UtcNow;
        restarts.RemoveAll(at => now - at > Window);
        if (restarts.Count >= Limit)
        {
            return false;
        }
        restarts.Add(now);
        return true;
    }

    /// <summary>What the status bar says of a server lost (<paramref name="what"/>: "The C# language server ended"), started
    /// <paramref name="again"/> or left stopped.</summary>
    public static string Said(string what, bool again) =>
        again ? $"{what}; starting it again" : $"{what} {Often}, so it was left stopped; the IDE's log says why";
}
