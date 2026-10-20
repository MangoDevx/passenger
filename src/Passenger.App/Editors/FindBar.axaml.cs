using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Passenger.App.Tables;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>Find and replace in the open file. Matches are found as the query is typed and drawn in
/// the editor; the one nearest after the caret is selected, and the count says which of how many. While a table lies
/// over the editor, its cells are searched instead (read-only: replacing is the Text view's).</summary>
public partial class FindBar : UserControl
{
    private CodeEditor? editor;
    private TextDocument? document;
    private SearchResult result = SearchResult.None;
    private int current = -1;
    private CellGrid? grid;
    private TableSearch cells = TableSearch.None;
    private CancellationTokenSource? searching;

    public FindBar()
    {
        InitializeComponent();
        IsVisible = false;
        Query.TextChanged += (_, _) => Search(jump: true);
        MatchCase.IsCheckedChanged += (_, _) => Search(jump: true);
        WholeWords.IsCheckedChanged += (_, _) => Search(jump: true);
        UseRegex.IsCheckedChanged += (_, _) => Search(jump: true);
        NextButton.Click += (_, _) => Next();
        PreviousButton.Click += (_, _) => Previous();
        CloseButton.Click += (_, _) => Close();
        ReplaceButton.Click += (_, _) => ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        // On the way down, before the text box takes Enter and Escape for itself.
        Query.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
        ReplaceWith.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>What the count beside the arrows says: "2/5", "0 results", a bad regex's error.</summary>
    public string CountText => Count.Text ?? string.Empty;

    public SearchQuery CurrentQuery => new(Query.Text ?? string.Empty, MatchCase.IsChecked == true, WholeWords.IsChecked == true, UseRegex.IsChecked == true);

    /// <summary>Follows whichever document the editor shows, or the table over it when there is one; called when a tab is
    /// activated and when a table file switches between its table and its text.</summary>
    public void Attach(CodeEditor shown, CellGrid? table = null)
    {
        editor = shown;
        Follow(table);
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
        }
        document = shown.Document;
        document.Changed += OnDocumentChanged;
        if (IsVisible)
        {
            Search(jump: false);
        }
    }

    /// <summary>Hides the bar and lets go of the editor and its text, as its group does when its last tab closes: nothing the
    /// file holds keeps the bar, or the group around it, alive.</summary>
    public void Detach()
    {
        IsVisible = false;
        if (document is not null)
        {
            document.Changed -= OnDocumentChanged;
        }
        document = null;
        editor = null;
        Follow(null);
        result = SearchResult.None;
        current = -1;
    }

    private void Follow(CellGrid? table)
    {
        if (grid is not null)
        {
            grid.TableChanged -= OnTableChanged;
            grid.SetMatches([], -1);
        }
        searching?.Cancel();
        grid = table;
        cells = TableSearch.None;
        if (grid is not null)
        {
            grid.TableChanged += OnTableChanged;
        }
        ReplaceWith.IsEnabled = ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = grid is null;
        ToolTip.SetTip(ReplaceRow, grid is null ? null : "Show the file's Text to replace in it");
    }

    /// <summary>Opens the bar (with the replace row if <paramref name="replace"/>), its query the
    /// selection when that is on one line, or the word under the caret.</summary>
    public void Open(bool replace)
    {
        if (editor is null)
        {
            return;
        }
        IsVisible = true;
        ReplaceRow.IsVisible = replace;
        // A table's chosen cell seeds nothing: the query typed last is what is looked for again.
        string? seed = grid is null ? Seed(editor) : null;
        if (seed is not null && seed != Query.Text)
        {
            Query.Text = seed;
        }
        else
        {
            Search(jump: false);
        }
        // A control shown this moment is not laid out yet and refuses the focus; it takes it once it is.
        Dispatcher.UIThread.Post(() =>
        {
            Query.Focus();
            Query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The selection when it is on one line, or the word under the caret.</summary>
    private static string? Seed(CodeEditor editor)
    {
        TextSnapshot text = editor.Document.Snapshot;
        Selection primary = editor.Selections.Primary;
        string? seed = null;
        if (!primary.IsEmpty && text.GetLineFromOffset(primary.Start) == text.GetLineFromOffset(primary.End))
        {
            seed = text.GetText(primary.Range);
        }
        else if (primary.IsEmpty)
        {
            TextRange word = TextNavigation.WordAt(text, primary.Caret);
            if (word.Length > 0 && TextNavigation.IsWordChar(text[word.Start]))
            {
                seed = text.GetText(word);
            }
        }
        return seed;
    }

    public void Close()
    {
        IsVisible = false;
        searching?.Cancel();
        editor?.SetHighlights([], -1);
        if (grid is not null)
        {
            grid.SetMatches([], -1);
            grid.Focus();
        }
        else
        {
            editor?.Focus();
        }
    }

    public void Next() => Step(forward: true);

    public void Previous() => Step(forward: false);

    private void Search(bool jump)
    {
        if (editor is null || !IsVisible)
        {
            return;
        }
        if (grid is not null)
        {
            editor.SetHighlights([], -1);
            _ = SearchCells(grid, jump);
            return;
        }
        result = TextSearch.FindAll(editor.Document.Snapshot, CurrentQuery);
        current = -1;
        if (result.Matches.Count > 0)
        {
            Selection primary = editor.Selections.Primary;
            current = result.NextFrom(primary.Start);
            if (jump)
            {
                editor.Select(result.Matches[current]);
            }
        }
        Show();
    }

    /// <summary>Searches the table's cells off the UI thread, a newer search or table ending this one's; the match at or
    /// after the chosen cell is the current one.</summary>
    private async Task SearchCells(CellGrid shown, bool jump)
    {
        searching?.Cancel();
        CancellationTokenSource cancel = searching = new CancellationTokenSource();
        SearchQuery query = CurrentQuery;
        TableSearch found = shown.Table is { } table && !query.IsEmpty
            ? await BackgroundWork.Run(() => TableSearch.Find(table, query, cancel.Token))
            : TableSearch.None;
        if (cancel.IsCancellationRequested || shown != grid || !IsVisible)
        {
            return;
        }
        cells = found;
        current = cells.NextFrom(shown.Caret, including: true);
        if (jump && current >= 0)
        {
            shown.Choose(cells.Matches[current], cells.Matches[current]);
        }
        ShowCells();
    }

    private void StepCells(CellGrid shown, bool forward)
    {
        if (cells.Matches.Count == 0)
        {
            return;
        }
        current = forward ? cells.NextFrom(shown.Caret, including: false) : cells.PreviousFrom(shown.Caret);
        shown.Choose(cells.Matches[current], cells.Matches[current]);
        ShowCells();
    }

    private void ShowCells()
    {
        grid?.SetMatches(cells.Matches, current);
        Count.Text = Counted(cells.Problem, cells.Matches.Count, cells.Truncated);
    }

    private void OnTableChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Search(jump: false);
        }
    }

    private void Step(bool forward)
    {
        if (grid is not null)
        {
            StepCells(grid, forward);
            return;
        }
        if (editor is null || result.Matches.Count == 0)
        {
            return;
        }
        Selection primary = editor.Selections.Primary;
        current = forward ? result.NextFrom(primary.End) : result.PreviousFrom(primary.Start);
        // An empty match (a line's start) the caret already stands on is passed, or Next would stay on it.
        if (forward && primary.IsEmpty && result.Matches[current] is { Length: 0 } here && here.Start == primary.End)
        {
            current = (current + 1) % result.Matches.Count;
        }
        editor.Select(result.Matches[current]);
        Show();
    }

    private void ReplaceCurrent()
    {
        if (editor is null || grid is not null || current < 0 || current >= result.Matches.Count)
        {
            return;
        }
        TextRange match = result.Matches[current];
        Selection primary = editor.Selections.Primary;
        if (primary.Start != match.Start || primary.End != match.End)
        {
            editor.Select(match);
            return;
        }
        // The result is found again at every change of the document, so its matches are this text's.
        string replacement = result.Replacement(current, ReplaceWith.Text ?? string.Empty);
        int after = match.Start + replacement.Length;
        editor.ApplyEdits([new TextEdit(match.Start, match.Length, replacement)], SelectionSet.Single(after));
        Step(forward: true);
    }

    private void ReplaceAll()
    {
        if (editor is null || grid is not null)
        {
            return;
        }
        IReadOnlyList<TextEdit> edits = TextSearch.FindAll(editor.Document.Snapshot, CurrentQuery, int.MaxValue).Replacements(ReplaceWith.Text ?? string.Empty);
        editor.ApplyEdits(edits);
        Count.Text = $"Replaced {edits.Count}";
    }

    private void Show()
    {
        editor?.SetHighlights(result.Matches, current);
        Count.Text = Counted(result.Problem, result.Matches.Count, result.Truncated);
    }

    private string Counted(string? problem, int count, bool truncated) =>
        problem ?? (string.IsNullOrEmpty(Query.Text) ? string.Empty
            : count == 0 ? "0 results"
            : $"{current + 1}/{count}{(truncated ? "+" : string.Empty)}");

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (IsVisible)
        {
            Search(jump: false);
        }
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when sender == ReplaceWith && grid is null:
                ReplaceCurrent();
                e.Handled = true;
                break;
            case Key.Enter:
                Step(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
