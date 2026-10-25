using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>The platform seam that ends Passenger's children with it, a crash or a kill of Passenger's process included.
/// On Windows each child is in the runtime's job object (KillOnParentExit), which the system kills once Passenger's
/// handle to it is gone. macOS has neither a job nor a parent-death signal: there each child leads a session and process
/// group of its own, and a watchdog shell reading a pipe only Passenger writes kills every group still alive once that
/// pipe closes, which it does however Passenger's process ends. Linux is treated as macOS is.</summary>
internal static partial class Lifeline
{
    private const int SigKill = 9;

    /// <summary>ESRCH, on macOS and Linux alike: no process is in the group.</summary>
    private const int NoSuchProcess = 3;

    /// <summary>How often groups with no process left are struck off the watchdog's list, so it never kills a group whose
    /// number the system has since given to another.</summary>
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(10);

    /// <summary>The watchdog: "+N" puts group N on its list and "-N" strikes it off; "!C" makes C the command it runs last,
    /// after every group still listed at the end of its input is killed. It writes nothing, and a terminal's hangup or
    /// interrupt is not its to obey.</summary>
    private const string Watch = """
        exec >/dev/null 2>&1
        trap '' HUP INT
        groups=' '
        last=''
        while IFS= read -r line; do
          case $line in
            +*) groups="$groups${line#+} " ;;
            -*) case $groups in *" ${line#-} "*) groups="${groups%% ${line#-} *} ${groups#* ${line#-} }" ;; esac ;;
            !*) last="${line#!}" ;;
          esac
        done
        for group in $groups; do kill -s KILL -- "-$group"; done
        if [ -n "$last" ]; then eval "$last"; fi
        """;

    private static readonly object Gate = new();
    private static readonly HashSet<int> Groups = [];
    private static Process? watchdog;
    private static string? atEnd;
    private static bool unavailable;
    private static Timer? pruning;

    /// <summary>Sets a child up, before it starts, to end with Passenger.</summary>
    public static void Prepare(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows())
        {
            start.KillOnParentExit = true;
            return;
        }
        start.StartDetached = true;
        lock (Gate)
        {
            Watchdog();
        }
    }

    /// <summary>Puts a child that has started, and so its group, on the watchdog's list.</summary>
    public static void Adopt(Process process) => AdoptGroup(process.Id);

    /// <summary>Puts a process group that leads itself on the watchdog's list: a child's, or a program the terminal started
    /// in its pseudo-terminal.</summary>
    public static void AdoptGroup(int group)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        lock (Gate)
        {
            Groups.Add(group);
            Tell($"+{group}");
            pruning ??= new Timer(_ => Prune(), null, PruneEvery, PruneEvery);
        }
    }

    /// <summary>Has the watchdog run a one-line shell command once Passenger's process has ended and its children's groups
    /// are killed, in place of the one given before: for what no group holds, such as the containers an engine runs for
    /// Passenger, which only a command to that engine ends. Nothing on Windows, which has no watchdog.</summary>
    public static void AtEnd(string command)
    {
        if (OperatingSystem.IsWindows() || command.Contains('\n', StringComparison.Ordinal))
        {
            return;
        }
        lock (Gate)
        {
            atEnd = command;
            Tell($"!{command}");
        }
    }

    /// <summary>Kills what is left of a child's group: what it started, and what it left behind once it ended (Roslyn's
    /// BuildHost after its server, a build's nodes). Only a group of Passenger's own, still listed, is ever signalled.</summary>
    public static void KillGroup(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        int group;
        try
        {
            group = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        lock (Gate)
        {
            if (Groups.Contains(group))
            {
                Signal(-group, SigKill);
            }
        }
    }

    private static void Prune()
    {
        lock (Gate)
        {
            foreach (int group in Groups.ToList())
            {
                if (Signal(-group, 0) != 0 && Marshal.GetLastPInvokeError() == NoSuchProcess)
                {
                    Groups.Remove(group);
                    Tell($"-{group}");
                }
            }
        }
    }

    /// <summary>Writes a line to the watchdog. One that was ended by hand is replaced, and its successor is given every
    /// group listed, so the line need not be written again.</summary>
    private static void Tell(string line)
    {
        if (Watchdog() is not { } watching)
        {
            return;
        }
        try
        {
            watching.StandardInput.Write(line + "\n");
            watching.StandardInput.Flush();
        }
        catch (IOException)
        {
            Watchdog();
        }
    }

    /// <summary>The watchdog, started with the first child (again if it was ended by hand, with every group listed). Null
    /// where there is no shell to be it: children still run, only nothing ends them on a crash.</summary>
    private static Process? Watchdog()
    {
        if (unavailable || watchdog is { HasExited: false })
        {
            return watchdog;
        }
        watchdog?.Dispose();
        watchdog = null;
        try
        {
            // Its own session too, so a terminal's Ctrl+C or hangup, which ends Passenger, leaves it to do its work.
            ProcessStartInfo start = new("/bin/sh", ["-c", Watch])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                StartDetached = true,
            };
            Process started = Process.Start(start) ?? throw new InvalidOperationException("The watchdog did not start");
            foreach (int group in Groups)
            {
                started.StandardInput.Write($"+{group}\n");
            }
            if (atEnd is not null)
            {
                started.StandardInput.Write($"!{atEnd}\n");
            }
            started.StandardInput.Flush();
            watchdog = started;
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            unavailable = true;
        }
        return watchdog;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int process, int signal);
}
