using System.Reflection;
using Avalonia;
using Avalonia.Threading;

namespace Passenger.Inspector;

/// <summary>The inspector inside the app, started by the startup hook in its own context: it connects to Passenger and takes
/// Avalonia's logger for the app's failed bindings before the app's Main, waits for the app's UI thread to exist (never
/// touching the dispatcher before the app makes it, which would make the wrong thread the UI's), says hello, and answers
/// each request there. It ends when Passenger goes.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's UI before it is up.</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>The app's UI dispatcher, once the app has made it.</summary>
    internal static Dispatcher? Ui { get; private set; }

    /// <summary>The inspector on the app's UI thread, once made.</summary>
    internal static Inspector? Current { get; private set; }

    /// <summary>Why the app keeps a top level from an agent's input, else null (<see cref="Guard"/>).</summary>
    internal static Func<object, string?>? Refusing { get; private set; }

    /// <summary>Keeps the agent's input from the top levels the app names a reason for, called with each one input
    /// would reach. Set before <see cref="Start"/>, on the app's UI thread.</summary>
    public static void Guard(Func<object, string?> refusing) => Refusing = refusing;

    /// <param name="overlays">The renderer debug overlays the launch named (<see cref="Wire.OverlaysVariable"/>), or null.</param>
    public static void Start(string socket, Thread main, string avalonia, string? overlays)
    {
        // Before the app's Main, so a binding that fails as its first window opens is heard.
        if (Connection.Open(socket) is not { } connection)
        {
            return;
        }
        Bindings.Start(connection);
        Thread thread = new(() => Serve(connection, main, avalonia, overlays)) { IsBackground = true, Name = "Passenger inspector" };
        thread.Start();
    }

    private static void Serve(Connection connection, Thread main, string avalonia, string? overlays)
    {
        using Connection held = connection;
        Dispatcher dispatcher = WaitForUi(main);
        Ui = dispatcher;
        Inspector inspector;
        try
        {
            inspector = dispatcher.Invoke(() => Current = new Inspector(connection, overlays));
        }
        catch (Exception error)
        {
            connection.Send(new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version, Environment.ProcessId, avalonia, $"The inspector could not start: {error.Message}") });
            // Written before the connection closes, so Passenger shows why rather than an app gone silent.
            connection.Drain();
            Bindings.Stop();
            return;
        }
        connection.Send(new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version, Environment.ProcessId, avalonia, null) });
        while (connection.Read() is { } request)
        {
            _ = AnswerAsync(dispatcher, inspector, connection, request);
        }
        Bindings.Stop();
        dispatcher.Post(inspector.Stop);
    }

    /// <summary>The app's UI dispatcher once the app has made it and its Application: the main thread's in a desktop app, or
    /// whichever thread made Avalonia's, read without making one.</summary>
    private static Dispatcher WaitForUi(Thread main)
    {
        FieldInfo? made = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static);
        while (true)
        {
            if ((Dispatcher.FromThread(main) ?? made?.GetValue(null) as Dispatcher) is { } dispatcher && HasApplication())
            {
                return dispatcher;
            }
            Thread.Sleep(Look);
        }
    }

    /// <summary>Whether the app's Application is up, after which its dispatcher has its platform's loop.</summary>
    internal static bool HasApplication()
    {
        try
        {
            return Application.Current is not null;
        }
        catch (Exception)
        {
            // Read off the UI thread while the app is setting up: asked again in a moment.
            return false;
        }
    }

    private static async Task AnswerAsync(Dispatcher dispatcher, Inspector inspector, Connection connection, Request request)
    {
        Reply reply;
        try
        {
            bool input = request.Method == Wire.Input;
            if (input)
            {
                // Hit testing reads the scene last rendered, so what changed since (a popup opening) is drawn first.
                await dispatcher.InvokeAsync(inspector.RenderedAsync);
            }
            reply = await dispatcher.InvokeAsync(() => inspector.Handle(request));
            if (input)
            {
                // Answered once what the input set going is laid out and drawn, so what the agent looks at next shows it.
                await dispatcher.InvokeAsync(inspector.RenderedAsync);
            }
        }
        catch (Exception error)
        {
            reply = new Reply { Id = request.Id, Error = error.Message };
        }
        connection.Send(reply);
    }
}
