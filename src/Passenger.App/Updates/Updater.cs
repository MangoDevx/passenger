using Velopack.Locators;

namespace Passenger.App.Updates;

/// <summary>How this copy of Passenger installs a newer release: it downloads the release's package for this system in
/// the background, checks it against the SHA-256 the release gives (and the app's signature on macOS), and installs it
/// as Passenger ends, starting the new version when the person chose Restart to Update. Only a copy that Passenger's
/// installer (Windows) or disk image (macOS) put in place has one.</summary>
public abstract class Updater
{
    /// <summary>Where this copy is installed, for the log.</summary>
    public abstract string Installed { get; }

    /// <summary>The version made ready to install, or null.</summary>
    public string? Ready { get; protected set; }

    /// <summary>Downloads the release's package for this system and checks it, off the caller's thread, reporting the
    /// percent done. Never throws but for <paramref name="cancel"/>: a refusal or a failure is an answer.</summary>
    public async Task<UpdateOutcome> DownloadAsync(LatestRelease release, Action<int> progress, CancellationToken cancel)
    {
        Ready = null;
        try
        {
            UpdateOutcome outcome = await PrepareAsync(release, progress, cancel).ConfigureAwait(false);
            if (outcome is UpdateOutcome.Ready)
            {
                Ready = release.Version;
            }
            return outcome;
        }
        catch (UpdateRefusedException refused)
        {
            Discard();
            return new UpdateOutcome.Refused(refused.Message,
                $"Passenger {release.Version} was not installed: {refused.Message}{(refused.Detail is { } detail ? $" ({detail})" : string.Empty)}");
        }
        catch (Exception error) when (error is UpdateFailedException or IOException or UnauthorizedAccessException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            Discard();
            return new UpdateOutcome.Failed(error.Message, $"could not download Passenger {release.Version}: {error.Message}");
        }
    }

    /// <summary>What <see cref="DownloadAsync"/> does for this system: throws <see cref="UpdateRefusedException"/> for a
    /// package that is not what the release says, <see cref="UpdateFailedException"/> for one that would not come.</summary>
    protected abstract Task<UpdateOutcome> PrepareAsync(LatestRelease release, Action<int> progress, CancellationToken cancel);

    /// <summary>Installs the version made ready, as the process ends (its window closed, what it started ended), and
    /// starts it when <paramref name="restart"/>. A failure is logged, and leaves the copy as it was.</summary>
    public abstract void Install(bool restart);

    /// <summary>Drops a download made or under way, as Check for updates turned off asks.</summary>
    public abstract void Discard();

    /// <summary>Another Passenger running from this install, whose files an install would change under it: it waits
    /// for the last one to close.</summary>
    public abstract bool OthersRunning();

    /// <summary>The updater of the running copy, or null with why it cannot update itself: not in a disk image's app
    /// bundle or an installer's folder, or where it cannot replace itself.</summary>
    public static Updater? OfThisCopy(string stateFolder, out string why)
    {
        if (OperatingSystem.IsMacOS())
        {
            return DiskImageUpdater.OfThisBundle(Path.Combine(stateFolder, "updates"), out why);
        }
        if (OperatingSystem.IsWindows())
        {
            if (!Installation.Installed || !VelopackLocator.IsCurrentSet || VelopackLocator.Current.CurrentlyInstalledVersion is null)
            {
                why = $"{AppContext.BaseDirectory} is not where Passenger's installer put it";
                return null;
            }
            why = string.Empty;
            return new VelopackUpdater(VelopackLocator.Current);
        }
        why = "Passenger installs updates itself on macOS and Windows";
        return null;
    }
}

/// <summary>What downloading an update came to, with a line for the IDE's log.</summary>
public abstract record UpdateOutcome(string Said)
{
    /// <summary>Downloaded and checked, to install as Passenger ends.</summary>
    public sealed record Ready(string Said) : UpdateOutcome(Said);

    /// <summary>The release carries nothing this copy installs (no package for this system), so it is only announced.</summary>
    public sealed record Unavailable(string Said) : UpdateOutcome(Said);

    /// <summary>Downloaded, but not what the release says it is: removed, and the person told why.</summary>
    public sealed record Refused(string Reason, string Said) : UpdateOutcome(Said);

    /// <summary>Not downloaded: no network, an error, a full disk.</summary>
    public sealed record Failed(string Reason, string Said) : UpdateOutcome(Said);
}
