using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.App.Run;
using Passenger.Editor;
using E = Passenger.Editor.EditorActions;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The window's actions: the registry every feature adds its own to, where each runs (<see cref="Context"/>),
/// and the window's own (files, editing, navigation, building and running, tests, the editor tabs' and the tool
/// windows' frame). However an action is asked for, it runs through the registry, so one that cannot run says why.</summary>
public partial class MainWindow
{
    private const string NoFile = "No file is open";
    private const string NoFolder = "No folder is open";

    private ActionRegistry? actions;

    /// <summary>The window's actions and its keymap.</summary>
    public ActionRegistry Actions => actions!;

    /// <summary>Runs an action by id, as its shortcut would: the IDE's wherever the keys are, an editor's where they are
    /// (the active editor, else the text box with them). False for an id no action has, or one that cannot run here.</summary>
    public bool Perform(string action) => actions!.Run(action) is null;

    /// <summary>Runs an action a person asked for (its keys, a menu item, Find Action, a tool window's button), in the
    /// context made as they asked: one that cannot run here says why in the status bar.</summary>
    private bool Request(string action, ActionContext? context = null)
    {
        if (actions!.Run(action, context ?? actions.Context()) is not { } why)
        {
            return true;
        }
        status.Text = why;
        return false;
    }

    /// <summary>Where an action asked for now runs: the file shown and its caret, what has the keys in the window in front
    /// (an editor window, a diff, a dialog, a floating tool window, this one) and the tool window it is in, and the
    /// debugger's state.</summary>
    private ActionContext Context()
    {
        Window front = Editors.InFront?.Window ?? OwnedWindows.LastOrDefault(w => w.IsActive) ?? this;
        object? focused = front.FocusManager?.GetFocusedElement();
        Docking.ToolWindow? tool = focused is Visual visual
            ? Dock.Windows.FirstOrDefault(w => w.Content == visual || w.Content.IsVisualAncestorOf(visual))
            : null;
        return new ActionContext(Editors.Active, Editors.CodeEditor, focused, tool, session.Debug.IsDebugging ? session.Debug.Session!.State : null);
    }

    private static string? NeedsFile(ActionContext context) => context.File is null ? NoFile : null;

    private string? NeedsFolder(ActionContext _) => Folder is null ? NoFolder : null;

    /// <summary>The scope of the rows chosen in the Explorer while it has the keys, as Rider's Find in Files takes the
    /// Solution view's selection; null elsewhere, for the scope kept.</summary>
    private Workspace.SearchScope? ExplorerScope(ActionContext context) =>
        context.ToolWindow?.Id == "explorer" && Explorer.SelectedNodes is { Count: > 0 } rows ? ScopeOf(rows) : null;

    /// <summary>The window's own actions; each feature's partial or controller adds its own beside them.</summary>
    private void AddActions()
    {
        actions = new ActionRegistry(Context, () => keys.Keymap);
        Dock.Actions = actions;
        Explorer.Actions = actions;
        AddEditing();
        Add(A.Open, "Open...", "IDE", context => _ = PickWorkspace(), menu: ("File", 0, 0));
        actions.Add(new IdeAction(A.RecentFiles, "Recent Files", "IDE", _ => ShowRecentFiles()) { Menus = [new("File", 0, 4), new("View", 2, 0)] });
        Add(A.Save, "Save", "IDE", context => _ = SaveExplicitly(all: false), NeedsFile, ("File", 1, 0));
        Add(A.SaveAll, "Save All", "IDE", context => _ = SaveExplicitly(all: true), _ => Editors.Documents.Any(d => d.IsModified) ? null : "Every file is saved", ("File", 1, 1));
        // Quit is the application menu's on macOS; either closes every window, as Rider's does.
        Add(A.Exit, "Exit", "IDE", _ => PassengerApp.Quit(), menu: OperatingSystem.IsMacOS() ? null : ("File", 4, 0));
        Add(A.FindAction, "Find Action...", "IDE", _ => ShowFindAction(), menu: ("Help", 0, 0));

        // Rider's Unit Tests window takes Find's keys for its search while it has them.
        Add(A.Find, "Find", "IDE", c =>
        {
            if (c.ToolWindow?.Id == "tests")
            {
                Bottom.Tests.FocusSearch();
            }
            else
            {
                Editors.ShowFind(replace: false);
            }
        }, c => c.ToolWindow?.Id == "tests" ? null : NeedsFile(c), ("Edit/Find", 0, 0));
        Add(A.Replace, "Replace", "IDE", _ => Editors.ShowFind(replace: true), NeedsFile, ("Edit/Find", 0, 1));
        Add(A.FindNext, "Find Next", "IDE", _ => Editors.FindBar.Next(), NeedsFile, ("Edit/Find", 0, 2));
        Add(A.FindPrevious, "Find Previous", "IDE", _ => Editors.FindBar.Previous(), NeedsFile, ("Edit/Find", 0, 3));
        Add(A.FindInFiles, "Find in Files...", "IDE", c => ShowFindInFiles(replace: false, ExplorerScope(c)), NeedsFolder, ("Edit/Find", 1, 0));
        Add(A.ReplaceInFiles, "Replace in Files...", "IDE", c => ShowFindInFiles(replace: true, ExplorerScope(c)), NeedsFolder, ("Edit/Find", 1, 1));
        Add(A.ExtendSelection, "Extend Selection", "Editor", _ => ExtendSelection(), NeedsFile, ("Edit", 2, 1));
        Add(A.ShrinkSelection, "Shrink Selection", "Editor", _ => ShrinkSelection(), NeedsFile, ("Edit", 2, 2));

        Add(A.Back, "Back", "Navigate", _ => navigator.StepHistory(-1), menu: ("Navigate", 0, 0));
        Add(A.Forward, "Forward", "Navigate", _ => navigator.StepHistory(1), menu: ("Navigate", 0, 1));
        Add(A.GoToEverything, "Search Everywhere", "Navigate", _ => navigator.ShowGoToEverything(), NeedsFolder, ("Navigate", 1, 0));
        Add(A.GoToFile, "Go to File", "Navigate", _ => navigator.ShowGoToFile(), NeedsFolder, ("Navigate", 1, 1));
        Add(A.GoToSymbol, "Go to Symbol", "Navigate", _ => navigator.ShowGoToSymbol(), NeedsFolder, ("Navigate", 1, 2));
        Add(A.GoToLine, "Go to Line:Column", "Navigate", _ => navigator.ShowGoToLine(), NeedsFile, ("Navigate", 1, 3));
        Add(A.FileStructure, "File Structure", "Navigate", _ => navigator.ShowFileStructure(), NeedsFile, ("Navigate", 1, 4));

        AddBuilding();
        AddHotReload();
        AddAttaching();
        AddTests();

        AddTabActions();

        // The tool windows' frame: run from the terminal too, as the windows' own shortcuts are.
        actions.Add(new IdeAction(A.MaximizeToolWindow, "Maximize Tool Window", "Tool Windows", _ => Dock.ToggleMaximize()) { Menus = [new("View", 3, 0)], FromTerminal = true });
        actions.Add(new IdeAction(A.HideAllToolWindows, "Hide All Tool Windows", "Tool Windows", _ => Dock.ToggleHideAll()) { Menus = [new("View", 3, 1)], FromTerminal = true });
        actions.Add(new IdeAction(A.HideToolWindow, "Hide Active Tool Window", "Tool Windows", _ => HideToolWindow()) { Menus = [new("View", 3, 2)], FromTerminal = true });
        Add(A.FocusEditor, "Focus Editor", "IDE", _ => FocusEditor(), NeedsFile);
        Add(A.ToggleSolutionAnalysis, "Toggle Solution-Wide Analysis", "IDE", _ => ToggleSolutionAnalysis(),
            _ => session.CSharp is null ? "No C# language server runs for this folder" : null, ("Code", 4, 0));
    }

    /// <summary>The editor's actions, run where the keys are: a text box's own editing there, another editor (a diff's)
    /// when it has them, the Explorer's Cut, Copy and Paste of files while its rows have them, else the active editor.</summary>
    private void AddEditing()
    {
        Dictionary<string, MenuPlace[]> menus = new()
        {
            [E.Undo] = [new("Edit", 0, 0)],
            [E.Redo] = [new("Edit", 0, 1)],
            [E.Cut] = [new("Edit", 1, 0)],
            [E.Copy] = [new("Edit", 1, 1)],
            [E.Paste] = [new("Edit", 1, 2)],
            [E.Duplicate] = [new("Edit", 1, 3)],
            [E.DeleteLine] = [new("Edit", 1, 4)],
            [E.SelectAll] = [new("Edit", 2, 0)],
            [E.SelectNextOccurrence] = [new("Edit", 2, 3)],
            [E.SelectAllOccurrences] = [new("Edit", 2, 4)],
            [E.ToggleLineComment] = [new("Edit", 4, 0), new("Code", 2, 0)],
            [E.ToggleBlockComment] = [new("Code", 2, 1)],
            [E.MoveLineUp] = [new("Edit", 4, 1)],
            [E.MoveLineDown] = [new("Edit", 4, 2)],
            // Rider's Folding, in its order: expanding, collapsing, the doc comments, the toggle.
            [E.Unfold] = [new("Code/Folding", 0, 0)],
            [E.UnfoldAll] = [new("Code/Folding", 0, 1)],
            [E.Fold] = [new("Code/Folding", 1, 0)],
            [E.FoldToDefinitions] = [new("Code/Folding", 1, 1)],
            [E.FoldAll] = [new("Code/Folding", 1, 2)],
            [E.UnfoldDocComments] = [new("Code/Folding", 2, 0)],
            [E.FoldDocComments] = [new("Code/Folding", 2, 1)],
            [E.ToggleFold] = [new("Code/Folding", 3, 0)],
        };
        foreach ((string id, string title) in E.All)
        {
            actions!.Add(new IdeAction(id, title, "Editor", context => Edit(id, context))
            {
                InEditor = true,
                WhyNot = context => context.File is null && context.Focused is not TextBox && !ExplorerEdits(id, context) ? NoFile : null,
                Menus = menus.GetValueOrDefault(id, []),
            });
        }
    }

    private void Edit(string action, ActionContext context)
    {
        if (ExplorerEdits(action, context))
        {
            ExplorerEdit(action);
            return;
        }
        switch (context.Focused)
        {
            case TextBox box when Edit(box, action):
                return;
            case Tables.CellGrid table when table.Execute(action):
                return;
            case CodeEditor editor when editor != Editors.CodeEditor:
                editor.TryExecute(action);
                return;
        }
        if (action is E.Undo or E.Redo && UndoCommand(action == E.Redo, Editors.Active))
        {
            return;
        }
        Editors.CodeEditor.TryExecute(action);
    }

    /// <summary>Build, Run and Debug: the solution's or the chosen projects' builds, the run configuration's run, and Edit
    /// Configurations.</summary>
    private void AddBuilding()
    {
        Add(A.Build, "Build Solution", "Run", _ => BuildSolution(), Unbuildable, ("Build", 0, 0));
        Add(A.RebuildSolution, "Rebuild Solution", "Build", _ => Build(BuildKind.Rebuild), Unbuildable, ("Build", 0, 1));
        Add(A.CleanSolution, "Clean Solution", "Build", _ => Build(BuildKind.Clean), Unbuildable, ("Build", 0, 2));
        Add(A.BuildSelected, "Build Selected Projects", "Build", _ => Build(BuildKind.Build, SelectedProjects()), NoneSelected, ("Build", 1, 0));
        Add(A.RebuildSelected, "Rebuild Selected Projects", "Build", _ => Build(BuildKind.Rebuild, SelectedProjects()), NoneSelected, ("Build", 1, 1));
        Add(A.CleanSelected, "Clean Selected Projects", "Build", _ => Build(BuildKind.Clean, SelectedProjects()), NoneSelected, ("Build", 1, 2));

        Add(A.RunProgram, "Run", "Run", _ => RunSelected(), _ => Unconfigured("run"), ("Run", 0, 0));
        Add(A.Debug, "Debug", "Run", _ => DebugSelected(), _ => Unconfigured("debug"), ("Run", 0, 1));
        // Rider's Run menu has it after Run, Debug and Attach to Process, with no key in its Visual Studio keymaps.
        Add(A.EditConfigurations, "Edit Configurations...", "Run", _ => ShowRunConfigurations(), NeedsFolder, ("Run", 0, 5));
        // Rerun runs the last session again, under the debugger when that is how it runs, attached again when it was attached:
        // as Rider's, one still running is stopped first, where Run said it was already running.
        Add(A.Rerun, "Rerun", "Run", c =>
        {
            if (session.Debug.Session is { } debugged && debugged == attachSession)
            {
                actions!.Run(A.ReattachToProcess, c);
            }
            else if (session.Debug.IsDebugging)
            {
                DebugSelected();
            }
            else
            {
                _ = RerunAsync();
            }
        }, _ => session.Debug.Session is { } debugged && debugged == attachSession ? null : Unconfigured("run"), ("Run", 0, 6));
        Add(A.Stop, "Stop", "Run", _ => buildRun.Stop(), _ => buildRun.WhyNotStop(), ("Run", 0, 7));
        Add(A.EditBreakpoint, "Edit Breakpoint...", "Debug", c =>
        {
            int line = c.Caret.Line;
            string? attribute = Debugging.DebugController.AttributeAtCaret(c);
            IReadOnlyList<Debugging.LineBreakpoint> here = session.Debug.Breakpoints.In(c.File!.Path);
            if ((here.FirstOrDefault(b => b.Line == line && b.Attribute == attribute) ?? here.FirstOrDefault(b => b.Line == line)) is not { } editing)
            {
                // Alt+F9 on a line with none sets one to edit, as Rider's Edit Breakpoint does: on an XAML attribute, its own.
                if (attribute is not null)
                {
                    session.Debug.Breakpoints.ToggleAttribute(c.File.Path, line, attribute);
                }
                else
                {
                    session.Debug.Breakpoints.Toggle(c.File.Path, line);
                }
                editing = session.Debug.Breakpoints.In(c.File.Path).First(b => b.Line == line && b.Attribute == attribute);
            }
            ShowBreakpointPopup(editing);
        }, NeedsFile, ("Run", 2, 2));
        Add(A.ViewBreakpoints, "View Breakpoints and Exceptions...", "Debug", _ => ShowBreakpoints(null), menu: ("Run", 2, 3));

        string? Unbuildable(ActionContext _) => Folder is null ? "Nothing to build: no folder is open" : IsBuilding ? "Another build is running" : null;

        string? NoneSelected(ActionContext context) => Unbuildable(context)
            ?? (SelectedProjects().Count == 0 ? "No project is chosen in the Explorer or open in the editor" : null);

        string? Unconfigured(string verb) => Configuration is null ? $"Nothing to {verb}: no executable project" : null;
    }

    /// <summary>The unit tests' actions: in context (the Unit Tests window's chosen rows while it has the keys, else the
    /// test at the caret), all of them, again, and Stop.</summary>
    private void AddTests()
    {
        Add(A.RunTests, "Run Unit Tests", "Unit Tests", c => RunTestsInContext(c, debug: false), NoTestHere, ("Tests", 0, 0));
        Add(A.DebugTests, "Debug Unit Tests", "Unit Tests", c => RunTestsInContext(c, debug: true), NoTestHere, ("Tests", 0, 1));
        Add(A.RunTestsUntilFailure, "Run Unit Tests Until Failure", "Unit Tests", c => RunTestsInContext(c, debug: false, untilFailure: true), NoTestHere, ("Tests", 0, 2));
        Add(A.RunAllTests, "Run All Tests", "Unit Tests", _ => RunTests(() => session.Tests.RunAsync(Folder!)), NeedsFolder, ("Tests", 0, 3));
        Add(A.RerunTests, "Rerun Unit Tests", "Unit Tests", _ => RunTests(() => session.Tests.RerunAsync(Folder!)), NeedsFolder, ("Tests", 1, 0));
        Add(A.RerunFailedTests, "Rerun Failed Tests", "Unit Tests", _ => RunTests(() => session.Tests.RerunFailedAsync(Folder!)), NeedsFolder, ("Tests", 1, 1));
        Add(A.StopTests, "Stop Unit Tests", "Unit Tests", _ => session.Tests.Stop(), NothingRunning, ("Tests", 1, 2));

        string? NoTestHere(ActionContext context) => NeedsFolder(context)
            ?? (ChosenTests(context) is null && ChosenProjects(context) is null && TestAtCaret(context) is null ? "No test is at the caret" : null);

        // Rider's Stop is disabled with no run to end, its toolbar's button and its menu item alike.
        string? NothingRunning(ActionContext context) => NeedsFolder(context) ?? (session.Tests.IsBusy ? null : "No unit tests are running");
    }

    /// <summary>The projects chosen in the Explorer, a solution folder's those under it, while it has the keys.</summary>
    private IReadOnlyList<string>? ChosenProjects(ActionContext context) =>
        context.ToolWindow?.Id == "explorer" && context.Row is IReadOnlyList<Explorer.ExplorerNode> rows
            && rows.SelectMany(Explorer.Tree.ProjectsOf).Distinct().ToList() is { Count: > 0 } projects ? projects : null;

    /// <summary>The Unit Tests window's chosen rows, while it has the keys.</summary>
    private static IReadOnlyList<Testing.TestNode>? ChosenTests(ActionContext context) =>
        context.ToolWindow?.Id == "tests" && context.Row is IReadOnlyList<Testing.TestNode> { Count: > 0 } chosen ? chosen : null;

    private static (string Type, string? Method)? TestAtCaret(ActionContext context) =>
        context.File?.Syntax is { } syntax ? Testing.TestsAtCaret.At(syntax.Current, context.Caret.Line) : null;

    /// <summary>Adds one of the window's actions, listed in one place of the menus if given.</summary>
    private void Add(string id, string title, string group, Action<ActionContext> run, Func<ActionContext, string?>? whyNot = null, (string Path, int Group, int Order)? menu = null,
        IReadOnlyList<string>? words = null) =>
        actions!.Add(new IdeAction(id, title, group, run)
        {
            WhyNot = whyNot ?? (_ => null),
            Menus = menu is { } at ? [new MenuPlace(at.Path, at.Group, at.Order)] : [],
            Words = words ?? [],
        });
}
