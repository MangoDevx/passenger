using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Passenger.Core;

namespace Passenger.Debugger;

public static class DiagnosticClientHelper
{
    /// <summary>Lets go a process held at its start by its diagnostic port, as an attach does once the debugger is registered
    /// for its runtime; one already running is let be, its runtime's answer (<paramref name="answered"/>) ending the wait
    /// for a port. The variable that held it is cleared in the program first, so a .NET program it starts in turn (a build,
    /// a test host, a child it waits for) runs as it would undebugged rather than held for a debugger that never comes.
    /// Fails when the process or the session ends first, or 10 s pass with neither: no .NET in it, or none the debugger reaches.</summary>
    public static async Task LetGoIfHeld(Process process, Task answered, CancellationToken sessionEnded)
    {
        DateTime until = DateTime.UtcNow + PortLimit;
        while (!answered.IsCompleted)
        {
            if (sessionEnded.IsCancellationRequested)
            {
                throw new InvalidOperationException("the session ended before the debugger could take hold of it");
            }
            if (process.HasExited)
            {
                throw new InvalidOperationException("it ended before the debugger could take hold of it");
            }
            if (TryRelease(process.Id))
            {
                return;
            }
            if (DateTime.UtcNow >= until)
            {
                throw new InvalidOperationException("it is not a .NET program the debugger can reach: no runtime in it answered, and it opened no diagnostic port within 10 s");
            }
            // A program only just started opens its port a moment after it starts.
            await Task.WhenAny(answered, Task.Delay(50, sessionEnded));
        }
    }

    /// <summary>Lets go a process's runtime held at its start, as an attach would have: a runtime already running takes the
    /// word as done. A runtime opens its port as it starts, which on a busy machine comes after the session meant to take
    /// hold of it may have ended, so the port is waited for while the process lives, until it is <see cref="PortLimit"/> old
    /// (as an attach waits) and within <paramref name="within"/> when given. Null once asked, else why it could not be.</summary>
    public static string? Resume(int processId, TimeSpan? within = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return "it had ended";
        }
        using (process)
        {
            DateTime until = StartingUntil(process);
            if (within is { } given && DateTime.UtcNow + given < until)
            {
                until = DateTime.UtcNow + given;
            }
            while (true)
            {
                try
                {
                    if (TryRelease(processId))
                    {
                        return null;
                    }
                }
                catch (Exception error) when (error is ServerErrorException or IOException or TimeoutException or UnauthorizedAccessException
                    or InvalidOperationException)
                {
                    return error.Message;
                }
                if (Ended(process))
                {
                    return "it ended first";
                }
                if (DateTime.UtcNow >= until)
                {
                    return "it has no diagnostic port listening";
                }
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>A program a terminal has only just started may take a while to load its runtime on a busy machine.</summary>
    private static readonly TimeSpan PortLimit = TimeSpan.FromSeconds(10);

    /// <summary>Until when a process may yet open its diagnostic port: <see cref="PortLimit"/> after it started, or from now
    /// when the system will not say when it started.</summary>
    private static DateTime StartingUntil(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime() + PortLimit;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow + PortLimit;
        }
    }

    /// <summary>Whether the process has ended, false when the system will not say (the deadline then ends the wait).</summary>
    private static bool Ended(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Asks the process's runtime to let go, once: false while it has no diagnostic port listening (none yet,
    /// one closing, or one a process of the same id left when it was killed). On Windows the pipe is looked for first,
    /// since a client there waits 30 s on one that does not exist; elsewhere the client finds the socket at once, with
    /// no list of published processes made.</summary>
    private static bool TryRelease(int processId)
    {
        if (OperatingSystem.IsWindows() && !DiagnosticsClient.GetPublishedProcesses().Contains(processId))
        {
            return false;
        }
        try
        {
            Release(new DiagnosticsClient(processId));
            return true;
        }
        catch (ServerNotAvailableException)
        {
            return false;
        }
    }

    private static void Release(DiagnosticsClient client)
    {
        try
        {
            client.SetEnvironmentVariable(RuntimeVariables.DefaultDiagnosticPortSuspend, string.Empty);
        }
        catch (ServerErrorException)
        {
            // A runtime before .NET 6 has no such command: its children are still held, but it runs.
        }
        client.ResumeRuntime();
    }
}
