using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Navigation;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Updates;
using Passenger.App.Welcome;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.App.State;

/// <summary>The IDE's own state, in one folder: the app's documents at its top (the settings, the layout a folder
/// without its own opens on, the recent solutions, the release last announced, Find in Files' mask, Rename's options),
/// and each folder's caches under <c>workspaces/&lt;name&gt;-&lt;hash&gt;/</c> (its documents in its own
/// <c>.passenger/</c>, <see cref="ProjectState"/>). Only the windows a person uses keep one, all of a
/// process's windows over the same folder sharing it (<see cref="Open"/>), so each document is held once and what one
/// window keeps another reads at once; a headless IDE starts on the defaults.</summary>
public sealed class StateStore
{
    /// <summary>The settings' file at the store's top.</summary>
    public const string SettingsFile = "settings.json";

    /// <summary>The stores open in this process by their folder, each shared by the windows keeping their state there. UI
    /// thread only.</summary>
    private static readonly Dictionary<string, StateStore> Shared = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProjectState> projects = new(StringComparer.Ordinal);
    private readonly string key;
    private int users;

    private StateStore(string root, string key)
    {
        Root = root;
        this.key = key;
        Settings = SettingsStore.Kept(new StateDocument<AppSettings>(Path.Combine(root, SettingsFile)));
        Layout = new(Path.Combine(root, "layout.json"));
        Recent = new(Path.Combine(root, "recentSolutions.json"));
        Updates = new(Path.Combine(root, "updates.json"));
        Find = new(Path.Combine(root, "find.json"));
        Refactorings = new(Path.Combine(root, "refactorings.json"));
        Previews = new(Path.Combine(root, "previews.json"));
        // Before this process writes any: a change is written a moment after it is made, and only through this store.
        UsedBefore = new[] { Path.Combine(root, SettingsFile), Layout.Path, Recent.Path, Updates.Path }.Any(File.Exists)
            || Directory.Exists(Path.Combine(root, "workspaces"));
    }

    /// <summary>Whether a Passenger kept its state here before this process opened the store: one of the app's documents,
    /// or a folder's, was there already (the logs, which every start writes, are none).</summary>
    public bool UsedBefore { get; }

    /// <summary>The variable that names the folder in the user's local application data's place.</summary>
    public const string Variable = "PASSENGER_SETTINGS";

    /// <summary>The folder: PASSENGER_SETTINGS names it, else the user's local application data.</summary>
    public static string DefaultRoot => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Passenger");

    public string Root { get; }

    /// <summary>The settings every window keeping its state here follows.</summary>
    public SettingsStore Settings { get; }

    /// <summary>The store of a state folder, shared by every window of this process that keeps its state there; let go
    /// with <see cref="Release"/>.</summary>
    public static StateStore Open(string root)
    {
        UiThread.Check();
        string key = Path.GetFullPath(root);
        if (!Shared.TryGetValue(key, out StateStore? store))
        {
            store = new StateStore(root, key);
            Shared[key] = store;
        }
        store.users++;
        return store;
    }

    /// <summary>A window's hold let go: the last one writes what is waiting and stops watching the settings.</summary>
    public void Release()
    {
        UiThread.Check();
        if (--users > 0)
        {
            return;
        }
        Flush();
        Settings.Dispose();
        if (Shared.TryGetValue(key, out StateStore? open) && open == this)
        {
            Shared.Remove(key);
        }
    }

    /// <summary>The tool windows' layout as it was changed last, which a folder with none of its own opens on.</summary>
    public StateDocument<DockLayout> Layout { get; }

    /// <summary>The folders and solutions opened lately, which a start reopens.</summary>
    public StateDocument<RecentSolutionsState> Recent { get; }

    /// <summary>The newest release the window has said is out, which it says once.</summary>
    public StateDocument<UpdatesState> Updates { get; }

    /// <summary>Find in Files' file mask, the masks used lately and whether directories are searched recursively, which
    /// every folder shares.</summary>
    public StateDocument<FindSettingsState> Find { get; }

    /// <summary>Rename's options as they were left, which every folder shares.</summary>
    public StateDocument<RenameSettings> Refactorings { get; }

    /// <summary>Which kinds of file show their preview beside their editor, as the person left them.</summary>
    public StateDocument<PreviewsState> Previews { get; }

    /// <summary>A folder's own documents.</summary>
    public ProjectState Project(string folder)
    {
        if (!projects.TryGetValue(folder, out ProjectState? project))
        {
            project = new ProjectState(Root, folder);
            projects[folder] = project;
        }
        return project;
    }

    /// <summary>Writes every change still waiting, now.</summary>
    public void Flush()
    {
        Settings.Flush();
        Layout.Flush();
        Recent.Flush();
        Updates.Flush();
        Find.Flush();
        Refactorings.Flush();
        Previews.Flush();
        foreach (ProjectState project in projects.Values)
        {
            project.Flush();
        }
    }

    /// <summary>The step from a file written before documents had versions to the first version, which has its shape.</summary>
    public static void Unversioned(JsonObject document)
    {
    }
}

/// <summary>A folder's documents, in the folder's own <c>.passenger/</c> as Rider keeps <c>.idea/</c>: its run configurations
/// at the top, meant to be shared, and under <c>workspace/</c>, which the folder's <c>.gitignore</c> there keeps out of
/// version control, the person's own (what its window had open, its layout, breakpoints, bookmarks, the configuration
/// chosen, Find in Files' scope, the Unit Tests window's view, the Git log's favourite branches, the publish profiles last
/// used). What its .http requests may take from the person's computer, which a cloned folder must not grant, and the
/// caches (its projects as MSBuild last evaluated and built them) stay in the store's <see cref="Folder"/>.</summary>
public sealed class ProjectState
{
    /// <summary>The folder's own folder of Passenger's documents, beside a solution as <c>.vs</c> and <c>.idea</c> are.</summary>
    public const string SharedName = ".passenger";

    /// <summary>The written <c>.gitignore</c>: it ignores itself too, so a folder whose person shares nothing shows no change.</summary>
    private const string Ignored = "# Passenger's: the person's own state stays out of version control, the rest of this folder is shared.\n/.gitignore\n/workspace/\n";

    internal ProjectState(string root, string folder)
    {
        Folder = FolderOf(root, folder);
        Shared = Path.Combine(folder, SharedName);
        Personal = Path.Combine(Shared, "workspace");
        Workspace = new(Path.Combine(Personal, "workspace.json"), [Path.Combine(Folder, "workspace.json"), Folder + ".json"], Ignore);
        Layout = Own<DockLayout>("layout.json");
        Breakpoints = Own<BreakpointsState>("breakpoints.json");
        Bookmarks = Own<BookmarksState>("bookmarks.json");
        RunConfigurations = new(Path.Combine(Shared, "runConfigurations.json"), [Path.Combine(Folder, "runConfigurations.json")], Ignore);
        RunChoice = new(Path.Combine(Personal, "runChoice.json"), [Path.Combine(Folder, "runConfigurations.json")], Ignore);
        Find = Own<FindScopeState>("find.json");
        UnitTests = Own<Testing.UnitTestsState>("unitTests.json");
        GitBranches = Own<ToolWindows.GitBranchesState>("gitBranches.json");
        Endpoints = new(Path.Combine(Folder, "endpoints.json"));
        Publish = Own<Run.PublishState>("publish.json");
        ProjectModels = new(Path.Combine(Folder, "projectModels.json"));
        ProjectBuilds = new(Path.Combine(Folder, "projectBuilds.json"));
    }

    /// <summary>The folder's <c>.passenger/</c>: what is meant to be shared at its top.</summary>
    public string Shared { get; }

    /// <summary>The person's own documents, <c>.passenger/workspace/</c>.</summary>
    public string Personal { get; }

    /// <summary>A personal document, read from the store's folder, where builds before <c>.passenger/</c> kept it, until
    /// written here.</summary>
    private StateDocument<T> Own<T>(string name) where T : class, IStateDocument<T>, new() =>
        new(Path.Combine(Personal, name), [Path.Combine(Folder, name)], Ignore);

    /// <summary>Before anything is written into <c>.passenger/</c>, on whichever thread writes it: its <c>.gitignore</c>,
    /// unless the folder has one. One that cannot be written is logged and costs the document nothing.</summary>
    private void Ignore()
    {
        string file = Path.Combine(Shared, ".gitignore");
        // Two documents' writes land together, and on Windows the second move over the first's file fails.
        lock (StateFile.Lock(file))
        {
            try
            {
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(Shared);
                    AtomicFile.Write(file, Encoding.UTF8.GetBytes(Ignored));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StateFile.Log($"{file} could not be written ({error.Message})");
            }
        }
    }

    /// <summary>The store's folder for the folder: its caches and grants, which never go into the folder itself.</summary>
    public string Folder { get; }

    /// <summary>Where a store keeps an open folder's own things, <c>workspaces/&lt;name&gt;-&lt;hash&gt;</c>: its
    /// documents, and the profiler's snapshots of its programs, which a window keeps whether or not it keeps state.</summary>
    public static string FolderOf(string root, string folder)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..12];
        return Path.Combine(root, "workspaces", $"{Path.GetFileName(folder)}-{hash}");
    }

    /// <summary>The editors' splits, tabs and carets, the recent files and the projects unloaded.</summary>
    public StateDocument<WorkspaceState> Workspace { get; }

    public StateDocument<DockLayout> Layout { get; }

    public StateDocument<BreakpointsState> Breakpoints { get; }

    public StateDocument<BookmarksState> Bookmarks { get; }

    /// <summary>The folder's own run configurations and the changes to generated ones, shared.</summary>
    public StateDocument<RunConfigurationsState> RunConfigurations { get; }

    /// <summary>The configuration the person chose in the toolbar.</summary>
    public StateDocument<RunChoiceState> RunChoice { get; }

    /// <summary>Find in Files' scope: the one chosen last, and the project, directory and named scope it showed.</summary>
    public StateDocument<FindScopeState> Find { get; }

    /// <summary>How the Unit Tests window groups the folder's tests and which outcomes it shows.</summary>
    public StateDocument<Testing.UnitTestsState> UnitTests { get; }

    /// <summary>The branches marked and unmarked as favourites in the Git log.</summary>
    public StateDocument<ToolWindows.GitBranchesState> GitBranches { get; }

    /// <summary>The environment variables and files outside the folder its .http requests may send, each to one host.</summary>
    public StateDocument<Passenger.App.Endpoints.EndpointsState> Endpoints { get; }

    /// <summary>The publish profile each project last published with, which Publish Again publishes it with.</summary>
    public StateDocument<Run.PublishState> Publish { get; }

    /// <summary>The folder's projects as MSBuild last evaluated them, which it opens on.</summary>
    public StateDocument<ProjectModelCache> ProjectModels { get; }

    /// <summary>The folder's projects' design-time builds, which Roslyn's server loads them from.</summary>
    public StateDocument<ProjectBuildCache> ProjectBuilds { get; }

    /// <summary>Reads, off the UI thread and at once, the documents a folder's open restores, so the restore takes them in
    /// with no read of its own. Done at once for those read already.</summary>
    public Task ReadAsync() => Task.WhenAll(Workspace.ValueAsync(), Layout.ValueAsync(), Breakpoints.ValueAsync(), Bookmarks.ValueAsync(),
        RunConfigurations.ValueAsync(), RunChoice.ValueAsync(), Find.ValueAsync(), UnitTests.ValueAsync(), GitBranches.ValueAsync(), Endpoints.ValueAsync(),
        Publish.ValueAsync());

    public void Flush()
    {
        ProjectModels.Flush();
        ProjectBuilds.Flush();
        Workspace.Flush();
        Layout.Flush();
        Breakpoints.Flush();
        Bookmarks.Flush();
        RunConfigurations.Flush();
        RunChoice.Flush();
        Find.Flush();
        UnitTests.Flush();
        GitBranches.Flush();
        Endpoints.Flush();
        Publish.Flush();
    }
}
