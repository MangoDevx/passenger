using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What the editor does from the file's own syntax, with no language server to ask: Surround With and Complete
/// Current Statement.</summary>
public sealed partial class CodeAssist
{
    /// <summary>Surround With: the surround templates that fit the code chosen (or the caret's line), each with its
    /// digit; the one chosen goes around that code and is filled in as a live template, from its first variable to
    /// where it leaves the caret.</summary>
    private void ShowSurround()
    {
        if (editors.Active is { } markup && XamlService.IsXaml(markup.Path))
        {
            ShowXamlSurround(markup);
            return;
        }
        if (editors.Active is not { Syntax: { } parser } document)
        {
            return;
        }
        CloseCompletion();
        SurroundTarget target = SurroundScopes.Of(parser.Current, Editor.Selections.Primary.Range);
        IReadOnlyList<LiveTemplate> offered = Templates.Around(target.Scope);
        if (offered.Count == 0)
        {
            Message?.Invoke(this, "Nothing here to surround: choose code, not a comment or a string");
            return;
        }
        ChangeTracker opened = new(document.Text);
        // At the caret, below the code chosen when the caret ends it.
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

    /// <summary>A surround template over code: the code's lines after its first written relative to its first line's
    /// indentation, which the template's lines take.</summary>
    private void Surround(LiveTemplate template, TextRange range)
    {
        TextSnapshot text = Editor.Document.Snapshot;
        int lineStart = text.GetLineStart(text.GetLineFromOffset(range.Start));
        int indented = lineStart;
        while (indented < range.Start && text[indented] is ' ' or '\t')
        {
            indented++;
        }
        string indent = text.GetText(lineStart, indented - lineStart);
        string[] lines = text.GetText(range).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            int strip = 0;
            while (strip < indent.Length && strip < lines[i].Length && lines[i][strip] is ' ' or '\t')
            {
                strip++;
            }
            lines[i] = lines[i][strip..];
        }
        Editor.InsertSnippet(range, Snippet.Parse(template.Text, Values(template, range.Start), indent, Editor.Indent.Unit, string.Join('\n', lines)));
    }

    /// <summary>Complete Current Statement on the caret's line (<see cref="StatementCompletion"/>), as one undo
    /// step.</summary>
    private void CompleteStatement()
    {
        if (editors.Active?.Syntax is not { } parser)
        {
            return;
        }
        CloseCompletion();
        if (StatementCompletion.At(parser.Current, Caret, Editor.Indent, Editor.Document.LineEnding) is not { } completed)
        {
            Message?.Invoke(this, "Nothing to complete on this line");
            return;
        }
        if (completed.Edits.Count == 0)
        {
            Editor.Selections = SelectionSet.Single(completed.Caret);
            return;
        }
        Editor.ApplyEdits(completed.Edits, SelectionSet.Single(completed.Caret));
    }
}
