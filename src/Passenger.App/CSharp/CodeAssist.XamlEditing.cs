using System.Text.Json;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.App.CSharp;

/// <summary>The XAML editing tools, Avalonia's and WPF's alike: Reformat Code (and Code Cleanup) by the XAML code style
/// with the file's .editorconfig over it, Extract Resource and Extract Style with their dialogs, the fixes that create
/// what a binding or a converter names, and Surround With's elements. What changes more than one file, or makes one, is
/// one command Undo takes back whole.</summary>
public sealed partial class CodeAssist
{
    /// <summary>The XAML code style as Settings leaves it, which a file's .editorconfig overrides.</summary>
    public XamlFormatSettings XamlFormat { get; set; } = new();

    /// <summary>Reformat Code's edits for an open XAML file as it stands, by <paramref name="settings"/> with its
    /// .editorconfig over them, its own indent and line breaks; only those in the lines of <paramref name="within"/> when
    /// given. Worked out off the UI thread.</summary>
    public static Task<XamlFormatted> FormatXamlAsync(OpenDocument document, XamlFormatSettings settings, TextRange? within = null)
    {
        TextSnapshot text = document.Text.Snapshot;
        string path = document.Path;
        IndentOptions indent = document.Indentation.Options;
        string eol = document.Text.LineEnding;
        return BackgroundWork.Run(() =>
        {
            EditorConfig config = EditorConfig.For(path);
            return XamlFormatter.Format(text.ToString(), settings.Over(key => config[key]), indent, eol, within);
        });
    }

    /// <summary>Reformat Code on a XAML file, the selection's lines alone when there is one, as one undo step; whether it
    /// changed the file, null when it could not (said in the status bar).</summary>
    private async Task<bool?> ReformatXaml(OpenDocument document, string what)
    {
        using ChangeTracker asked = new(document.Text);
        Selection selection = Editor.Selections.Primary;
        TextRange? within = editors.Active == document && !selection.IsEmpty ? selection.Range : null;
        XamlFormatted formatted = await FormatXamlAsync(document, XamlFormat, within);
        if (formatted.Problem is { } problem)
        {
            Message?.Invoke(this, $"{what} was not applied: {problem}");
            return null;
        }
        if (formatted.Edits.Count == 0 || !editors.Documents.Contains(document))
        {
            return false;
        }
        if (asked.Map(formatted.Edits) is not { } edits)
        {
            Message?.Invoke(this, $"{what} was not applied: the code it changes was edited meanwhile");
            return null;
        }
        WorkspaceEdits.ApplyTo(editors, document, edits);
        return true;
    }

    /// <summary>Extract Resource (Extract XAML Resource): the value at the caret to a keyed resource, through its
    /// dialog.</summary>
    private async Task ExtractResource()
    {
        const string name = "Extract Resource";
        ExtractResourceDialog? dialog = null;
        // Held until the dialog shows, which then waits for the person.
        BackgroundWork.Begin();
        try
        {
            if (await XamlAtCaret(name) is not ({ } file, int caret))
            {
                return;
            }
            (ExtractableValue? value, string? why) = await BackgroundWork.Run(() => XamlRefactorings.ValueAt(file, caret));
            if (value is null)
            {
                Message?.Invoke(this, why ?? $"{name} is not available here");
                return;
            }
            Func<string, string?> texts = editors.Texts().Text;
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: false, texts));
            dialog = new ExtractResourceDialog(value, places, key => XamlRefactorings.KeyProblem(file, key), (key, place, everywhere) =>
            {
                string? refused = ApplyFileChanges(name, XamlRefactorings.ExtractResource(file, value, key, place, everywhere));
                if (refused is null)
                {
                    int replaced = everywhere ? value.Uses.Count : 1;
                    Message?.Invoke(this, $"Extracted resource '{key}' to {place.Title}{(replaced > 1 ? $", used in {replaced} places" : string.Empty)}");
                }
                return Task.FromResult(refused);
            });
        }
        finally
        {
            BackgroundWork.End();
        }
        if (dialog is not null)
        {
            await ShowAsync(dialog);
        }
    }

    /// <summary>Extract Style (Extract XAML Style): the element's properties at the caret to a style, through its
    /// dialog.</summary>
    private async Task ExtractStyle()
    {
        const string name = "Extract Style";
        ExtractStyleDialog? dialog = null;
        BackgroundWork.Begin();
        try
        {
            if (await XamlAtCaret(name) is not ({ } file, int caret))
            {
                return;
            }
            (StyleSource? source, string? why) = await BackgroundWork.Run(() => XamlRefactorings.StyleAt(file, caret));
            if (source is null)
            {
                Message?.Invoke(this, why ?? $"{name} is not available here");
                return;
            }
            Func<string, string?> texts = editors.Texts().Text;
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: source.ByClass, texts));
            dialog = new ExtractStyleDialog(source, places, styleName => XamlRefactorings.StyleNameProblem(file, source, styleName), (styleName, chosen, place) =>
            {
                string? refused = ApplyFileChanges(name, XamlRefactorings.ExtractStyle(file, source, styleName, chosen, place));
                if (refused is null)
                {
                    Message?.Invoke(this, $"Extracted {Core.Plural.Of(chosen.Count, "property", "properties")} of {source.ElementName} to style '{styleName}' in {place.Title}");
                }
                return Task.FromResult(refused);
            });
        }
        finally
        {
            BackgroundWork.End();
        }
        if (dialog is not null)
        {
            await ShowAsync(dialog);
        }
    }

    /// <summary>The shown XAML file read as it stands and the caret; null, said in the status bar for
    /// <paramref name="what"/>, for a file that is not XAML of a project Passenger reads.</summary>
    private async Task<(XamlFile? File, int Caret)> XamlAtCaret(string what)
    {
        if (XamlCurrent is not { } xaml || editors.Active is not { } document)
        {
            Message?.Invoke(this, $"{what} works in a XAML file of an Avalonia or WPF project");
            return (null, 0);
        }
        int caret = Caret;
        if (await xaml.FileAsync() is not { } file)
        {
            Message?.Invoke(this, $"{what} is not available: the project's XAML is not read");
            return (null, 0);
        }
        return editors.Active == document ? (file, caret) : (null, 0);
    }

    /// <summary>A refactoring's or fix's changes to files made as one command (XAML's, an Aspire parameter's value set; Undo takes back
    /// every file, a file made included): each file's edits made from its text as it is now, open or on disk. Null once made; else why not.</summary>
    internal string? ApplyFileChanges(string name, IReadOnlyList<XamlFileChange> changes)
    {
        List<object> documentChanges = [];
        foreach (XamlFileChange change in changes)
        {
            string uri = DocumentUris.FromPath(change.Path);
            if (change.Made is { } made)
            {
                if (File.Exists(change.Path) || editors.Find(change.Path) is not null)
                {
                    return $"{Path.GetFileName(change.Path)} is already there";
                }
                documentChanges.Add(new { kind = "create", uri });
                documentChanges.Add(new { textDocument = new { uri }, edits = new[] { new LspTextEdit(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), made) } });
                continue;
            }
            string text = ReadOpenOrDisk(change.Path);
            IReadOnlyList<TextEdit> edits = change.Edits?.Invoke(text) ?? [];
            if (edits.Count == 0)
            {
                return $"{Path.GetFileName(change.Path)} is no longer as {name} found it";
            }
            documentChanges.Add(new { textDocument = new { uri }, edits = ServerPlaces.EditsOf(TextSnapshot.From(text), edits) });
        }
        JsonElement edit = JsonSerializer.SerializeToElement(new { documentChanges }, Json.Options);
        List<string> dropped = [];
        try
        {
            TypeRename.Apply(editors, edit, [], name, null, dropped);
        }
        catch (WorkspaceEditFailed failed)
        {
            return failed.Message;
        }
        if (changes.Any(c => c.Made is not null) || changes.Any(c => editors.Documents.All(d => d.Path != c.Path)))
        {
            FilesChanged?.Invoke(this, EventArgs.Empty);
        }
        return dropped.Count > 0 ? $"{name} was not applied to {string.Join(", ", dropped)}: it was edited meanwhile" : null;
    }

    /// <summary>The refactorings offered at the caret of a XAML file (Refactor This, and after the fixes in Show
    /// Context Actions): Extract Resource on a value, Extract Style on an element.</summary>
    private static IReadOnlyList<Choice> XamlRefactoringsAt(XamlFile file, int caret)
    {
        List<Choice> offered = [];
        if (XamlRefactorings.ValueAt(file, caret).Value is { } value)
        {
            offered.Add(new Choice("Extract Resource…", value.Property, XamlRefactoring.Resource));
        }
        if (XamlRefactorings.StyleAt(file, caret).Source is { } source)
        {
            offered.Add(new Choice("Extract Style…", source.ElementName, XamlRefactoring.Style));
        }
        return offered;
    }

    /// <summary>A XAML refactoring chosen from a list, run as its action would.</summary>
    private Task RunXamlRefactoring(XamlRefactoring chosen) => chosen switch
    {
        XamlRefactoring.Resource => ExtractResource(),
        XamlRefactoring.Style => ExtractStyle(),
        _ => Carried("Rename", () =>
        {
            ShowRename();
            return Task.CompletedTask;
        }, closeModal: false),
    };

    private enum XamlRefactoring
    {
        Resource,
        Style,
        Rename,
    }

    /// <summary>XAML's Surround With: the templates that fit the elements chosen (the element at the caret with nothing
    /// chosen), each with its digit; the one chosen goes around them, filled in from its first variable.</summary>
    private void ShowXamlSurround(OpenDocument document)
    {
        CloseCompletion();
        XmlTree tree = XmlTree.Parse(document.Text.Snapshot.ToString());
        if (XamlRefactorings.SurroundTarget(tree, Editor.Selections.Primary.Range) is not { } target)
        {
            Message?.Invoke(this, "Nothing here to surround: choose elements, not text, a comment or the root");
            return;
        }
        bool avalonia = tree.Root?.Attribute("xmlns")?.Value == AvaloniaDialect.Uri;
        IReadOnlyList<LiveTemplate> offered = [.. LiveTemplates.Xaml.Where(t => (target.Count == 1 || !t.OneChild) && (avalonia || !t.AvaloniaOnly)).Select(t => t.Template)];
        ChangeTracker opened = new(document.Text);
        ShowModal(new ChoicePopup("Surround With", [.. offered.Select(t => new Choice(t.Abbreviation, t.Description, t, Mnemonic: t.Mnemonic))]),
            Caret, "Surround With", choice =>
            {
                IReadOnlyList<TextEdit>? still = opened.Map([new TextEdit(target.Range.Start, target.Range.Length, string.Empty)]);
                opened.Dispose();
                if (choice?.Value is not LiveTemplate template)
                {
                    return Task.CompletedTask;
                }
                if (still is not [TextEdit wrapped] || Editor.Document != document.Text)
                {
                    Message?.Invoke(this, "Surround With was not applied: the code it wraps was edited meanwhile");
                    return Task.CompletedTask;
                }
                Surround(template, new TextRange(wrapped.Start, wrapped.Length));
                return Task.CompletedTask;
            });
    }
}
