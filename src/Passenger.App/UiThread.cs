using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>The one crossing onto the UI thread for work that comes from any other (a library's callback, a process's
/// output, a task's end): the work is held in <see cref="BackgroundWork"/> until it has run, and an exception in it goes
/// to <see cref="IdeErrors"/> instead of ending the IDE. Controllers and views are UI-thread objects.</summary>
public static class UiThread
{
    /// <summary>The process's one UI thread's dispatcher, every window's.</summary>
    private static volatile Dispatcher? owner;

    /// <summary>Takes the calling thread's dispatcher as the one work from other threads goes to, as each window does.
    /// Work that comes once that dispatcher has shut down is dropped with it, rather than queued on one made for the
    /// late thread, which nothing runs.</summary>
    public static void Own() => owner = Dispatcher.UIThread;

    /// <summary>Runs work on the UI thread, later, from any thread; only a crossing is held, since work posted on the
    /// UI thread is queued already where every wait runs it.</summary>
    public static void Post(Action work, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Post(work, DispatcherPriority.Default, file, line);

    /// <summary><see cref="Post(Action, string, int)"/> at a priority of its own: work that can wait for the window's input
    /// and drawing (a console's next batch of output) goes below them. The crossing's hold is named by its caller.</summary>
    public static void Post(Action work, DispatcherPriority priority, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Cross(work, priority, null, file, line);

    /// <summary><see cref="Post(Action, string, int)"/> for work that lets go of a hold its caller took (a program's
    /// end until its console says so): <paramref name="dropped"/> lets go of it in the work's place when the dispatcher
    /// will never run the work (one shut down), so the hold never outlives what was to end it.</summary>
    public static void Post(Action work, Action dropped, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Cross(work, DispatcherPriority.Default, dropped, file, line);

    private static void Cross(Action work, DispatcherPriority priority, Action? dropped, string file, int line)
    {
        Dispatcher? owned = owner;
        if (owned?.CheckAccess() ?? Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Run(work), priority);
            return;
        }
        IDisposable held = BackgroundWork.Scope(file, line);
        DispatcherOperation operation = (owned ?? Dispatcher.UIThread).InvokeAsync(() =>
        {
            try
            {
                Run(work);
            }
            finally
            {
                held.Dispose();
            }
        }, priority);
        // A dispatcher that has shut down aborts what it will never run.
        int aborted = 0;
        operation.Aborted += (_, _) => Drop();
        if (operation.Status == DispatcherOperationStatus.Aborted)
        {
            Drop();
        }

        void Drop()
        {
            if (Interlocked.Exchange(ref aborted, 1) == 0)
            {
                held.Dispose();
                dropped?.Invoke();
            }
        }
    }

    /// <summary>A handler to give a library, which may call it on any thread: each call runs on the UI thread.</summary>
    public static Action<T> Marshal<T>(Action<T> handler, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        value => Post(() => handler(value), file, line);

    /// <summary>Runs work on the UI thread for a caller on another that waits for its answer, and so for an exception
    /// in it too. Not background work: the caller holds it (an agent's tool, a test host waiting for its debugger).</summary>
    public static Task<T> InvokeAsync<T>(Func<Task<T>> work) => (owner ?? Dispatcher.UIThread).InvokeAsync(work);

    /// <inheritdoc cref="InvokeAsync{T}(Func{Task{T}})"/>
    public static Task<T> InvokeAsync<T>(Func<T> work) => (owner ?? Dispatcher.UIThread).InvokeAsync(work).GetTask();

    /// <summary>Fails, in a Debug build, when called off the UI thread: the guard on what only the UI thread may touch.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Check() => (owner ?? Dispatcher.UIThread).VerifyAccess();

    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }
}
