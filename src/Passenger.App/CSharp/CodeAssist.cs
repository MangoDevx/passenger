using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A fix Passenger offers itself at a place in a file, beside the language server's (a running app's failed binding's
/// nearest member, a compose file's undefined name): what Show Context Actions lists, a quieter detail, and the text it puts
/// in place of a range.</summary>
public sealed record EditorFix(string Title, string Detail, TextRange Range, string Text)
{
    /// <summary>The fix worked out again on the file's text as it stands when it is chosen, null when that text no longer
    /// has what it fixes; none for a fix made as it was offered.</summary>
    public Func<TextSnapshot, EditorFix?>? Again { get; init; }

    /// <summary>Where the caret goes in <see cref="Text"/> once it is made: its end when none is given.</summary>
    public int? Caret { get; init; }

    /// <summary>A fix made in another file than the one shown (an AppHost's WithReference, for its service's warning), in
    /// place of <see cref="Text"/>'s edit: made as it is chosen, given <see cref="Range"/> as it then stands and an agent's
    /// call's patience, and saying what it came to.</summary>
    public Func<TextRange, Patience?, Task<RefactorOutcome>>? Elsewhere { get; init; }

    /// <summary>A fix that asks for a value before it is made (an AppHost parameter's), in place of <see cref="Text"/>'s
    /// edit: what is asked, whether the answer is a secret (masked as it is typed, never said back), and the fix made from it.</summary>
    public ValueAsked? Asks { get; init; }

    /// <summary>The fix to make on a text now, worked out again where it can be; null when it no longer fits.</summary>
    public EditorFix? On(TextSnapshot text) => Again is null ? this : Again(text);
}

/// <summary>What a fix asks before it is made: the question, whether the answer is a secret, and the fix made from the
/// answer, given an agent's call's patience, saying what it came to (never the answer).</summary>
public sealed record ValueAsked(string Question, bool Secret, Func<string, Patience?, Task<RefactorOutcome>> Make);

/// <summary>What the language server adds to the editor as you work: completion as you type, a card when the pointer
/// rests (the diagnostics there and the symbol's description), signature help after an opening bracket or a comma, and
/// the code commands (quick fixes, rename, go to declaration, implementation, type declaration, base and derived
/// symbols, find usages, reformat, next error). Its popups sit on a layer over the editor, placed at the
/// caret.</summary>
public sealed partial class CodeAssist
{
    private readonly EditorArea editors;
    private Canvas layer;
    private readonly Func<CSharpService?> service;
    private readonly InfoCard hoverCard = new();
    private readonly InfoCard signatureCard = new();
    private readonly CompletionPopup completion = new();
    private readonly InfoCard itemCard = new();
    private readonly Dictionary<CompletionItem, LiveTemplate> templatesOffered = [];
    private readonly Dictionary<CompletionItem, (PostfixTemplate Template, PostfixTarget Target)> postfixOffered = [];
    private CancellationTokenSource? hovering;
    private CancellationTokenSource? completing;
    private CancellationTokenSource? describing;
    private CancellationTokenSource? signing;
    private Completion? open;
    private Control? modal;

    /// <summary>The pause after the pointer leaves a card of a paused program's value (its word, or the card itself) before
    /// the card goes: time to reach the card from the word, on which it stays while the pointer or the keys are on it.</summary>
    private readonly Debounce leaving;

    /// <summary>Takes the card away once the stop its value was read at is gone.</summary>
    private CancellationTokenRegistration hoverGone;

    /// <summary>Where a card of a value was placed: the line's top it hangs from, in the layer, and whether it stands over
    /// the line, so a card that grows as its rows open stays beside the line and inside the editor.</summary>
    private (Point Line, bool Above)? valuesPlace;

    /// <summary>What undoes what the open modal showed in the editor, run as it closes however it closes.</summary>
    private Action? modalClosed;

    public CodeAssist(EditorArea editors, Canvas layer, Func<CSharpService?> service)
    {
        this.editors = editors;
        this.layer = layer;
        this.service = service;
        foreach (Control popup in new Control[] { hoverCard, signatureCard, completion, itemCard })
        {
            popup.IsVisible = false;
            layer.Children.Add(popup);
        }
        leaving = new Debounce(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!HoverHeld)
            {
                HideHover();
            }
            return Task.CompletedTask;
        });
        hoverCard.PointerEntered += (_, _) => leaving.Stop();
        hoverCard.PointerExited += (_, _) => LeaveValues();
        hoverCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false)
            {
                LeaveValues();
            }
        };
        hoverCard.SizeChanged += (_, _) => KeepValuesPlaced();
        hoverCard.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && hoverCard.Values is not null)
            {
                HideHover();
                e.Handled = true;
            }
        };
        // Every editor a split makes is wired the same; what it asks is answered for the active one, which it is.
        editors.ForEachEditor(editor =>
        {
            editor.Hovered += (_, e) =>
            {
                // A value's card in use (its keys, its menu, a value being typed) stays over what else the pointer rests on.
                if (editor == Editor && !ValuesInUse)
                {
                    _ = e.Hint is { } hint ? ShowHintHover(hint, e.Point) : ShowHover(e.Offset, e.Point);
                }
            };
            editor.HoverEnded += (_, _) =>
            {
                if (hoverCard.IsVisible && hoverCard.Values is not null)
                {
                    LeaveValues();
                }
                else
                {
                    HideHover();
                }
            };
            editor.Typed += (_, text) =>
            {
                if (editor == Editor)
                {
                    OnTyped(text);
                }
            };
            editor.SelectionChanged += (_, _) =>
            {
                if (editor == Editor)
                {
                    OnCaretMoved();
                }
            };
            editor.NavigateRequested += (_, offset) =>
            {
                if (editor == Editor)
                {
                    _ = GoToDefinition(offset);
                }
            };
        });
        editors.ActiveChanged += (_, _) =>
        {
            CloseAll();
            FollowLayer();
        };
        completion.List.DoubleTapped += (_, _) => _ = Commit();
        completion.SelectedChanged += (_, _) => _ = DescribeSelected();
        completion.Swatch = Swatch;
    }

    /// <summary>A place to go: a file and a line and column (from zero).</summary>
    public event EventHandler<(string Path, TextPosition Position)>? Navigate;

    /// <summary>Find Usages' answer, for the Find window: what was searched for and where it is used.</summary>
    public event EventHandler<(string Title, IReadOnlyList<LspLocation> Usages)>? UsagesFound;

    /// <summary>Something to say in the status bar ("No usages found").</summary>
    public event EventHandler<string>? Message;

    /// <summary>A refactoring changed files beyond the open ones, or made new ones, which the Explorer and Git then show.</summary>
    public event EventHandler? FilesChanged;

    public bool IsCompletionOpen => completion.IsVisible;

    /// <summary>What the card beside the completion list says about the chosen item while it shows, null otherwise.</summary>
    public string? ItemDocumentation => itemCard.IsVisible ? itemCard.Text : null;

    /// <summary>The completion list's labels, in order, while it shows.</summary>
    public IReadOnlyList<string> CompletionLabels => completion.IsVisible ? [.. completion.Rows.Select(r => r.Item.Label)] : [];

    /// <summary>The completion list's rows, in order, each with the detail it shows on its right, while it shows.</summary>
    public IReadOnlyList<(string Label, string Detail)> CompletionDetails => completion.IsVisible ? [.. completion.Rows.Select(r => (r.Item.Label, r.Detail))] : [];

    /// <summary>What was typed of the name the completion list shows its rows for, while it shows.</summary>
    public string? CompletionNarrowedBy => completion.IsVisible ? filtered?.Typed : null;

    /// <summary>The controls showing the completion list's rows on screen, while it shows.</summary>
    public IReadOnlyList<Control> CompletionRowControls => completion.IsVisible
        ? [.. completion.List.GetRealizedContainers().OfType<ListBoxItem>().Select(row => row.Presenter?.Child).OfType<Control>()]
        : [];

    /// <summary>What the hover card says while it shows, null otherwise.</summary>
    public string? HoverText => hoverCard.IsVisible ? hoverCard.Text : null;

    /// <summary>The rows of a paused program's value the hover card shows, while it shows them.</summary>
    public Debugging.VariablesList? HoverValues => hoverCard.IsVisible ? hoverCard.Values : null;

    /// <summary>The hover card while it shows, its frame the popups' (<c>Border.popup</c>).</summary>
    public Border? HoverCard => hoverCard.IsVisible ? hoverCard : null;

    /// <summary>Whether a card of a value is in use: it has the keys, its menu is open, or a value is being typed in it.</summary>
    private bool ValuesInUse => hoverCard.IsVisible && hoverCard.Values is { } values && (hoverCard.IsKeyboardFocusWithin || values.IsInUse);

    /// <summary>Whether a card of a value stays: in use, or the pointer is on it.</summary>
    private bool HoverHeld => ValuesInUse || (hoverCard.IsVisible && hoverCard.Values is not null && hoverCard.IsPointerOver);

    /// <summary>What Parameter Info's card says, while it shows.</summary>
    public string? SignatureText => signatureCard.IsVisible ? signatureCard.Text : null;

    /// <summary>The name in the rename box while it is open, null otherwise.</summary>
    public string? Renaming => modal is RenameBox box ? box.Text : null;

    /// <summary>The files the open rename box offers to rename with the type, none while it offers none.</summary>
    public IReadOnlyList<string> RenameOffersFiles => modal is RenameBox box ? box.Files : [];

    /// <summary>Rename's options as they were left: whether a type's files and a file's type are renamed with it.</summary>
    public RenameSettings RenameSettings
    {
        get => renameSettings;
        set
        {
            if (value != renameSettings)
            {
                renameSettings = value;
                RenameSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private RenameSettings renameSettings = new();

    /// <summary>An option of Rename's turned on or off, for the window to keep.</summary>
    public event EventHandler? RenameSettingsChanged;

    /// <summary>While the debugger is paused, the value of what the pointer rests on, which the hover card shows
    /// instead of the symbol's description; in a debugged app's .axaml file, a binding's.</summary>
    public Func<int, CancellationToken, Task<DebuggerHover?>>? DebuggerValue { get; set; }

    private CodeEditor Editor => editors.CodeEditor;

    /// <summary>The shown file's share of its language server: C#'s or Razor's, else a web language's.</summary>
    private IServerDocument? Current => editors.Active is { } active ? service()?.For(active) ?? (IServerDocument?)WebOf(active) : null;

    /// <summary>The window's JavaScript, TypeScript, HTML, CSS and JSON, for a web file shown.</summary>
    public Func<Web.WebLanguages?>? Web { get; set; }

    private Web.WebDocument? WebOf(OpenDocument document) => Web?.Invoke()?.For(document);

    /// <summary>The shown file's web language document, null for a file of none.</summary>
    private Web.WebDocument? WebCurrent => editors.Active is { } active ? WebOf(active) : null;

    /// <summary>The shown file's syntax as it stands, the one parse of it everything shares; null for a file not C#.</summary>
    private ParsedText? Syntax => editors.Active?.Syntax?.Current;

    /// <summary>The server's document for the file shown, asked for each time it is used, since a server started again
    /// has a new one: null for a file no server reads, and, said in the status bar for a command
    /// (<paramref name="what"/>), while the server cannot take it.</summary>
    private LspDocument? Ask(string? what = null)
    {
        if (Current is not { } current)
        {
            return null;
        }
        if (current.Lsp is null && what is not null)
        {
            if (current is Web.WebDocument && Web?.Invoke() is { } web && editors.Active is { } shown)
            {
                Message?.Invoke(this, web.Unready(what, shown));
            }
            else if (service() is { } csharp)
            {
                Message?.Invoke(this, csharp.Unready(what));
            }
        }
        return current.Lsp;
    }

    /// <summary>An open file's server document as it is now, for what the person chose from an answer (a fix, a new
    /// name): the server that answered may have been started again while they chose.</summary>
    private LspDocument Now(OpenDocument document) => service()?.For(document)?.Lsp ?? WebOf(document)?.Lsp ?? throw new LanguageServerFailed(
        WebOf(document) is not null ? "its language server is not running"
        : service() is { } csharp ? $"the C# language server is not ready: {csharp.State}" : "no C# language server runs for this folder");

    /// <summary>The folder the open file's places are named from: the C# server's, else the web servers'.</summary>
    private string? Root => service()?.Session?.Folder ?? Web?.Invoke()?.Folder;

    /// <summary>Says in the status bar why a command got nothing from the server.</summary>
    private void Failed(string what, LanguageServerFailed failed) => Message?.Invoke(this, $"{what} failed: {failed.Reason}");

    /// <summary>Keys the open popups take before the editor does: the completion list's Up, Down, Enter,
    /// Tab and Escape, and Escape for a card.</summary>
    public bool TakeKey(string action)
    {
        if (open is not null && action is EditorActions.Up or EditorActions.Down or EditorActions.PageUp or EditorActions.PageDown
            or EditorActions.NewLine or EditorActions.Indent)
        {
            // From the rows of the name as typed, whose filter landing later would have chosen its first row again.
            FilterNow();
        }
        if (completion.IsVisible)
        {
            switch (action)
            {
                case EditorActions.Up:
                    completion.Move(-1);
                    return true;
                case EditorActions.Down:
                    completion.Move(1);
                    return true;
                case EditorActions.PageUp:
                    completion.Move(-9);
                    return true;
                case EditorActions.PageDown:
                    completion.Move(9);
                    return true;
                case EditorActions.NewLine or EditorActions.Indent:
                    _ = Commit();
                    return true;
                case EditorActions.Escape:
                    CloseCompletion();
                    return true;
            }
        }
        if (action == EditorActions.Indent && !Editor.IsInSnippet && TryExpandTemplate())
        {
            return true;
        }
        if (action == EditorActions.Escape && (signatureCard.IsVisible || hoverCard.IsVisible))
        {
            signatureCard.IsVisible = false;
            HideHover();
            return true;
        }
        return false;
    }

    /// <summary>The code actions, on the active editor's caret: those that ask the language server need a C# file of a
    /// folder it runs for; the error steps and Quick Documentation (the problems there, a paused program's values) only a
    /// file.</summary>
    public void AddActions(ActionRegistry actions)
    {
        Code(AppActions.Complete, "Basic Completion", context => _ = StartCompletion(null), new("Code/Code Completion", 0, 0), xaml: true, msbuild: true);
        Code(AppActions.CompleteStatement, "Complete Current Statement", _ => CompleteStatement(), new("Code/Code Completion", 1, 0), server: false, syntax: true);
        // Passenger's own fixes first, which a file the server does not read (a XAML file) may have too.
        actions.Add(new IdeAction(AppActions.QuickFix, "Show Context Actions", "Code", context => _ = ShowFixes())
        {
            WhyNot = context => OwnFixes(context.File, context.Editor.Selections.Primary.Caret).Count > 0 ? null : WhyNot(context, server: true, xaml: true),
            Menus = [new("Code", 0, 1)],
        });
        // Generate: the server's actions that write code for the type (constructors, equality, members, overrides).
        Code(AppActions.Generate, "Generate…", context => _ = ShowFixes(a => GenerateWords.Any(w => a.Title.StartsWith(w, StringComparison.Ordinal)), "Generate"),
            new("Code", 0, 2));
        Code(AppActions.SurroundWith, "Surround With…", _ => ShowSurround(), new("Code", 0, 3), server: false, syntax: true, markup: true);
        // A file Prettier formats (Run on 'Reformat Code' action) needs no language server.
        actions.Add(new IdeAction(AppActions.Reformat, "Reformat Code", "Code", context => _ = Reformat())
        {
            WhyNot = context => PrettierReformats(context.File) ? (context.Editor.IsReadOnly ? "The file is read-only" : null) : WhyNot(context, server: true, markup: true),
            Menus = [new("Code", 1, 0)],
        });
        AddLintingActions(actions);
        Code(AppActions.CodeCleanup, "Code Cleanup", context => _ = Cleanup(), new("Code", 1, 1), markup: true);
        Code(AppActions.OptimizeUsings, "Optimize Imports", context => _ = OptimizeUsings(), new("Code", 1, 2));
        Code(AppActions.RefactorThis, "Refactor This…", context => _ = ShowFixes(a => a.Kind?.StartsWith("refactor", StringComparison.Ordinal) == true, "Refactor This"),
            new("Refactor", 0, 0), xaml: true);
        Code(AppActions.ChangeSignature, "Change Signature…", context => _ = ChangeSignature(), new("Refactor", 1, 1));
        Code(AppActions.ExtractMethod, "Extract Method…", context => _ = Refactor(a => a.Title == "Extract method", "Extract Method", renameMethod: true),
            new("Refactor", 1, 2));
        Code(AppActions.IntroduceVariable, "Introduce Variable…",
            context => _ = Refactor(a => a.Title.StartsWith("Introduce local", StringComparison.Ordinal), "Introduce Variable", renameMethod: false), new("Refactor", 1, 3));
        Code(AppActions.IntroduceField, "Introduce Field…", context => _ = Refactor(IntroduceField), new("Refactor", 1, 4));
        Code(AppActions.IntroduceParameter, "Introduce Parameter…", context => _ = Refactor(IntroduceParameter), new("Refactor", 1, 5));
        Code(AppActions.ExtractInterface, "Extract Interface…", context => _ = ExtractInterface(), new("Refactor", 1, 6));
        Code(AppActions.Inline, "Inline…",
            context => _ = Refactor(a => a.Kind == "refactor.inline" || a.Title.StartsWith("Inline", StringComparison.Ordinal), "Inline", renameMethod: false), new("Refactor", 1, 7));
        Code(AppActions.Move, "Move…", context => _ = Move(), new("Refactor", 2, 0));
        Code(AppActions.SafeDelete, "Safe Delete…", context => _ = SafeDelete(), new("Refactor", 2, 1));
        Code(AppActions.PullMembersUp, "Pull Members Up…", context => _ = MoveMembers(up: true), new("Refactor", 3, 0));
        Code(AppActions.PushMembersDown, "Push Members Down…", context => _ = MoveMembers(up: false), new("Refactor", 3, 1));
        Code(AppActions.EncapsulateField, "Encapsulate Field…", context => _ = Refactor(EncapsulateField), new("Refactor", 3, 2));
        actions.Add(new IdeAction(AppActions.ExtractResource, "Extract Resource…", "Code", context => _ = ExtractResource())
        {
            WhyNot = context => WhyNotXaml(context),
            Menus = [new("Refactor", 4, 0)],
        });
        actions.Add(new IdeAction(AppActions.ExtractStyle, "Extract Style…", "Code", context => _ = ExtractStyle())
        {
            WhyNot = context => WhyNotXaml(context),
            Menus = [new("Refactor", 4, 1)],
        });
        Code(AppActions.GoToDeclaration, "Go to Declaration", context => _ = GoToDefinition(Caret), new("Navigate", 2, 0), xaml: true, msbuild: true, http: true);
        Code(AppActions.DerivedSymbols, "Derived Symbols", context => _ = GoToRelated("Derived Symbols", Caret, DerivedSymbolsAsync, "No derived symbols found"),
            new("Navigate", 2, 1));
        Code(AppActions.GoToImplementation, "Go to Implementation", context => _ = GoToImplementation(Caret), new("Navigate", 2, 2));
        Code(AppActions.GoToTypeDeclaration, "Go to Type Declaration",
            context => _ = GoToRelated("Type Declaration", Caret, async (lsp, at) => [.. (await lsp.TypeDefinitionAsync(at)).Select(l => new Target(l))],
                "Cannot find a type declaration to go to"), new("Navigate", 2, 3));
        Code(AppActions.BaseSymbols, "Base Symbols", context => _ = GoToRelated("Base Symbols", Caret, BaseSymbolsAsync, "No base symbols found"), new("Navigate", 2, 4));
        Code(AppActions.FindUsages, "Find Usages", context => _ = FindUsages(Caret), new("Navigate", 2, 5), xaml: true);
        Code(AppActions.ParameterInfo, "Parameter Info", context => _ = ShowSignature(), new("View", 1, 2));
        Code(AppActions.NextError, "Next Highlighted Error", _ => StepToMarker(forward: true), new("Navigate", 3, 0), server: false);
        Code(AppActions.PreviousError, "Previous Highlighted Error", _ => StepToMarker(forward: false), new("Navigate", 3, 1), server: false);
        Code(AppActions.QuickDocumentation, "Quick Documentation", context => QuickDocumentation(), new("View", 1, 1), server: false);

        void Code(string id, string title, Action<ActionContext> run, MenuPlace menu, bool server = true, bool syntax = false, bool xaml = false, bool markup = false, bool msbuild = false,
            bool http = false) =>
            actions.Add(new IdeAction(id, title, "Code", run)
            {
                // An .http file's requests go to their routes, with no language server of its own.
                WhyNot = context => http && IsHttpFile(context.File) ? null : WhyNot(context, server, syntax, xaml, markup, msbuild),
                Menus = [menu],
            });
    }

    private int Caret => Editor.Selections.Primary.Caret;

    /// <summary>Rename at the editor's caret: a C# symbol through the server, a XAML name and a compose file's of its own.</summary>
    public void Rename() => ShowRename();

    /// <summary>Why Rename cannot run at the editor's caret; null when it can.</summary>
    public string? WhyNotRename(ActionContext context) => WhyNot(context, server: true, xaml: true);

    /// <summary>Quick Documentation: the card for what is at the caret (its problems, a paused program's value, the symbol's
    /// description); pressed again while a value's card shows, the card takes the keys, its rows to open with the arrows.</summary>
    private void QuickDocumentation()
    {
        if (hoverCard.IsVisible && hoverCard.Values is { } values)
        {
            values.FocusRow();
            return;
        }
        _ = ShowHover(Caret, Editor.PointOf(Caret));
    }

    /// <summary>Why an action of a XAML file's alone cannot run: no file, a file not XAML of a project Passenger reads, or
    /// one that cannot be edited.</summary>
    private string? WhyNotXaml(ActionContext context) =>
        context.File is null ? Refusals.NoFile
        : XamlOf(context.File) is not { } markup || (markup.Loaded.IsCompleted && !markup.IsLoaded) ? "The file is not XAML of an Avalonia or WPF project"
        : context.Editor.IsReadOnly ? "The file is read-only"
        : null;

    /// <summary>Why an action cannot run: no file; for one that asks the server, none for the file; for one that edits by
    /// the file's syntax, a file not C# or one that cannot be edited. One that also edits XAML (<paramref name="markup"/>)
    /// takes any XAML file that can be edited; XAML and MSBuild files answer those they say they do.</summary>
    private string? WhyNot(ActionContext context, bool server, bool syntax = false, bool xaml = false, bool markup = false, bool msbuild = false) =>
        context.File is null ? Refusals.NoFile
        : markup && XamlService.IsXaml(context.File.Path) ? (context.Editor.IsReadOnly ? "The file is read-only" : null)
        : xaml && XamlOf(context.File) is { } markupFile && (markupFile.IsLoaded || !markupFile.Loaded.IsCompleted) ? null
        : msbuild && MSBuildOf(context.File) is not null ? null
        : syntax && context.File.Syntax is null ? "The file is not C#"
        : syntax && context.Editor.IsReadOnly ? "The file is read-only"
        : !server || Current is not null ? null
        : service() is null ? Refusals.NoCSharp
        : "The file is not C#";

    private static readonly string[] GenerateWords = ["Generate", "Implement", "Override", "Add parameter", "Add null check", "Create and assign"];

    /// <summary>What the open list of choices (fixes, refactorings, definitions) offers, by its words; empty with none open.</summary>
    public IReadOnlyList<string> Offered => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Titles ?? [];

    /// <summary>What the list of choices shown says beside each of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> OfferedDetails => layer.Children.OfType<ChoicePopup>().LastOrDefault()?.Details ?? [];

    private void OnTyped(string text)
    {
        HideHover();
        if (completion.IsVisible)
        {
            if (text.All(IsWordChar))
            {
                Filter();
                if (open is { IsIncomplete: true })
                {
                    // The server listed only some of what fits: what is typed now narrows it to the rest.
                    _ = StartCompletion(null, incomplete: true);
                }
                return;
            }
            if (CommitsOn(text) is { } end)
            {
                _ = CommitOn(text, end);
                return;
            }
            CloseCompletion();
        }
        AfterTyped(text);
    }

    /// <summary>Where the name typed so far ends, when what was just typed is one of the chosen item's commit
    /// characters (the server's: a dot, a bracket, a semicolon), so the item goes in before it; null otherwise. Space
    /// is not one, and nothing commits before a letter of the name is typed.</summary>
    private int? CommitsOn(string text)
    {
        if (open is null || text.Length != 1 || text == " ")
        {
            return null;
        }
        int end = Editor.Selections.Primary.Caret - 1;
        if (end <= open.Start || end > Editor.Document.Snapshot.Length || Editor.Document.Snapshot[end] != text[0])
        {
            return null;
        }
        // The item chosen among the rows of the name as typed before the character, its filter landed or not.
        FilterNow(end);
        if (open is null || completion.Selected is not { } row || templatesOffered.ContainsKey(row.Item) || postfixOffered.ContainsKey(row.Item))
        {
            return null;
        }
        return (row.Item.CommitCharacters ?? open.CommitCharacters).Contains(text) ? end : null;
    }

    /// <summary>The chosen item goes in before the commit character typed, and the character then does what it does
    /// after any name: a dot lists members, a bracket shows the signature.</summary>
    private async Task CommitOn(string text, int end)
    {
        BackgroundWork.Begin();
        try
        {
            await CommitCore(end);
            AfterTyped(text);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private void AfterTyped(string text)
    {
        char last = text[^1];
        if (MSBuildCurrent is not null)
        {
            if (MSBuildTrigger(text) || (IsIdentifierChar(last) && !char.IsDigit(last) && WordStart(Editor.Selections.Primary.Caret) == Editor.Selections.Primary.Caret - 1))
            {
                _ = StartCompletion(null);
            }
            return;
        }
        if (XamlCurrent is not null)
        {
            if (XamlTrigger(text) || (IsIdentifierChar(last) && !char.IsDigit(last) && WordStart(Editor.Selections.Primary.Caret) == Editor.Selections.Primary.Caret - 1))
            {
                _ = StartCompletion(null);
            }
            return;
        }
        if (WebCurrent is { } web)
        {
            AfterTypedInWeb(web, text);
            return;
        }
        if (last == '.' || (InRazor && last is '<' or '@'))
        {
            // In a Razor file a tag's '<' lists the elements and components, and '@' Razor's directives and the C#.
            _ = StartCompletion(last.ToString());
        }
        else if (IsIdentifierChar(last) && !char.IsDigit(last) && WordStart(Editor.Selections.Primary.Caret) == Editor.Selections.Primary.Caret - 1)
        {
            _ = StartCompletion(null);
        }
        if (last is '(' or ',')
        {
            _ = ShowSignature();
        }
        else if (last == ')' || last == ';')
        {
            signing?.Cancel();
            signatureCard.IsVisible = false;
        }
    }

    /// <summary>Filters once the move's input is done: a typed character moves the caret before the editor says what was
    /// typed, and a commit character must find the list still open.</summary>
    private void OnCaretMoved()
    {
        if (completion.IsVisible)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Filter);
        }
    }

    private async Task StartCompletion(string? trigger, bool incomplete = false)
    {
        BackgroundWork.Begin();
        try
        {
            await StartCompletionCore(trigger, incomplete);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task StartCompletionCore(string? trigger, bool incomplete)
    {
        if (MSBuildCurrent is { } msbuild)
        {
            await StartMSBuildCompletion(msbuild);
            return;
        }
        if (XamlCurrent is { } xaml)
        {
            await StartXamlCompletion(xaml);
            return;
        }
        if (Current?.Lsp is not { } lsp)
        {
            // A settings file's keys are Passenger's own, which need no JSON server (none without Node.js).
            if (WebCurrent is { Kind: WebServerKind.Json } json)
            {
                await StartSettingsCompletion(json);
            }
            return;
        }
        xamlOffered.Clear();
        completing?.Cancel();
        CancellationTokenSource cancel = new();
        completing = cancel;
        try
        {
            // An AppHost's or a service's resource names where one is typed; any other file's list is the server's alone.
            if (await StartAspireCompletion(lsp, cancel.Token))
            {
                return;
            }
            int caret = Editor.Selections.Primary.Caret;
            TextSnapshot asked = Editor.Document.Snapshot;
            Web.WebDocument? web = WebCurrent;
            // A compose file's names, or a settings file's keys and values, merged by label ahead of the server's.
            (int Start, IReadOnlyList<CompletionItem> Items)? named = web is not null ? await ComposeCompletionAsync(web, lsp, asked, caret) : null;
            (int Start, IReadOnlyList<CompletionItem> Items, string Tail)? settings = named is null && web is not null
                ? await SettingsCompletionAsync(web, asked, caret, cancel.Token) : null;
            if (settings is { } configured)
            {
                named = (configured.Start, configured.Items);
            }
            // A localizer's string offers its resource's keys, ahead of the server's.
            (int Start, IReadOnlyList<CompletionItem> Items, string Tail)? localized = web is null ? await LocalizedCompletionAsync(lsp, asked, caret, cancel.Token) : null;
            if (localized is { } keys)
            {
                named = (keys.Start, keys.Items);
            }
            (CompletionList? list, _) = await lsp.CompletionAsync(caret, trigger, cancel.Token, incomplete);
            IReadOnlyList<CompletionItem> served = list?.Items ?? [];
            if (named is { } own)
            {
                HashSet<string> labels = [.. own.Items.Select(i => i.Label)];
                // A server's item that would replace another stretch than the name's could not go in where the name does.
                served = [.. served.Where(i => !labels.Contains(i.Label) && (i.TextEdit is not { } replaced || lsp.OffsetOf(replaced.Range.Start) == own.Start))];
            }
            if (cancel.IsCancellationRequested || (served.Count == 0 && named is null))
            {
                if (!cancel.IsCancellationRequested)
                {
                    CloseCompletion();
                }
                return;
            }
            int start = named?.Start ?? (served.FirstOrDefault(i => i.TextEdit is not null)?.TextEdit is { } edit
                ? lsp.OffsetOf(edit.Range.Start)
                : lsp.IsRazor ? RazorWordStart(caret, served) : web is not null ? WebWordStart(web, caret) : WordStart(caret));
            // The live templates that may go here are listed with the server's items, ahead of a keyword they share.
            TemplateScope scope = Syntax is { } syntax ? TemplateScopes.At(syntax, caret).Scope : TemplateScope.None;
            templatesOffered.Clear();
            foreach (LiveTemplate template in web is null ? Templates.In(scope) : [])
            {
                templatesOffered[new CompletionItem { Label = template.Abbreviation, Kind = 15, Detail = template.Description, SortText = "!" + template.Abbreviation }] = template;
            }
            postfixOffered.Clear();
            foreach ((PostfixTemplate postfix, PostfixTarget target) in Templates.PostfixInCompletion ? Postfixes(Math.Min(start, caret), caret) : [])
            {
                postfixOffered[new CompletionItem { Label = postfix.Name, Kind = 15, Detail = postfix.Example, SortText = "~" + postfix.Name }] = (postfix, target);
            }
            open = new Completion([.. named?.Items ?? [], .. served, .. templatesOffered.Keys, .. postfixOffered.Keys], Math.Min(start, Editor.Selections.Primary.Caret),
                list?.ItemDefaults?.CommitCharacters ?? [], list?.IsIncomplete ?? false)
            {
                Tail = settings is { } settled ? settled.Tail : localized is { } read ? read.Tail : web is not null ? Tail(asked, served, caret) : string.Empty,
                Lead = web is not null && start < caret && caret <= asked.Length ? asked.GetText(start, caret - start) : string.Empty,
            };
            Filter();
        }
        catch (OperationCanceledException)
        {
            // Typed on: the list is asked for again.
        }
        catch (LanguageServerFailed)
        {
            // No list, as with nothing to offer; typing on asks again.
        }
    }

    /// <summary>What a list is narrowed by: the text typed since it opened, what of it an item with no filter text of its
    /// own is matched by, and how alike two scores must be to keep the list's own order.</summary>
    private readonly record struct Narrowing(Completion List, string Typed, string Bare, int Alike);

    /// <summary>How many filters were asked for, the last the only one shown when it lands; and what the rows shown were
    /// filtered by.</summary>
    private int filters;
    private (Completion List, string Typed)? filtered;

    /// <summary>What the open list is narrowed by with the name typed up to <paramref name="end"/> (the caret when not
    /// given); null, the list closed, once the caret has left the name or what is typed is no name.</summary>
    private Narrowing? NarrowedBy(int? end = null)
    {
        if (open is null)
        {
            return null;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = end ?? Editor.Selections.Primary.Caret;
        if (caret < open.Start || Editor.Selections.HasMultiple)
        {
            CloseCompletion();
            return null;
        }
        string typed = text.GetText(open.Start, caret - open.Start);
        // A web server's items may replace what came before the name (TypeScript's the member's dot), there as it asked.
        string lead = open.Lead;
        int skip = 0;
        while (skip < lead.Length && !IsWordChar(lead[skip]))
        {
            skip++;
        }
        bool led = skip > 0 && typed.StartsWith(lead[..skip], StringComparison.Ordinal) && typed[skip..].All(IsWordChar);
        if (!led && !typed.All(IsWordChar))
        {
            CloseCompletion();
            return null;
        }
        // An MSBuild list keeps its own order among names that match alike (the common properties first), not the shortest.
        int alike = msbuildOffered.Count > 0 ? 1000 : 1;
        return new Narrowing(open, typed, led ? typed[skip..] : typed, alike);
    }

    /// <summary>The list narrowed to what has been typed since it opened: prefix matches first (an exact-case one first of
    /// those), then CamelHumps, then anywhere in the name. Scored and made into rows on the thread pool, since a
    /// statement's list is every type in reach, and shown as it lands unless a later key's filter was asked for since.</summary>
    private void Filter()
    {
        if (NarrowedBy() is not { } narrowing)
        {
            return;
        }
        _ = FilterAsync(narrowing, ++filters);
    }

    private async Task FilterAsync(Narrowing narrowing, int asked)
    {
        CompletionRow[] rows = await BackgroundWork.Run(() => Rows(narrowing));
        if (asked == filters && open == narrowing.List)
        {
            ShowRows(narrowing, rows);
        }
    }

    /// <summary>The list shown for the name as typed up to <paramref name="end"/>, filtered here should its filter not have
    /// landed yet: what commits or moves the choice must find the rows of the name as typed.</summary>
    private void FilterNow(int? end = null)
    {
        if (NarrowedBy(end) is not { } narrowing || filtered == (narrowing.List, narrowing.Typed))
        {
            return;
        }
        filters++;
        ShowRows(narrowing, Rows(narrowing));
    }

    private void ShowRows(Narrowing narrowing, CompletionRow[] rows)
    {
        if (rows.Length == 0)
        {
            CloseCompletion();
            return;
        }
        bool opening = !completion.IsVisible;
        completion.Show(rows);
        filtered = (narrowing.List, narrowing.Typed);
        completion.IsVisible = true;
        Place(completion, narrowing.List.Start, above: false);
        if (opening && msbuildOffered.Count > 0)
        {
            // An MSBuild list's first item's documentation beside it as it opens, which its selection came too early to show.
            _ = DescribeSelected();
        }
    }

    /// <summary>The rows a list shows narrowed (<see cref="Shown"/>), each badged by its kind; reads nothing of the window's,
    /// so it runs off the UI thread.</summary>
    private static CompletionRow[] Rows(Narrowing narrowing)
    {
        CompletionItem[] shown = Shown(narrowing.List.Items, narrowing.Typed, narrowing.Bare, narrowing.Alike);
        CompletionRow[] rows = new CompletionRow[shown.Length];
        for (int i = 0; i < shown.Length; i++)
        {
            rows[i] = Row(shown[i]);
        }
        return rows;
    }

    /// <summary>The most rows the list shows.</summary>
    private const int MostShown = 200;

    /// <summary>The items that match what was typed, the best first (the higher score over <paramref name="alike"/>,
    /// then the one the server preselects, then by its sort text): only the best <see cref="MostShown"/> kept as the
    /// items are scored, since a statement's list is every type in reach. An item with no filter text of its own is
    /// matched by <paramref name="bare"/>, what was typed less a web server's lead.</summary>
    private static CompletionItem[] Shown(IReadOnlyList<CompletionItem> items, string typed, string bare, int alike)
    {
        PriorityQueue<CompletionItem, (int Score, CompletionItem Item, int Index)> best = new(MostShown + 1, WorstFirst.Instance);
        for (int i = 0; i < items.Count; i++)
        {
            CompletionItem item = items[i];
            if (FuzzyMatch.Label(item.FilterText is null ? bare : typed, item.FilterText ?? item.Label) is not { } score)
            {
                continue;
            }
            (int, CompletionItem, int) ranked = (score / alike, item, i);
            if (best.Count < MostShown)
            {
                best.Enqueue(item, ranked);
            }
            else if (best.TryPeek(out _, out (int Score, CompletionItem Item, int Index) worst) && WorstFirst.Instance.Compare(ranked, worst) > 0)
            {
                best.DequeueEnqueue(item, ranked);
            }
        }
        CompletionItem[] shown = new CompletionItem[best.Count];
        for (int i = shown.Length - 1; i >= 0; i--)
        {
            shown[i] = best.Dequeue();
        }
        return shown;
    }

    /// <summary>Matches in the order they go from the list's best: a lower score, then not preselected, then later by sort
    /// text, then later in the server's list.</summary>
    private sealed class WorstFirst : IComparer<(int Score, CompletionItem Item, int Index)>
    {
        public static readonly WorstFirst Instance = new();

        public int Compare((int Score, CompletionItem Item, int Index) x, (int Score, CompletionItem Item, int Index) y)
        {
            int byScore = x.Score.CompareTo(y.Score);
            if (byScore != 0)
            {
                return byScore;
            }
            int byPreselect = (x.Item.Preselect == true).CompareTo(y.Item.Preselect == true);
            if (byPreselect != 0)
            {
                return byPreselect;
            }
            int bySort = string.CompareOrdinal(y.Item.SortText ?? y.Item.Label, x.Item.SortText ?? x.Item.Label);
            return bySort != 0 ? bySort : y.Index.CompareTo(x.Index);
        }
    }

    private async Task Commit()
    {
        BackgroundWork.Begin();
        try
        {
            await CommitCore();
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>The chosen item in place of the name typed, up to <paramref name="end"/> (the caret when not given).</summary>
    private async Task CommitCore(int? end = null)
    {
        FilterNow(end);
        if (open is null || !completion.IsVisible || completion.Selected is not { } row)
        {
            return;
        }
        if (xamlOffered.TryGetValue(row.Item, out XamlItem? xamlItem))
        {
            int from = open.Start;
            CloseCompletion();
            CommitXaml(xamlItem, from);
            return;
        }
        if (msbuildOffered.TryGetValue(row.Item, out MSBuildItem? msbuildItem))
        {
            int from = open.Start;
            CloseCompletion();
            CommitMSBuild(msbuildItem, from);
            return;
        }
        LspDocument? lsp = Current?.Lsp;
        if (lsp is null && !open.Settings)
        {
            return;
        }
        int start = open.Start;
        string tail = open.Tail;
        CloseCompletion();
        CompletionItem item = row.Item;
        if (templatesOffered.TryGetValue(item, out LiveTemplate? chosen))
        {
            Expand(chosen, start, Editor.Selections.Primary.Caret);
            return;
        }
        if (postfixOffered.TryGetValue(item, out (PostfixTemplate Template, PostfixTarget Target) postfix))
        {
            ExpandPostfix(postfix.Template, postfix.Target, Editor.Selections.Primary.Caret);
            return;
        }
        TextDocument document = Editor.Document;
        IndentOptions indent = Editor.Indent;
        using ChangeTracker asked = new(document);
        int typed = end ?? Editor.Selections.Primary.Caret;
        try
        {
            if (item.Data is not null && item.AdditionalTextEdits is null && lsp is not null)
            {
                item = await lsp.ResolveAsync(item) ?? item;
            }
        }
        catch (LanguageServerFailed)
        {
            // The item goes in as listed, without what resolving would have added (a using it needs).
        }
        // The editor shows another file now: the item was for the one it showed.
        if (Editor.Document != document)
        {
            return;
        }
        if (ClientCommands.CompletionEdit(item) is { } complex)
        {
            CommitWhole(asked, complex, start, typed, indent);
            return;
        }
        start = asked.MapOffset(start);
        int caret = Math.Min(asked.MapOffset(typed), Editor.Document.Snapshot.Length);
        // What the item replaces after the caret, still there (a JSON string's closing quote): it goes with the name typed.
        if (tail.Length > 0 && caret + tail.Length <= Editor.Document.Snapshot.Length && Editor.Document.Snapshot.GetText(caret, tail.Length) == tail)
        {
            caret += tail.Length;
        }
        string insert = item.TextEdit?.NewText ?? item.TextEditText ?? item.InsertText ?? item.Label;
        TextRange final = new(insert.Length, 0);
        if (item.InsertTextFormat == 2)
        {
            // A snippet (Razor's attribute="$0", a component with its required parameters): its places are Tab's to visit.
            Snippet snippet = Snippet.FromProtocol(insert);
            if (item.AdditionalTextEdits is not { Count: > 0 })
            {
                Editor.InsertSnippet(TextRange.FromBounds(start, Math.Max(start, caret)), snippet);
                return;
            }
            insert = snippet.Text;
            final = snippet.End;
        }
        int replaced = Math.Max(start, caret);
        List<Passenger.Text.TextEdit> edits = [new Passenger.Text.TextEdit(start, replaced - start, insert)];
        if (item.AdditionalTextEdits is { Count: > 0 } extra && WorkspaceEdits.Carried(asked, extra, indent) is { } placed)
        {
            edits.AddRange(placed.Where(e => e.End <= start || e.Start >= caret));
        }
        string eol = document.LineEnding;
        List<Passenger.Text.TextEdit> fitted = Indentation.Fit(document.Snapshot, LineEndings.Fit(document.Snapshot, edits, eol), indent);
        int after = ItemEnd(fitted, start, replaced);
        Selection landing = new(after - LineEndings.In(insert[final.Start..], eol).Length, after - LineEndings.In(insert[final.End..], eol).Length);
        int at = Editor.Selections.Primary.Caret;
        // Said, since an item put in with nothing typed is an insertion at the caret, which stays before it; a commit character's caret is past it.
        Editor.ApplyEdits(fitted, at >= start && at <= replaced ? SelectionSet.Single(landing) : null);
        if (item.Command is { } command)
        {
            Message?.Invoke(this, NotRun(item.Label, command));
        }
    }

    /// <summary>Where the item put in place of <paramref name="start"/> to <paramref name="end"/> ends once the fitted
    /// edits are made: the end of the edit holding it (fitting may widen it or join an edit touching it), moved on by the
    /// edits before it (a using added above), or the item's own end when it changed nothing.</summary>
    private static int ItemEnd(IReadOnlyList<Passenger.Text.TextEdit> fitted, int start, int end)
    {
        int shift = 0;
        foreach (Passenger.Text.TextEdit edit in fitted)
        {
            if (edit.Start <= start && edit.End >= end)
            {
                return edit.Start + shift + edit.Text.Length;
            }
            if (edit.Start >= end)
            {
                break;
            }
            shift += edit.Text.Length - edit.Length;
        }
        return end + shift;
    }

    /// <summary>A completion whose code the server writes whole (an override's body, the async an await needs in its
    /// method): its edit in place of the name typed from <paramref name="start"/> to <paramref name="typed"/>, answered for
    /// the text <paramref name="asked"/> began with, and the caret where the server puts it.</summary>
    private void CommitWhole(ChangeTracker asked, ComplexEdit complex, int start, int typed, IndentOptions indent)
    {
        ServerLines lines = ServerLines.Of(asked.Snapshot);
        int from = lines.OffsetOf(complex.Edit.Range.Start);
        int to = lines.OffsetOf(complex.Edit.Range.End);
        string written = complex.Edit.NewText;
        string eol = asked.Document.LineEnding;
        // Roslyn's edit covers the name as it stood when resolved; one that meets the name takes all of what was typed.
        (int first, int last) = from <= typed && to >= start ? (Math.Min(from, start), Math.Max(to, typed)) : (from, to);
        if (LineEndings.Fit(asked.Snapshot, [new Passenger.Text.TextEdit(first, last - first, written)], eol) is not [Passenger.Text.TextEdit broken])
        {
            return;
        }
        // The server's caret is an offset in the text its edit leaves, which starts as this edit's does, or after a '\r' it took in.
        int into = complex.Caret is { } caret && caret - from >= 0 && caret - from <= written.Length
            ? Math.Min(LineEndings.In(asked.Snapshot.GetText(broken.Start, first - broken.Start) + written[..(caret - from)], eol).Length, broken.Text.Length)
            : broken.Text.Length;
        if (Indentation.Fit(asked.Snapshot, broken, indent, ref into) is not { } made)
        {
            return;
        }
        if (asked.Map([made]) is not [Passenger.Text.TextEdit placed])
        {
            Message?.Invoke(this, "The completion was not inserted: the code it changes was edited meanwhile");
            return;
        }
        Editor.ApplyEdits([placed], SelectionSet.Single(placed.Start + into));
    }

    private void CloseCompletion()
    {
        completing?.Cancel();
        describing?.Cancel();
        open = null;
        xamlOffered.Clear();
        xamlAsked = null;
        msbuildOffered.Clear();
        completion.IsVisible = false;
        itemCard.IsVisible = false;
    }

    /// <summary>The documentation beside the completion list: the chosen item's signature and summary, resolved once
    /// the choice rests a moment.</summary>
    private async Task DescribeSelected()
    {
        describing?.Cancel();
        itemCard.IsVisible = false;
        if (completion.IsVisible && completion.Selected is { } chosen && msbuildOffered.TryGetValue(chosen.Item, out MSBuildItem? described))
        {
            DescribeMSBuild(described);
            return;
        }
        if (!completion.IsVisible || completion.Selected is not { } row || Current?.Lsp is not { } lsp)
        {
            return;
        }
        CancellationTokenSource cancel = new();
        describing = cancel;
        BackgroundWork.Begin();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancel.Token);
            CompletionItem item = row.Item;
            if (item.Data is not null && item.Documentation is null)
            {
                item = await lsp.ResolveAsync(item, cancel.Token) ?? item;
            }
            if (cancel.IsCancellationRequested || !completion.IsVisible || completion.Selected != row)
            {
                return;
            }
            itemCard.Clear();
            if (item.Detail is { Length: > 0 } detail)
            {
                itemCard.AddCode(detail);
            }
            if (Shapes.Text(item.Documentation) is { Length: > 0 } documentation)
            {
                itemCard.AddMarkdown(documentation);
            }
            if (itemCard.IsEmpty)
            {
                return;
            }
            itemCard.IsVisible = true;
            PlaceBeside(itemCard, completion);
        }
        catch (OperationCanceledException)
        {
            // Another item chosen, or the list closed.
        }
        catch (LanguageServerFailed)
        {
            // No card beside the list.
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>A card to the right of a popup, at its top; to its left when the right has no room.</summary>
    private void PlaceBeside(Control card, Control beside)
    {
        card.Measure(Size.Infinity);
        double left = Canvas.GetLeft(beside);
        double width = card.DesiredSize.Width;
        // A popup just shown has no bounds until the next layout: its measure says how wide it will be.
        if (beside.Bounds.Width == 0)
        {
            beside.Measure(Size.Infinity);
        }
        double x = left + (beside.Bounds.Width > 0 ? beside.Bounds.Width : beside.DesiredSize.Width) + 4;
        if (x + width > layer.Bounds.Width && left - width - 4 >= 0)
        {
            x = left - width - 4;
        }
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, Canvas.GetTop(beside));
    }

    private async Task ShowHover(int offset, Point point)
    {
        BackgroundWork.Begin();
        try
        {
            await ShowHoverCore(offset, point);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task ShowHoverCore(int offset, Point point)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        foreach (Marker marker in Editor.MarkersAt(offset))
        {
            hoverCard.AddText(marker.Message, marker.Kind switch
            {
                MarkerKind.Error => InfoCard.Brush("Passenger.Error"),
                MarkerKind.Warning => InfoCard.Brush("Passenger.Warning"),
                _ => null,
            });
        }
        DebuggerHover? debuggerValue = DebuggerValue is { } value ? await value(offset, cancel.Token) : null;
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        Debugging.VariablesList? values = null;
        if (debuggerValue is not null)
        {
            if (debuggerValue.Gone.IsCancellationRequested)
            {
                hoverCard.IsVisible = false;
                return;
            }
            if (debuggerValue.Problem is { } problem)
            {
                hoverCard.AddText(problem, InfoCard.Brush("Passenger.Error"));
            }
            if (debuggerValue.Values is { } rows)
            {
                values = rows;
                // The menu closed, the card goes unless the pointer or the keys are still on it.
                rows.MenuClosed += (_, _) =>
                {
                    if (hoverCard.Values == rows)
                    {
                        LeaveValues();
                    }
                };
            }
            else if (debuggerValue.Code.Length > 0)
            {
                hoverCard.AddCode(debuggerValue.Code);
            }
        }
        else if (XamlCurrent is { } xaml)
        {
            if (await XamlDescription(xaml, offset) is { } described && !cancel.IsCancellationRequested)
            {
                hoverCard.AddCode(described);
            }
            if (await XamlLocalizationHoverAsync(xaml, offset) is { } languages && !cancel.IsCancellationRequested)
            {
                hoverCard.AddText(languages);
            }
        }
        else if (MSBuildCurrent is { } msbuild)
        {
            await MSBuildHover(msbuild, offset, cancel.Token);
        }
        else if (Current?.Lsp is { } lsp)
        {
            try
            {
                Hover? hover = await lsp.HoverAsync(offset, cancel.Token);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }
                if (hover is not null && Shapes.Text(hover.Contents) is { Length: > 0 } markdown)
                {
                    hoverCard.AddMarkdown(markdown);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (LanguageServerFailed)
            {
                // The card says what the markers there say, without the symbol's description.
            }
        }
        if (debuggerValue is null && XamlCurrent is null && MSBuildCurrent is null && await ConfigurationHoverAsync(offset, cancel.Token) is { } configured)
        {
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            hoverCard.AddText(configured);
        }
        if (debuggerValue is null && XamlCurrent is null && MSBuildCurrent is null && await LocalizationHoverAsync(offset, cancel.Token) is { } localized)
        {
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            hoverCard.AddText(localized);
        }
        if ((hoverCard.IsEmpty && values is null) || completion.IsVisible)
        {
            hoverCard.IsVisible = false;
            return;
        }
        hoverCard.IsVisible = true;
        TextSnapshot text = Editor.Document.Snapshot;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(Math.Min(offset, text.Length)));
        Point anchor = Editor.PointOf(lineStart);
        if (values is not null)
        {
            PlaceValues(values, new Point(point.X, anchor.Y));
        }
        else
        {
            Place(hoverCard, new Point(point.X, anchor.Y), above: false);
        }
        if (debuggerValue is not null)
        {
            // Read at a stop, the value goes with it: the program going on, another program's tab chosen, the folder left.
            hoverGone = debuggerValue.Gone.Register(HideHover);
        }
    }

    /// <summary>Puts a card of a value's rows under the line at <paramref name="at"/> (its top), or over it when the room
    /// below holds fewer rows than the room above, its rows scrolling past what that room holds.</summary>
    private void PlaceValues(Debugging.VariablesList values, Point at)
    {
        at = Editor.TranslatePoint(at, layer) ?? at;
        double lineHeight = Editor.LineHeight;
        double below = layer.Bounds.Height - (at.Y + lineHeight + 2) - 8;
        double over = at.Y - 2 - 8;
        bool above = below < ListRow.Height * 8 && over > below;
        hoverCard.AddValues(values, above ? over : below);
        valuesPlace = (at, above);
        hoverCard.Measure(Size.Infinity);
        Size size = hoverCard.DesiredSize;
        Canvas.SetLeft(hoverCard, Math.Clamp(at.X - 4, 0, Math.Max(0, layer.Bounds.Width - size.Width - 8)));
        Canvas.SetTop(hoverCard, above ? Math.Max(0, at.Y - size.Height - 2) : at.Y + lineHeight + 2);
    }

    /// <summary>A card of a value that grew as its rows opened stays inside the editor, and over its line when it stands over it.</summary>
    private void KeepValuesPlaced()
    {
        if (valuesPlace is not { } place || !hoverCard.IsVisible || hoverCard.Values is null)
        {
            return;
        }
        Size size = hoverCard.Bounds.Size;
        double x = Math.Clamp(Canvas.GetLeft(hoverCard), 0, Math.Max(0, layer.Bounds.Width - size.Width - 8));
        if (x != Canvas.GetLeft(hoverCard))
        {
            Canvas.SetLeft(hoverCard, x);
        }
        double y = Math.Max(0, place.Line.Y - size.Height - 2);
        if (place.Above && y != Canvas.GetTop(hoverCard))
        {
            Canvas.SetTop(hoverCard, y);
        }
    }

    /// <summary>The pointer left a card of a value, or its word, or the keys left the card: it goes after a pause unless
    /// the pointer comes back to it or it is in use.</summary>
    private void LeaveValues()
    {
        if (hoverCard.IsVisible && hoverCard.Values is not null)
        {
            leaving.Restart();
        }
    }

    /// <summary>Lets go of the value the card showed: its pause before going, its stop, its place.</summary>
    private void ForgetValues()
    {
        leaving.Stop();
        hoverGone.Dispose();
        hoverGone = default;
        valuesPlace = null;
    }

    /// <summary>The card for an inlay hint the pointer rests on: what the server says of it (a type's description, a
    /// parameter's), or none when it says nothing.</summary>
    private async Task ShowHintHover(InlayHint hint, Point point)
    {
        hovering?.Cancel();
        CancellationTokenSource cancel = new();
        hovering = cancel;
        ForgetValues();
        hoverCard.Clear();
        if (hint.Data is not LspInlayHint server || Current?.Lsp is not { } lsp)
        {
            hoverCard.IsVisible = false;
            return;
        }
        BackgroundWork.Begin();
        try
        {
            string tooltip = await lsp.InlayHintTooltipAsync(server, cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            if (tooltip.Length == 0 || completion.IsVisible)
            {
                hoverCard.IsVisible = false;
                return;
            }
            hoverCard.AddMarkdown(tooltip);
            hoverCard.IsVisible = true;
            TextSnapshot text = Editor.Document.Snapshot;
            int lineStart = text.GetLineStart(text.GetLineFromOffset(Math.Min(hint.Offset, text.Length)));
            Place(hoverCard, new Point(point.X, Editor.PointOf(lineStart).Y), above: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (LanguageServerFailed)
        {
            // A server that no longer has the hint (the file changed since) shows no card for it.
            hoverCard.IsVisible = false;
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private void HideHover()
    {
        hovering?.Cancel();
        bool focused = hoverCard.IsKeyboardFocusWithin;
        ForgetValues();
        hoverCard.IsVisible = false;
        if (hoverCard.Values is not null)
        {
            hoverCard.Clear();
        }
        if (focused)
        {
            Editor.Focus();
        }
    }

    private async Task ShowSignature()
    {
        BackgroundWork.Begin();
        try
        {
            await ShowSignatureCore();
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task ShowSignatureCore()
    {
        if (Current?.Lsp is not { } lsp)
        {
            return;
        }
        int caret = Editor.Selections.Primary.Caret;
        TextDocument document = Editor.Document;
        signing?.Cancel();
        CancellationTokenSource cancel = new();
        signing = cancel;
        try
        {
            SignatureHelp? help = await lsp.SignatureHelpAsync(caret, cancel.Token);
            // A late answer is for a call the caret has left, or a file no longer shown.
            if (cancel.IsCancellationRequested || Editor.Document != document)
            {
                return;
            }
            if (help is null || help.Signatures.Count == 0)
            {
                signatureCard.IsVisible = false;
                return;
            }
            SignatureInformation signature = help.Signatures[Math.Clamp(help.ActiveSignature ?? 0, 0, help.Signatures.Count - 1)];
            signatureCard.Clear();
            signatureCard.AddCode(signature.Label);
            int active = signature.ActiveParameter ?? help.ActiveParameter ?? -1;
            List<string> names = [.. ParameterNames(signature)];
            if (signature.Parameters is { } parameters && active >= 0 && active < parameters.Count
                && Shapes.Text(parameters[active].Documentation) is { Length: > 0 } about)
            {
                signatureCard.AddText(SentencesApart(about, names));
            }
            else if (Shapes.Text(signature.Documentation) is { Length: > 0 } documentation)
            {
                signatureCard.AddText(SentencesApart(documentation, names));
            }
            signatureCard.IsVisible = true;
            Place(signatureCard, caret, above: true);
        }
        catch (OperationCanceledException)
        {
            // The caret left the call, or asked again.
        }
        catch (LanguageServerFailed)
        {
            // No signature shown; the next bracket or comma asks again.
        }
    }

    /// <summary>Documentation with the space back where its XML put a reference to one of the signature's parameters
    /// straight after a sentence's full stop, which the server's plain text runs into the word before: .NET's
    /// String.Join(String, IEnumerable) says "a separator.&lt;paramref name="separator"/&gt; is included", which read
    /// "a separator.separator is included".</summary>
    private static string SentencesApart(string text, IEnumerable<string> parameters) =>
        parameters.Distinct(StringComparer.Ordinal).Aggregate(text, (said, name) => System.Text.RegularExpressions.Regex.Replace(
            said, $@"(?<=\p{{Ll}}\.){System.Text.RegularExpressions.Regex.Escape(name)}\b", " " + name, System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    /// <summary>A signature's parameters' names, whether the server labels each by its name (Roslyn), as "name: type"
    /// (TypeScript) or by where it is in the signature's label.</summary>
    private static IEnumerable<string> ParameterNames(SignatureInformation signature)
    {
        foreach (ParameterInformation parameter in signature.Parameters ?? [])
        {
            System.Text.Json.JsonElement label = parameter.Label;
            string text = label.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => label.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Array when label.GetArrayLength() == 2 && label[0].TryGetInt32(out int start) && label[1].TryGetInt32(out int end)
                    && 0 <= start && start <= end && end <= signature.Label.Length => signature.Label[start..end],
                _ => string.Empty,
            };
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            // TypeScript's "name?: type" names it before the colon, C#'s "type name" last.
            System.Text.RegularExpressions.Match name = System.Text.RegularExpressions.Regex.Match(colon >= 0 ? text[..colon] : text,
                @"[\p{L}_][\p{L}\p{Nd}_]*", System.Text.RegularExpressions.RegexOptions.RightToLeft);
            if (name.Success)
            {
                yield return name.Value;
            }
        }
    }

    private async Task ShowFixes(Func<CodeAction, bool>? only = null, string title = "Fixes and refactorings")
    {
        BackgroundWork.Begin();
        try
        {
            await ShowFixesCore(only, title);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>The fixes Passenger offers itself at an offset of a file, which Show Context Actions lists before asking the
    /// language server; none by default.</summary>
    public Func<OpenDocument, int, IReadOnlyList<EditorFix>>? EditorFixes { get; set; }

    /// <summary>Asks the person for a value a fix sets (a title, what is asked, whether it is a secret, masked as typed);
    /// null when they gave none.</summary>
    public Func<string, string, bool, Task<string?>>? AskValue { get; set; }

    internal IReadOnlyList<EditorFix> OwnFixes(OpenDocument? file, int offset) => file is not null && EditorFixes?.Invoke(file, offset) is { } fixes ? fixes : [];

    /// <summary>One of Passenger's own fixes made in the file shown, as one undoable edit: worked out again on its text as it
    /// stands (<see cref="EditorFix.On"/>), and refused, said in the status bar, when the text no longer has what it fixes;
    /// one made in another file (<see cref="EditorFix.Elsewhere"/>) is that file's command, said as it lands.</summary>
    private async Task ApplyOwnFix(OpenDocument document, EditorFix fix)
    {
        if (editors.Active != document)
        {
            return;
        }
        if (fix.On(document.Text.Snapshot) is not { } now)
        {
            Message?.Invoke(this, $"{fix.Title} was not applied: the file changed, so it no longer fits");
            return;
        }
        if (now.Elsewhere is { } elsewhere)
        {
            RefactorOutcome outcome = await elsewhere(now.Range, null);
            Message?.Invoke(this, outcome.Problem ?? outcome.Said);
            return;
        }
        if (now.Asks is { } asks)
        {
            // Not awaited: the question holds no background work, so a settle waits on the IDE, never on the person.
            _ = AskAndMakeAsync(now.Title.TrimEnd('…'), asks);
            return;
        }
        Editor.ApplyEdits([new TextEdit(now.Range.Start, now.Range.Length, now.Text)], SelectionSet.Single(now.Range.Start + (now.Caret ?? now.Text.Length)));
    }

    /// <summary>A fix's value asked of the person (<see cref="AskValue"/>), then the fix made from it as background work,
    /// what it came to said; nothing when they give none.</summary>
    private async Task AskAndMakeAsync(string title, ValueAsked asks)
    {
        try
        {
            if (AskValue is null || await AskValue(title, asks.Question, asks.Secret) is not { } value)
            {
                return;
            }
            BackgroundWork.Begin();
            try
            {
                RefactorOutcome made = await asks.Make(value, null);
                Message?.Invoke(this, made.Problem ?? made.Said);
            }
            finally
            {
                BackgroundWork.End();
            }
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
    }

    private async Task ShowFixesCore(Func<CodeAction, bool>? only, string title)
    {
        if (XamlCurrent is { } xaml)
        {
            await XamlFixes(xaml, only is null ? OwnFixes(editors.Active, Caret) : [], refactor: only is not null);
            return;
        }
        string what = only is null ? "Show Context Actions" : title;
        // Passenger's own fixes first, which need no server (a compose file's are its container engine's).
        IReadOnlyList<EditorFix> own = only is null ? OwnFixes(editors.Active, Caret) : [];
        if (editors.Active is not { } document)
        {
            return;
        }
        Selection primary = Editor.Selections.Primary;
        IReadOnlyList<EslintFix> linted = [];
        IReadOnlyList<CodeAction> actions = [];
        if (Ask(own.Count > 0 ? null : what) is { } lsp && Current is { } current)
        {
            int line = lsp.PositionOf(primary.Caret).Line;
            List<Diagnostic> here = current.Diagnostics
                .Where(d => d.Range.Start.Line <= line && line <= d.Range.End.Line)
                .ToList();
            try
            {
                // ESLint's fixes for the line first, before the language's own.
                linted = only is null ? await LintFixes(document, primary.Caret) : [];
                actions = [.. (await lsp.CodeActionsAsync(primary.Range, here)).Where(a => a.Disabled is null && (only?.Invoke(a) ?? true))];
            }
            catch (LanguageServerFailed failed)
            {
                if (own.Count == 0)
                {
                    Failed(what, failed);
                    return;
                }
            }
        }
        else if (own.Count == 0)
        {
            return;
        }
        if (own.Count == 0 && actions.Count == 0 && linted.Count == 0)
        {
            Message?.Invoke(this, only is null ? "No fixes or refactorings here" : "No refactorings here");
            return;
        }
        List<Choice> choices = [.. own.Select(f => new Choice(f.Title, f.Detail, f)), .. linted.Select(f => new Choice(f.Title, CodeActionKinds.Shown(f.Action.Kind), f)),
            .. actions.Select(a => new Choice(a.Title, CodeActionKinds.Shown(a.Kind), a))];
        ShowModal(new ChoicePopup(title, choices), primary.Caret, what, choice =>
        {
            switch (choice?.Value)
            {
                case EditorFix fix:
                    return ApplyOwnFix(document, fix);
                case EslintFix fix:
                    return RunLintFix(document, fix);
                case CodeAction chosen:
                    return Carry(document, chosen, primary.Caret, chosen.Title, null);
                default:
                    return Task.CompletedTask;
            }
        });
    }

    /// <summary>A code action chosen, carried out: one that offers choices (a fix-all's scopes, the actions nested under
    /// it) offers them as a list at <paramref name="at"/> and applies the one chosen there, or the one choice
    /// <paramref name="keep"/> leaves; any other is applied at once. <paramref name="applied"/> runs once its change is made.</summary>
    private async Task Carry(OpenDocument document, CodeAction action, int at, string what, Asked? listed, Action? applied = null, Func<ActionChoice, bool>? keep = null)
    {
        IReadOnlyList<ActionChoice> offered = ClientCommands.Choices(action);
        IReadOnlyList<ActionChoice> choices = keep is null ? offered : [.. offered.Where(keep)];
        if (offered.Count > 0 && choices.Count == 0)
        {
            Message?.Invoke(this, $"{what} is not available here: the language server offers none of its kind");
            return;
        }
        if (choices.Count == 0 || keep is not null && choices.Count == 1)
        {
            (CodeAction chosen, string? scope) = choices.Count == 1 ? (choices[0].Action, choices[0].Scope) : (action, null);
            if (await Apply(document, chosen, scope, what, choices.Count == 1 ? null : listed))
            {
                applied?.Invoke();
            }
            return;
        }
        ShowModal(new ChoicePopup(action.Title, [.. choices.Select(c => new Choice(c.Text, string.Empty, c))]), at, what, async choice =>
        {
            if (choice?.Value is ActionChoice chosen && await Apply(document, chosen.Action, chosen.Scope, what, null))
            {
                applied?.Invoke();
            }
        });
    }

    /// <summary>A code action's change made: resolved first when the server left it for later (a fix-all's over the scope
    /// chosen), by the server running now, its edit applied, and a command it still carries, which Passenger cannot run,
    /// said in the status bar, as is a server that cannot resolve it. <paramref name="listed"/> follows the files from
    /// when the actions were asked for. Whether every file took it.</summary>
    private async Task<bool> Apply(OpenDocument document, CodeAction action, string? scope, string what, Asked? listed)
    {
        (RefactorOutcome made, bool applied) = await ApplyAsync(document, action, scope, what, listed);
        if (made.Problem is not null)
        {
            Message?.Invoke(this, made.Problem);
            return false;
        }
        if (made.Left.Count > 0)
        {
            Message?.Invoke(this, $"{what} was not applied to {string.Join(", ", made.Left)}: it was edited meanwhile");
        }
        if (made.Said.Length > 0)
        {
            Message?.Invoke(this, made.Said);
        }
        return applied;
    }

    /// <summary>A code action's change made, as <see cref="Apply"/> makes it, for the status bar or an agent: why not, else
    /// the files it changed, those left as edited meanwhile, and what to say beyond its being done (a command it carries
    /// that Passenger cannot run, or that it changed nothing); and whether every file took it. An agent's call's refusal for
    /// files the server had yet to read is told its <paramref name="patience"/>.</summary>
    public async Task<(RefactorOutcome Outcome, bool Applied)> ApplyAsync(OpenDocument document, CodeAction action, string? scope, string what, Asked? listed, Patience? patience = null)
    {
        bool resolve = (action.Edit is null && action.Data is not null) || scope is not null;
        // An answer resolved now is for the text as it is now, and carried through what is typed while it comes.
        using Asked? resolving = resolve ? WorkspaceEdits.Track(editors) : null;
        CodeAction chosen;
        try
        {
            chosen = resolve ? await Now(document).ResolveChosenAsync(action, scope) : action;
        }
        catch (LanguageServerFailed failed)
        {
            return (RefactorOutcome.Refused($"{what} was not applied: {failed.Reason}"), false);
        }
        Asked? asked = resolving ?? listed;
        if (chosen.Edit is { } changes && await StaleAsync(document, changes, asked, patience) is { } stale)
        {
            return (RefactorOutcome.Refused($"{what} was not applied: {stale}"), false);
        }
        RefactorOutcome made = chosen.Edit is { } edit ? ApplyEdit(edit, asked, what) : new RefactorOutcome(string.Empty, null, [], []);
        if (made.Problem is not null)
        {
            return (made, false);
        }
        string said = chosen.Command is { } command ? NotRun(what, command) : chosen.Edit is null ? $"{what} changed nothing" : string.Empty;
        return (made with { Said = said }, chosen.Edit is not null && made.Left.Count == 0);
    }

    /// <summary>Why a server's edit must not be applied to the files not open it changes (<see cref="WorkspaceEdits.StaleAsync"/>):
    /// the C# server is asked what it holds of them; another server's files are judged by when they changed alone.</summary>
    private Task<string?> StaleAsync(OpenDocument document, System.Text.Json.JsonElement edit, Asked? asked, Patience? patience = null) =>
        WorkspaceEdits.StaleAsync(editors, service()?.For(document)?.Lsp, edit, asked?.Since, patience);

    /// <summary>What the status bar says of a command left unrun: Roslyn's own are run, and it registers no other.</summary>
    private static string NotRun(string what, LspCommand command) => $"{what} needs {command.Command}, which Passenger cannot run";

    /// <summary>A refactoring by its own key, over the selection: the language server's action of that kind applied at
    /// once, and for Extract Method its new method's name then offered to rename.</summary>
    private async Task Refactor(Func<CodeAction, bool> pick, string name, bool renameMethod)
    {
        if (Ask(name) is not { } lsp || editors.Active is not { } document)
        {
            return;
        }
        BackgroundWork.Begin();
        try
        {
            Selection primary = Editor.Selections.Primary;
            using Asked asked = WorkspaceEdits.Track(editors);
            using ExtractedMethod? made = renameMethod && document.Syntax is { } syntax ? new ExtractedMethod(Editor.Document, syntax, primary) : null;
            CodeAction? action = (await lsp.CodeActionsAsync(primary.Range, [])).Where(a => a.Disabled is null).FirstOrDefault(pick);
            if (action is null)
            {
                Message?.Invoke(this, $"{name} is not available here{(primary.IsEmpty && renameMethod ? ": select the code first" : string.Empty)}");
                return;
            }
            // Choices the server nests under the action (Introduce Variable's occurrences) are offered as a list first.
            await Carry(document, action, primary.Caret, name, asked, () =>
            {
                Message?.Invoke(this, $"{name} done");
                // The call that replaced the selection is where the new name is typed, in the file still shown.
                if (made is not null && made.Document == Editor.Document && made.Call() is { } call)
                {
                    Editor.Selections = SelectionSet.Single(call);
                    ShowRename();
                }
            });
        }
        catch (LanguageServerFailed failed)
        {
            Failed(name, failed);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private void ShowRename()
    {
        if (XamlCurrent is { } xaml && editors.Active is not null)
        {
            _ = Carried("Rename", () => XamlRename(xaml), closeModal: false);
            return;
        }
        // A compose file's own name: where it is declared and every use, its override's too; else the server's (an anchor's).
        if (WebCurrent is { Lsp: not null } yaml && editors.Active is { } compose && IsComposeFile(yaml, compose))
        {
            int caret = Editor.Selections.Primary.Caret;
            _ = Carried("Rename", async () =>
            {
                if (!await ShowComposeRename(yaml, compose, caret) && editors.Active == compose)
                {
                    ShowServerRename();
                }
            }, closeModal: false);
            return;
        }
        if (WebCurrent is { Lsp: not null } own && !own.Provides("renameProvider") && editors.Active is { } json)
        {
            ShowWebRename(own, json);
            return;
        }
        // An Aspire resource's name in a string: the AppHost's declaration and its services' reads, else the server's rename.
        if (AspireRoleOfShown != Aspire.AspireRole.None && editors.Active is { } aspire && MayNameResource(Editor.Selections.Primary.Caret))
        {
            int caret = Editor.Selections.Primary.Caret;
            _ = Carried("Rename", async () =>
            {
                if (!await ShowAspireRename(aspire, caret) && editors.Active == aspire)
                {
                    ShowServerRename();
                }
            }, closeModal: false);
            return;
        }
        ShowServerRename();
    }

    /// <summary>Rename through the language server at the editor's caret, a type's files offered with it.</summary>
    private void ShowServerRename()
    {
        if (Ask("Rename") is null || editors.Active is not { } document)
        {
            return;
        }
        TextSnapshot text = Editor.Document.Snapshot;
        int caret = Editor.Selections.Primary.Caret;
        // A web language's names run on through its own characters: CSS's --accent, JavaScript's $el.
        TextRange word = WebCurrent is { } web ? WebWordAt(web, caret) : TextNavigation.WordAt(text, caret);
        if (word.Length == 0 || !(TextNavigation.IsWordChar(text[word.Start]) || (WebCurrent is { } named && IsWebWordChar(named, text[word.Start]))))
        {
            return;
        }
        string old = text.GetText(word);
        RenameBox box = new(old);
        box.RenamesFilesChanged += (_, on) => RenameSettings = RenameSettings with { RenameFiles = on };
        ChangeTracker opened = new(Editor.Document);
        ShowModal(box, word.Start, async name =>
        {
            // Let go as the box closed, just before: what was typed while it was open is still mapped.
            int at = opened.MapOffset(word.Start);
            if (name is null)
            {
                return;
            }
            try
            {
                using Asked asked = WorkspaceEdits.Track(editors);
                // Asked of the server running now: the one there as the box opened may have been started again since.
                if (await Now(document).RenameAsync(at, name) is { } edit)
                {
                    if (await StaleAsync(document, edit, asked) is { } stale)
                    {
                        Message?.Invoke(this, $"Rename was not applied: {stale}");
                        return;
                    }
                    List<string> dropped = [];
                    List<(string From, string To)> moves = box.RenamesFiles ? [.. box.Files.Select(f => (f, TypeRename.Renamed(f, name)))] : [];
                    int files = TypeRename.Apply(editors, edit, moves, $"Rename {old} to {name}", asked, dropped);
                    string renamedFiles = moves.Count > 0 ? $"; {string.Join(", ", moves.Select(m => $"{Path.GetFileName(m.From)} to {Path.GetFileName(m.To)}"))}" : string.Empty;
                    Message?.Invoke(this, dropped.Count > 0 ? $"Renamed, but not in {string.Join(", ", dropped)}: it was edited meanwhile"
                        : (files > 1 ? $"Renamed in {files} files" : "Renamed") + renamedFiles);
                }
                else
                {
                    Message?.Invoke(this, "This can't be renamed");
                }
            }
            catch (LanguageServerFailed failed)
            {
                Failed("Rename", failed);
            }
            catch (WorkspaceEditFailed failed)
            {
                Message?.Invoke(this, $"Rename failed: {failed.Message}");
            }
        });
        modalClosed = opened.Dispose;
        _ = OfferFileRenames(box, document, word.Start);
    }

    /// <summary>Also rename files to reflect this change, offered in the open rename box once the server says the name
    /// is a type's declared in files named after it.</summary>
    private async Task OfferFileRenames(RenameBox box, OpenDocument document, int offset)
    {
        BackgroundWork.Begin();
        try
        {
            if (WebOf(document) is null && service()?.Session?.Folder is { } folder && await TypeRename.NamedAsync(editors, folder, Now(document), offset) is { Files.Count: > 0 } named
                && modal == box)
            {
                box.OfferFiles(named.Files, RenameSettings.RenameFiles);
                Place(box, offset, above: true);
            }
        }
        catch (LanguageServerFailed)
        {
            // The rename asks the same server, and says why it could not.
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task GoToDefinition(int offset)
    {
        BackgroundWork.Begin();
        try
        {
            await GoToDefinitionCore(offset);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task GoToDefinitionCore(int offset)
    {
        if (HttpGoTo(offset))
        {
            return;
        }
        if (MSBuildCurrent is { } msbuild)
        {
            await MSBuildGoTo(msbuild, offset);
            return;
        }
        if (XamlCurrent is { } xaml)
        {
            await XamlGoTo(xaml, offset);
            return;
        }
        if (WebCurrent is { Lsp: not null } compose && editors.Active is { } shown && IsComposeFile(compose, shown) && await ComposeGoTo(compose, offset))
        {
            return;
        }
        if (WebCurrent is { Kind: WebServerKind.Json } && editors.Active is { } settings && ConfiguredBy?.Invoke(settings.Path) is { Count: > 0 } configured
            && await SettingsGoTo(settings, configured, offset))
        {
            return;
        }
        if (WebCurrent is { Lsp: not null } web && !web.Provides("definitionProvider"))
        {
            await WebGoTo(web, offset);
            return;
        }
        if (Ask("Go to Declaration") is not { } lsp)
        {
            return;
        }
        try
        {
            if (await AspireGoTo(lsp, offset) || await ConfigurationGoTo(lsp, offset) || await LocalizationGoTo(lsp, offset))
            {
                return;
            }
            IReadOnlyList<LspLocation> found = await lsp.DefinitionAsync(offset);
            // A FROM's image that names no stage of the file: its registry's page.
            if (found.Count == 0 && WebCurrent is { Kind: Passenger.App.Web.WebServerKind.Dockerfile } && FromImageAt(Editor.Document.Snapshot, offset) is { } page)
            {
                Browse?.Invoke(this, page);
                return;
            }
            if (await NamedElementOfAsync(found, offset) is { } element)
            {
                await Arrive(element);
                return;
            }
            Go(found, offset, "Declarations", Refusals.NoDeclaration);
        }
        catch (LanguageServerFailed failed)
        {
            Failed("Go to Declaration", failed);
        }
    }

    private async Task GoToImplementation(int offset)
    {
        BackgroundWork.Begin();
        try
        {
            await GoToImplementationCore(offset);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task GoToImplementationCore(int offset)
    {
        if (Ask("Go to Implementation") is not { } lsp)
        {
            return;
        }
        try
        {
            Go(await lsp.ImplementationAsync(offset), offset, "Implementations", "No implementations found");
        }
        catch (LanguageServerFailed failed)
        {
            Failed("Go to Implementation", failed);
        }
    }

    /// <summary>Goes to what the server finds from the symbol at an offset (its type's declaration, its base or derived
    /// symbols): straight there when it is one place, else the places to choose from; the status bar says when none.</summary>
    private async Task GoToRelated(string what, int offset, Func<LspDocument, int, Task<IReadOnlyList<Target>>> find, string none)
    {
        BackgroundWork.Begin();
        try
        {
            if (Ask(what) is { } lsp)
            {
                TextDocument asked = Editor.Document;
                string name = NameAt(offset);
                IReadOnlyList<Target> found = await find(lsp, offset);
                // The editor shows another file now: the places were for the one it showed.
                if (Editor.Document == asked)
                {
                    Go([.. found.Select(t => t.Location)], offset, name.Length > 0 ? $"{what} of {name}" : what, none, [.. found.Select(t => t.Name)]);
                }
            }
        }
        catch (LanguageServerFailed failed)
        {
            Failed(what, failed);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>Base Symbols: a type's base types and interfaces, or the members a member overrides or implements, the
    /// nearest first, through every level of the hierarchy that has source.</summary>
    private async Task<IReadOnlyList<Target>> BaseSymbolsAsync(LspDocument lsp, int offset)
    {
        string name = NameAt(offset);
        // An override's own keyword takes the server to the member it overrides, a library's (decompiled) too.
        List<LspLocation> found = Syntax is { } syntax && Declarations.OverrideKeywordAt(syntax, offset) is { } keyword ? [.. await lsp.DefinitionAsync(keyword)] : [];
        if (await lsp.PrepareTypeHierarchyAsync(offset) is not { } type)
        {
            return [.. found.Select(l => new Target(l))];
        }
        IReadOnlyList<HierarchyItem> bases = await Walk(type, (server, item) => server.SupertypesAsync(item));
        if (IsType(type, name))
        {
            return [.. bases.Select(TargetOf)];
        }
        // A member: those of the base types by its name that it overrides or implements, which its references take in.
        if (bases.Count == 0 || name.Length == 0 || service()?.Session is not { } session)
        {
            return [.. found.Select(l => new Target(l))];
        }
        IReadOnlyList<LspLocation> family = await lsp.ReferencesAsync(offset);
        IReadOnlyList<SymbolInformation> symbols = [.. (await session.SymbolsAsync(name)).Where(s => Bare(s.Name) == name)];
        Dictionary<string, string> paths = DocumentUris.ToPaths([.. family.Select(f => f.Uri), .. symbols.Select(s => s.Location.Uri), .. bases.Select(b => b.Uri), .. found.Select(f => f.Uri)]);
        IReadOnlyList<SymbolInformation> named = [.. symbols.Where(s => family.Any(f => SamePlace(paths, f, s.Location)))];
        foreach (LspLocation place in bases.SelectMany(b => named.Where(s => Inside(paths, s.Location, b)).Select(s => s.Location)))
        {
            if (!found.Any(f => SamePlace(paths, f, place)))
            {
                found.Add(place);
            }
        }
        return [.. found.Select(l => new Target(l))];
    }

    /// <summary>Derived Symbols: the types that derive from a type or implement an interface, through every level, or
    /// the members that override or implement a member.</summary>
    private async Task<IReadOnlyList<Target>> DerivedSymbolsAsync(LspDocument lsp, int offset)
    {
        if (await lsp.PrepareTypeHierarchyAsync(offset) is { } type && IsType(type, NameAt(offset)))
        {
            return [.. (await Walk(type, (server, item) => server.SubtypesAsync(item))).Select(TargetOf)];
        }
        // The server's implementations of a member with a body hold the member itself, which is not derived from it.
        IReadOnlyList<LspLocation> own = await lsp.DefinitionAsync(offset);
        IReadOnlyList<LspLocation> found = await lsp.ImplementationAsync(offset);
        Dictionary<string, string> paths = DocumentUris.ToPaths([.. own.Select(o => o.Uri), .. found.Select(l => l.Uri)]);
        return [.. found.Where(l => !own.Any(o => SamePlace(paths, o, l)))
            .OrderBy(l => paths[l.Uri], StringComparer.Ordinal).ThenBy(l => l.Range.Start.Line).ThenBy(l => l.Range.Start.Character)
            .Select(l => new Target(l))];
    }

    /// <summary>A type hierarchy walked one way from a type, nearest first, each type once, as far as the server has
    /// source for: at most <see cref="MostTypes"/>, so an interface the whole solution implements stays quick.</summary>
    private async Task<IReadOnlyList<HierarchyItem>> Walk(HierarchyItem from, Func<LanguageServer, HierarchyItem, Task<IReadOnlyList<HierarchyItem>>> next)
    {
        List<HierarchyItem> found = [];
        HashSet<(string, LspPosition)> seen = [(DocumentUris.ToPath(from.Uri), from.SelectionRange.Start)];
        Queue<HierarchyItem> waiting = new([from]);
        while (waiting.Count > 0 && found.Count < MostTypes)
        {
            // Asked of the server running now at each level: the one that answered the last may have been started again.
            LanguageServer server = service()?.Session?.Server ?? throw new LanguageServerFailed("the C# language server is not running");
            foreach (HierarchyItem type in (await next(server, waiting.Dequeue())).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (found.Count < MostTypes && seen.Add((DocumentUris.ToPath(type.Uri), type.SelectionRange.Start)))
                {
                    found.Add(type);
                    waiting.Enqueue(type);
                }
            }
        }
        return found;
    }

    private const int MostTypes = 200;

    /// <summary>Whether the type hierarchy's type is the symbol named at the caret rather than the type its member is in.</summary>
    private static bool IsType(HierarchyItem type, string name) => name.Length > 0 && Bare(type.Name) == name;

    /// <summary>A name without its type parameters or parameters, as the caret's word is.</summary>
    private static string Bare(string name) => name.Split('<', '(')[0].Trim();

    /// <summary>A place to go, and the name of what is there when the list to choose from should say it (a type's).</summary>
    private sealed record Target(LspLocation Location, string? Name = null);

    private static Target TargetOf(HierarchyItem item) => new(new LspLocation(item.Uri, item.SelectionRange), item.Name);

    /// <summary>Whether two places are one, by their files' paths made once for the list (<see cref="DocumentUris.ToPaths"/>).</summary>
    private static bool SamePlace(Dictionary<string, string> paths, LspLocation one, LspLocation other) =>
        one.Range.Start == other.Range.Start && paths[one.Uri] == paths[other.Uri];

    /// <summary>Whether a place is in a type's declaration.</summary>
    private static bool Inside(Dictionary<string, string> paths, LspLocation place, HierarchyItem type) =>
        paths[place.Uri] == paths[type.Uri] && !Before(place.Range.Start, type.Range.Start) && !Before(type.Range.End, place.Range.Start);

    private static bool Before(LspPosition one, LspPosition other) => one.Line < other.Line || (one.Line == other.Line && one.Character < other.Character);

    /// <summary>The name at an offset, or empty where there is none.</summary>
    private string NameAt(int offset)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        TextRange word = TextNavigation.WordAt(text, Math.Min(offset, text.Length));
        return word.Length > 0 && TextNavigation.IsWordChar(text[word.Start]) ? text.GetText(word) : string.Empty;
    }

    private async Task FindUsages(int offset)
    {
        BackgroundWork.Begin();
        try
        {
            await FindUsagesCore(offset);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task FindUsagesCore(int offset)
    {
        if (RouteUsages(offset))
        {
            return;
        }
        if (XamlCurrent is { } xaml)
        {
            await XamlUsages(xaml, offset);
            return;
        }
        if (WebCurrent is { Lsp: not null } web && !web.Provides("referencesProvider"))
        {
            Message?.Invoke(this, $"Find Usages finds nothing in {editors.Active?.Name}: its language server has no usages to find");
            return;
        }
        if (Ask("Find Usages") is not { } lsp)
        {
            return;
        }
        try
        {
            IReadOnlyList<LspLocation> usages = await lsp.ReferencesAsync(offset);
            if (usages.Count == 0)
            {
                Message?.Invoke(this, "No usages found");
                return;
            }
            int start = WordStart(offset);
            int end = offset;
            while (end < Editor.Document.Snapshot.Length && IsIdentifierChar(Editor.Document.Snapshot[end]))
            {
                end++;
            }
            string name = Editor.Document.Snapshot.GetText(start, end - start);
            UsagesFound?.Invoke(this, ($"Usages of {(name.Length > 0 ? name : "symbol")}", usages));
        }
        catch (LanguageServerFailed failed)
        {
            Failed("Find Usages", failed);
        }
    }

    private void Go(IReadOnlyList<LspLocation> locations, int offset, string title, string none, IReadOnlyList<string?>? names = null)
    {
        if (locations.Count == 0)
        {
            Message?.Invoke(this, none);
        }
        else if (locations.Count == 1)
        {
            Arrive(locations[0]);
        }
        else
        {
            Pick(title, locations, offset, names);
        }
    }

    /// <summary>The places to choose from: a type by its name with its file and line beside it, any other place by its file
    /// and line with its line of code beside it.</summary>
    private void Pick(string title, IReadOnlyList<LspLocation> locations, int offset, IReadOnlyList<string?>? names = null)
    {
        string root = Root ?? string.Empty;
        Dictionary<string, string> paths = DocumentUris.ToPaths(locations.Select(l => l.Uri));
        Func<string, LspPosition, (int Line, string Code)> preview = LinePreviews();
        List<Choice> choices = locations.Select((l, i) =>
        {
            string path = paths[l.Uri];
            (int line, string code) = preview(path, l.Range.Start);
            string shown = $"{PathNames.Shown(path, root)}:{line + 1}";
            return names?[i] is { } name
                ? new Choice(name, shown, l)
                : new Choice(shown, code, l, DetailIsCode: true);
        }).ToList();
        ShowModal(new ChoicePopup(title, choices), offset, title, choice =>
        {
            if (choice?.Value is LspLocation location)
            {
                Arrive(location);
            }
            return Task.CompletedTask;
        });
    }

    private void Arrive(LspLocation location)
    {
        string path = DocumentUris.ToPath(location.Uri);
        Navigate?.Invoke(this, (path, ServerPlaces.PositionOf(editors, path, location.Range.Start)));
    }

    private async Task Reformat()
    {
        BackgroundWork.Begin();
        try
        {
            await ReformatCore();
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private async Task ReformatCore()
    {
        if (editors.Active is { } shown && XamlService.IsXaml(shown.Path))
        {
            if (await ReformatXaml(shown, "Reformat Code") is { } changed)
            {
                Message?.Invoke(this, changed ? "Reformatted" : "Already formatted");
            }
            return;
        }
        // Prettier's "Run on 'Reformat Code' action": a file its .prettierignore names goes to its language server.
        if (editors.Active is { } file && PrettierReformats(file) && Web?.Invoke() is { } web
            && await Prettify(web, file, "Reformat Code") is not (Prettified.Ignored or Prettified.Unsupported))
        {
            return;
        }
        if (Ask("Reformat Code") is not { } lsp || editors.Active is not { } document)
        {
            return;
        }
        try
        {
            using ChangeTracker asked = new(document.Text);
            IReadOnlyList<LspTextEdit> edits = await lsp.FormatAsync(document.Indentation.Options);
            if (ApplyAnswer(document, asked, edits, "Reformat Code"))
            {
                Message?.Invoke(this, edits.Count == 0 ? "Already formatted" : "Reformatted");
            }
        }
        catch (LanguageServerFailed failed)
        {
            Failed("Reformat Code", failed);
        }
    }

    private async Task Cleanup()
    {
        BackgroundWork.Begin();
        try
        {
            await CleanupCore();
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    /// <summary>Code Cleanup on the file, through the language server's own fixes: the unnecessary usings removed
    /// (IDE0005's fix), the usings sorted, then the file reformatted.</summary>
    private async Task CleanupCore()
    {
        // Code Cleanup on XAML reformats it, by the same code style.
        if (editors.Active is { } shown && XamlService.IsXaml(shown.Path))
        {
            if (await ReformatXaml(shown, "Code Cleanup") is { } changed)
            {
                Message?.Invoke(this, changed ? "Code cleanup reformatted" : "Already clean");
            }
            return;
        }
        if (Ask("Code Cleanup") is not { } lsp || editors.Active is not { } document)
        {
            return;
        }
        try
        {
            Message?.Invoke(this, CleanedUp(await CleanUpAsync(lsp, document)));
        }
        catch (LanguageServerFailed failed)
        {
            Failed("Code Cleanup", failed);
        }
    }

    /// <summary>What Code Cleanup says it did to a file.</summary>
    public static string CleanedUp(IReadOnlyList<string> done) => done.Count == 0 ? "Already clean" : $"Code cleanup {string.Join(", ", done)}";

    /// <summary>Code Cleanup on a C# file: the unnecessary usings removed, the usings sorted, then the file reformatted;
    /// what it did, in the status bar's words ("removed unused usings"), none when the file was clean.</summary>
    public async Task<IReadOnlyList<string>> CleanUpAsync(LspDocument lsp, OpenDocument document)
    {
        List<string> done = [];
        (bool removed, bool sorted) = await TidyUsings(lsp, document);
        if (removed)
        {
            done.Add("removed unused usings");
        }
        if (sorted)
        {
            done.Add("sorted the usings");
        }
        string unformatted = document.Text.Snapshot.ToString();
        using ChangeTracker asked = new(document.Text);
        IReadOnlyList<LspTextEdit> edits = await lsp.FormatAsync(document.Indentation.Options);
        if (ApplyAnswer(document, asked, edits, "Code Cleanup") && document.Text.Snapshot.ToString() != unformatted)
        {
            done.Add("reformatted");
        }
        return done;
    }

    /// <summary>Runs the server's action of that title at a range, if it offers one; whether it did.</summary>
    private async Task<bool> ApplyNamed(LspDocument lsp, OpenDocument document, TextRange range, IReadOnlyList<Diagnostic> diagnostics, string title)
    {
        using Asked asked = WorkspaceEdits.Track(editors);
        IReadOnlyList<CodeAction> actions = await lsp.CodeActionsAsync(range, diagnostics);
        if (actions.FirstOrDefault(a => a.Title == title && a.Disabled is null) is not { } action)
        {
            return false;
        }
        return await Apply(document, action, null, title, asked);
    }

    /// <summary>A workspace edit applied, its open files' edits carried through what was typed since the request, as one
    /// command that Undo takes back whole (a fix all's files not open with it); one whose edited text changed meanwhile is
    /// left alone and named in the status bar. Whether every file took it.</summary>
    private bool ApplyWorkspace(System.Text.Json.JsonElement edit, Asked? asked, string what)
    {
        RefactorOutcome made = ApplyEdit(edit, asked, what);
        if (made.Problem is not null)
        {
            Message?.Invoke(this, made.Problem);
            return false;
        }
        if (made.Left.Count > 0)
        {
            Message?.Invoke(this, $"{what} was not applied to {string.Join(", ", made.Left)}: it was edited meanwhile");
        }
        return made.Left.Count == 0;
    }

    /// <summary><see cref="ApplyWorkspace"/>'s change made: the files it changed and those it left, or why not.</summary>
    private RefactorOutcome ApplyEdit(System.Text.Json.JsonElement edit, Asked? asked, string what)
    {
        List<string> dropped = [];
        IReadOnlyList<RefactoredFile> files = WorkspaceEdits.Files(edit, WorkspaceEdits.Answered(editors, asked));
        try
        {
            WorkspaceEdits.ApplyAsCommand(editors, edit, what, asked, dropped);
        }
        catch (WorkspaceEditFailed failed)
        {
            return RefactorOutcome.Refused($"{what} was not applied: {failed.Message}");
        }
        return new RefactorOutcome(string.Empty, null, files, dropped);
    }

    /// <summary>Edits answered for a document's text as it was asked about, applied to that document as it is now (through
    /// the editor when it shows it), never to whatever file the editor shows by then; refused, said in the status bar,
    /// when the code they change was edited meanwhile.</summary>
    private bool ApplyAnswer(OpenDocument document, ChangeTracker asked, IReadOnlyList<LspTextEdit> answer, string what)
    {
        if (!editors.Documents.Contains(document))
        {
            return false;
        }
        if (WorkspaceEdits.Carried(asked, answer, document.Indentation.Options) is not { } edits)
        {
            Message?.Invoke(this, $"{what} was not applied: the code it changes was edited meanwhile");
            return false;
        }
        if (edits.Count > 0)
        {
            WorkspaceEdits.ApplyTo(editors, document, edits);
        }
        return true;
    }

    /// <summary>The next (or previous) error or warning after the caret, wrapping.</summary>
    private void StepToMarker(bool forward)
    {
        List<Marker> problems = Editor.Markers.Where(m => m.Kind is MarkerKind.Error or MarkerKind.Warning).ToList();
        if (problems.Count == 0)
        {
            Message?.Invoke(this, "No errors or warnings in this file");
            return;
        }
        int caret = Editor.Selections.Primary.Caret;
        Marker target = forward
            ? problems.FirstOrDefault(m => m.Range.Start > caret, problems[0])
            : problems.LastOrDefault(m => m.Range.Start < caret, problems[^1]);
        Editor.Selections = SelectionSet.Single(target.Range.Start);
        _ = ShowHover(target.Range.Start, Editor.PointOf(target.Range.Start));
    }

    private void ShowModal(ChoicePopup popup, int offset, string what, Func<Choice?, Task> done)
    {
        CloseModal();
        modal = popup;
        layer.Children.Add(popup);
        Place(popup, offset, above: false);
        popup.Done += (_, choice) => _ = Carried(what, () => done(choice));
    }

    private void ShowModal(RenameBox box, int offset, Func<string?, Task> done)
    {
        CloseModal();
        modal = box;
        layer.Children.Add(box);
        Place(box, offset, above: true);
        box.Done += (_, name) => _ = Carried("Rename", () => done(name));
    }

    /// <summary>What a modal's answer does (a fix chosen, a new name), carried out as background work. A failure of the
    /// server's, or an edit the disk refused, is said in the status bar, should the work not say it itself; anything else
    /// is the IDE's own bug, reported as an IDE error rather than lost in the event's handler.</summary>
    private async Task Carried(string what, Func<Task> done, bool closeModal = true)
    {
        if (closeModal)
        {
            CloseModal();
        }
        BackgroundWork.Begin();
        try
        {
            await done();
        }
        catch (LanguageServerFailed failed)
        {
            Failed(what, failed);
        }
        catch (WorkspaceEditFailed failed)
        {
            Message?.Invoke(this, $"{what} failed: {failed.Message}");
        }
        catch (Exception error)
        {
            IdeErrors.Report(error);
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private void CloseModal()
    {
        if (modal is not null)
        {
            layer.Children.Remove(modal);
            modal = null;
            Editor.Focus();
        }
        Action? closed = modalClosed;
        modalClosed = null;
        closed?.Invoke();
    }

    /// <summary>The popups go to the layer over the active editor's window, an editor window's when it has the active
    /// group, since a popup is placed in its layer's coordinates and one window's cannot reach another's.</summary>
    private void FollowLayer()
    {
        if (editors.AssistLayer == layer)
        {
            return;
        }
        foreach (Control popup in new Control[] { hoverCard, signatureCard, completion, itemCard })
        {
            layer.Children.Remove(popup);
            editors.AssistLayer.Children.Add(popup);
        }
        layer = editors.AssistLayer;
    }

    private void CloseAll()
    {
        CloseCompletion();
        HideHover();
        signing?.Cancel();
        signatureCard.IsVisible = false;
        CloseModal();
    }

    private void Place(Control popup, int offset, bool above) => Place(popup, Editor.PointOf(offset), above);

    /// <summary>Puts a popup under the line at <paramref name="at"/> (its top), or over it when asked
    /// or when there is no room below, kept inside the editor.</summary>
    private void Place(Control popup, Point at, bool above)
    {
        // The point is the editor's; the layer spans every split, so it is translated into the layer's.
        at = Editor.TranslatePoint(at, layer) ?? at;
        popup.Measure(Size.Infinity);
        Size size = popup.DesiredSize;
        Size room = layer.Bounds.Size;
        double lineHeight = Editor.LineHeight;
        double x = Math.Clamp(at.X - 4, 0, Math.Max(0, room.Width - size.Width - 8));
        double below = at.Y + lineHeight + 2;
        double over = at.Y - size.Height - 2;
        double y = above ? (over >= 0 ? over : below) : (below + size.Height <= room.Height || over < 0 ? below : over);
        Canvas.SetLeft(popup, x);
        Canvas.SetTop(popup, y);
    }

    private int WordStart(int caret)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int start = caret;
        while (start > 0 && IsIdentifierChar(text[start - 1]))
        {
            start--;
        }
        return start;
    }

    /// <summary>The line of each of a list of places, in the editor's lines, and its code, each closed file read once for
    /// the list, not once a place, since it is read on the UI thread; an open file's from its text.</summary>
    private Func<string, LspPosition, (int Line, string Code)> LinePreviews()
    {
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(editors);
        return (path, at) =>
        {
            TextSnapshot? text = texts(path);
            TextPosition place = ServerPlaces.PositionOf(text, at);
            return (place.Line, text is not null && place.Line < text.LineCount ? text.GetLineText(place.Line).Trim() : string.Empty);
        };
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Whether the file shown is a Razor file, whose completion's words are markup's as well as C#'s.</summary>
    private bool InRazor => Current?.Lsp?.IsRazor == true;

    /// <summary>A character of the word a completion list narrows by: an identifier's, and in a Razor file a markup
    /// name's too (<c>@onclick</c>, <c>data-enhance-nav</c>, <c>@bind:after</c>).</summary>
    private bool IsWordChar(char c) => IsIdentifierChar(c) || (c is '@' or '-' or ':' && InRazor) || (open?.ResourceNames == true && c == '-') || (xamlOffered.Count > 0 && xamlWordCharacters.Contains(c))
        || (msbuildOffered.Count > 0 && msbuildWordCharacters.Contains(c))
        || (WebCurrent is { } web && IsWebWordChar(web, c));

    /// <summary>Where a Razor list's word starts when its items say no range: back over an identifier, then over as much of
    /// a markup name before it as some item begins with, so <c>data-en</c> is replaced whole by <c>data-enhance-nav</c>
    /// while C#'s <c>a-b</c> completes only <c>b</c>.</summary>
    private int RazorWordStart(int caret, IReadOnlyList<CompletionItem> items)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int identifier = WordStart(caret);
        int start = identifier;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }
        for (; start < identifier; start++)
        {
            string typed = text.GetText(start, caret - start);
            if (items.Any(i => (i.FilterText ?? i.Label).StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            {
                return start;
            }
        }
        return identifier;
    }

    /// <summary>A XAML colour's badge, the colour itself; null for any other item.</summary>
    private IBrush? Swatch(CompletionItem item) =>
        xamlOffered.TryGetValue(item, out XamlItem? offered) && KnownColours.Parse(offered.Colour) is { } colour
            ? new SolidColorBrush(Color.FromArgb(colour.A, colour.R, colour.G, colour.B))
            : null;

    /// <summary>A row's badge: the letter and colour of each kind of completion.</summary>
    private static CompletionRow Row(CompletionItem item)
    {
        (string letter, string brush) = item.Kind switch
        {
            2 or 3 => ("m", "Passenger.Syntax.Method"),
            4 => ("c", "Passenger.Syntax.Class"),
            5 => ("f", "Passenger.Syntax.Field"),
            6 => ("v", "Passenger.TextMuted"),
            7 => ("C", "Passenger.Syntax.Class"),
            8 => ("I", "Passenger.Syntax.Interface"),
            9 => ("N", "Passenger.Syntax.Keyword"),
            10 => ("p", "Passenger.Syntax.Property"),
            13 => ("E", "Passenger.Syntax.Enum"),
            14 => ("k", "Passenger.Syntax.Keyword"),
            15 => ("s", "Passenger.TextMuted"),
            20 => ("e", "Passenger.Syntax.EnumMember"),
            21 => ("c", "Passenger.Syntax.Constant"),
            22 => ("S", "Passenger.Syntax.Struct"),
            23 => ("e", "Passenger.Syntax.Event"),
            24 => ("o", "Passenger.TextMuted"),
            25 => ("T", "Passenger.Syntax.TypeParameter"),
            _ => ("·", "Passenger.TextMuted"),
        };
        string detail = item.LabelDetails?.Description ?? item.Detail ?? string.Empty;
        return new CompletionRow(item, letter, brush, detail);
    }

    /// <summary>An open completion list: its items, where the name it completes starts, the characters that commit an
    /// item that names none of its own, and whether the server left items out until more is typed.</summary>
    private sealed record Completion(IReadOnlyList<CompletionItem> Items, int Start, IReadOnlyList<string> CommitCharacters, bool IsIncomplete)
    {
        public bool ResourceNames { get; init; }

        /// <summary>A settings file's keys alone, Passenger's own, offered where its JSON server does not run.</summary>
        public bool Settings { get; init; }

        /// <summary>The text after the caret a web server's items replace as well (a JSON string's closing quote, the rest
        /// of a CSS name the caret is inside), as it stood when the list was asked for; empty for none.</summary>
        public string Tail { get; init; } = string.Empty;

        /// <summary>The text before the caret a web server's items replace, from <see cref="Start"/>, as it stood when the
        /// list was asked for (TypeScript's member dot, a JSON string's opening quote); empty for none.</summary>
        public string Lead { get; init; } = string.Empty;
    }

    /// <summary>The text after the caret a list's items replace, by the first item's range past the caret.</summary>
    private static string Tail(TextSnapshot asked, IReadOnlyList<CompletionItem> items, int caret)
    {
        if (items.FirstOrDefault(i => i.TextEdit is not null)?.TextEdit is not { } edit)
        {
            return string.Empty;
        }
        int end = Math.Min(ServerLines.Of(asked).OffsetOf(edit.Range.End), asked.Length);
        return end > caret && caret <= asked.Length ? asked.GetText(caret, end - caret) : string.Empty;
    }
}

/// <summary>What the debugger's hover card says of what the pointer rests on: its value as code, and what went wrong reading
/// it (a binding that failed), said first in the error colour; a value with members as its row instead (<see cref="Values"/>).</summary>
public sealed record DebuggerHover(string Code, string? Problem = null)
{
    /// <summary>A value with members as its row of a stop's values, which opens in the card as the Variables view's rows do.</summary>
    public Debugging.VariablesList? Values { get; init; }

    /// <summary>Cancelled once the stop the value was read at is gone: the card goes with it.</summary>
    public CancellationToken Gone { get; init; }
}
