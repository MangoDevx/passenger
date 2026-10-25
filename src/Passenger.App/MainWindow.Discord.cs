using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.App.Discord;
using Passenger.App.Settings;

namespace Passenger.App;

/// <summary>Discord Rich Presence: while Settings' Show activity in Discord is on (as it is at first) and a solution is
/// open, the person's own window has Discord show Playing Passenger with its icon, Passenger's own words or, as the
/// person chooses, the solution and the file, and the time since the solution opened; only Passenger once the person
/// has been idle a while; nothing once the setting goes off, the solution closes or Passenger quits. Of several of the
/// person's windows, the one in front alone gives Discord its activity, through the one presence they share
/// (<see cref="PassengerApp"/>). No other window (a test's, an agent's, a screenshot's) reaches Discord.</summary>
public partial class MainWindow
{
    /// <summary>The longest between two looks at whether the person has gone idle.</summary>
    private static readonly TimeSpan IdleLook = TimeSpan.FromSeconds(15);

    private DiscordPresence? discord;
    private bool sharesActivity;
    private string? timedSolution;
    private DateTimeOffset solutionOpened;
    private long lastStir = Stopwatch.GetTimestamp();
    private bool idleInDiscord;
    private DispatcherTimer? idleWatch;

    /// <summary>Where this window looks for Discord: the system's places, or the one PASSENGER_DISCORD_IPC names,
    /// unless one is given.</summary>
    public DiscordIpc DiscordIpc { get; init; } = DiscordIpc.Default;

    /// <summary>How long without a key, click or scroll before only Passenger shows: Settings' minutes, unless
    /// given.</summary>
    public TimeSpan? DiscordIdleAfter { get; init; }

    private void AddDiscord()
    {
        // Any key, click, scroll or caret move anywhere in the IDE is the person at work.
        AddHandler(PointerWheelChangedEvent, (_, _) => Stir(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Editors.CaretMoved += (_, _) => Stir();
    }

    /// <summary>The person's window shows its activity in Discord while Settings says so (called for the person's
    /// window alone).</summary>
    public void ShowActivityInDiscord()
    {
        sharesActivity = true;
        FollowInDiscord();
    }

    /// <summary>Gives Discord the activity as the solution, the file and the settings now stand, whenever one changes (the
    /// window's title follows the same) and as the window comes in front; its presence sends it in its own time, so this
    /// costs the UI thread nothing. Of several windows, the one Discord shows (<see cref="PassengerApp.ShowsInDiscord"/>)
    /// gives it its own.</summary>
    internal void FollowInDiscord()
    {
        string? solution = Folder is null ? null : session.Workspace?.OpenedAs ?? Folder;
        if (solution != timedSolution)
        {
            timedSolution = solution;
            solutionOpened = DateTimeOffset.UtcNow;
        }
        if (!sharesActivity)
        {
            return;
        }
        if (!settings.ShowInDiscord)
        {
            LetGoOfDiscord();
            return;
        }
        WatchIdle(settings.DiscordIdle);
        (PassengerApp.ShowsInDiscord(DiscordIpc) ?? this).ShowInDiscord();
    }

    /// <summary>This window's activity, in place of whatever Discord showed.</summary>
    private void ShowInDiscord()
    {
        discord ??= PassengerApp.Presence(DiscordIpc);
        discord.Show(ActivityNow());
    }

    /// <summary>The activity: none without a solution, only Passenger (in its own words) while the person is idle, else
    /// what Settings chose.</summary>
    private DiscordActivity? ActivityNow()
    {
        if (Folder is null)
        {
            return null;
        }
        if (idleInDiscord)
        {
            return new DiscordActivity(DiscordPresence.OwnDetails, DiscordPresence.OwnState, null);
        }
        string named = session.Workspace?.Name ?? Path.GetFileName(Folder);
        return settings.DiscordShows switch
        {
            DiscordShown.PassengerOnly => new DiscordActivity(DiscordPresence.OwnDetails, DiscordPresence.OwnState, solutionOpened),
            DiscordShown.Solution => new DiscordActivity(named, null, solutionOpened),
            _ => new DiscordActivity(named, Editors.Active is { } file ? $"Editing {file.Name}" : null, solutionOpened),
        };
    }

    /// <summary>The activity cleared and Discord let go of, as the setting goes off: by the same presence that showed it,
    /// so a clear never comes after an activity shown once the setting is back on.</summary>
    private void LetGoOfDiscord()
    {
        WatchIdle(false);
        discord?.Show(null);
    }

    /// <summary>As the window closes: the window in front after it shows its activity, or with none left the activity is
    /// cleared, which the process waits for (<see cref="Ended"/>) for at most the IPC's timeout.</summary>
    private Task EndDiscord()
    {
        bool shared = sharesActivity;
        sharesActivity = false;
        WatchIdle(false);
        discord = null;
        return shared ? PassengerApp.LetGoOfDiscord(DiscordIpc) : Task.CompletedTask;
    }

    private TimeSpan IdleAfter => DiscordIdleAfter ?? TimeSpan.FromMinutes(settings.DiscordIdleMinutes);

    /// <summary>Looks every so often at how long it has been since the person did anything, while the idle time is on.</summary>
    private void WatchIdle(bool on)
    {
        if (!on)
        {
            idleWatch?.Stop();
            idleWatch = null;
            idleInDiscord = false;
            return;
        }
        TimeSpan every = IdleAfter / 4 < IdleLook ? IdleAfter / 4 : IdleLook;
        if (idleWatch is null)
        {
            idleWatch = new DispatcherTimer { Interval = every };
            idleWatch.Tick += (_, _) => LookForIdle();
            idleWatch.Start();
        }
        else if (idleWatch.Interval != every)
        {
            idleWatch.Interval = every;
        }
    }

    private void LookForIdle()
    {
        if (!idleInDiscord && Stopwatch.GetElapsedTime(lastStir) >= IdleAfter)
        {
            idleInDiscord = true;
            FollowInDiscord();
        }
    }

    private void Stir()
    {
        lastStir = Stopwatch.GetTimestamp();
        if (idleInDiscord)
        {
            idleInDiscord = false;
            FollowInDiscord();
        }
    }
}
