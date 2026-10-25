namespace Passenger.Core;

/// <summary>The IDE's own log, passenger.log, for code that knows nothing of the app: a line from any thread goes to the
/// writer the app gives as it starts (<see cref="Install"/>), and nowhere in a process that gives none.</summary>
public static class PassengerLog
{
    private static volatile Action<string>? writer;

    /// <summary>Gives <see cref="Write"/> the app's log, which only the app knows.</summary>
    public static void Install(Action<string> write) => writer = write;

    public static void Write(string line) => writer?.Invoke(line);
}
