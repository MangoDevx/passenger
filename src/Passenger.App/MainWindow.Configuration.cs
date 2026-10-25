using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>A project's configuration as the window follows it: Manage User Secrets on a project (the Solution view's row, Find
/// Action on the file shown's project), which gives it a UserSecretsId where it has none and opens its secrets.json, made when
/// missing; and a settings file, user secrets or launch profile saved, after which the open C# files' reads of keys nothing
/// gave are looked at again, as their reads of localized keys are after a .resx or XLIFF file is saved.</summary>
public partial class MainWindow
{
    private void AddConfiguration()
    {
        actions!.Add(new IdeAction(A.ManageUserSecrets, "Manage User Secrets", ExplorerGroup, c => ManageUserSecrets(SecretsProjectOf(c)!.Path))
        {
            WhyNot = c => SecretsProjectOf(c) is not null ? null
                : RowOf(c) is { } row ? $"{row.Name} is not a project"
                : c.File is null ? Refusals.NoFile : $"{c.File.Name} is in no project",
        });
        Editors.Opened += (_, document) =>
        {
            if (ConfigurationFiles.IsSettings(document.Path) || ConfigurationFiles.SecretsIdOf(document.Path) is not null
                || Path.GetFileName(document.Path).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
            {
                document.Saved += (_, _) => session.CSharp?.ConfigurationChanged();
            }
            else if (Passenger.Xaml.Localization.ResourceFiles.Holds(document.Path))
            {
                document.Saved += (_, _) => session.CSharp?.LocalizationChanged();
            }
        };
    }

    /// <summary>The project Manage User Secrets acts on: the Explorer's project row where its menu or keys ask, else the project
    /// of the file shown.</summary>
    private ProjectModel? SecretsProjectOf(ActionContext context) => RowOf(context) is { } row
        ? row is { Kind: NodeKind.Project, Path: { } path } ? SolutionProject(path) : null
        : context.File is { } file ? ProjectOf(file.Path) : null;

    /// <summary>A project's user secrets opened in an editor tab: its UserSecretsId given first where it has none (a
    /// new GUID, set in its project file) and its secrets.json made where missing, both one command of global undo, in
    /// the user secrets' folder (<see cref="UserSecrets.Root"/>). Null once open, else why not, which the status bar
    /// says too.</summary>
    public string? ManageUserSecrets(string project)
    {
        if (SolutionProject(project) is not { } model)
        {
            return SecretsSaid($"{Path.GetFileName(project)} is not one of the solution's projects");
        }
        string name = Path.GetFileNameWithoutExtension(model.Path);
        string? id = model.Property(Passenger.Host.CarriedProperties.UserSecretsId) is { Length: > 0 } evaluated ? evaluated
            : ConfigurationFiles.Disk(model.Path) is { } text ? ProjectEdits.PropertyIn(text, "UserSecretsId") : null;
        bool given = id is null;
        id ??= Guid.NewGuid().ToString();
        if (UserSecrets.FileOf(id) is not { } secrets)
        {
            return SecretsSaid($"{name}'s UserSecretsId, {id}, names no folder for its secrets");
        }
        secrets = PathNames.Canonical(secrets);
        bool made = !File.Exists(secrets);
        if (given || made)
        {
            if (given && SaveBeforeWriting([model.Path]) is { } unsaved)
            {
                return SecretsSaid($"{name} could not be given user secrets: {unsaved}");
            }
            try
            {
                using CommandHistory.Recording recording = Editors.Commands.Record("Manage User Secrets");
                if (given)
                {
                    recording.Watch(model.Path);
                    try
                    {
                        if (ProjectEdits.SetProperty(model.Path, "UserSecretsId", id) is { } problem)
                        {
                            return SecretsSaid($"{name} could not be given user secrets: {problem}");
                        }
                    }
                    finally
                    {
                        // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                        FollowWritten([model.Path]);
                    }
                }
                if (made)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(secrets)!);
                    WriteNew(recording, secrets, "{\n}\n");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return SecretsSaid($"{name}'s user secrets could not be made: {error.Message}");
            }
        }
        OpenFile(secrets);
        return SecretsSaid(null, given ? $"Gave {name} the UserSecretsId {id}; its secrets.json is open, kept out of its files"
            : $"{name}'s secrets.json is open, kept out of its files");
    }

    private string? SecretsSaid(string? problem, string? done = null)
    {
        status.Text = problem ?? done ?? string.Empty;
        return problem;
    }
}
