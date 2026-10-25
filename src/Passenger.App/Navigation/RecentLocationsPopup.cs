using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.CSharp;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place Recent Locations shows: where it is, its file's name, the type and member it is in when known, and its
/// line with those either side as the file has them (the first of them <see cref="FirstLine"/>, from zero), coloured as
/// the file is.</summary>
public sealed record RecentLocation(HistoryPlace Place, string Name, string? Member, int FirstLine, string Lines, IHighlighter? Colours)
{
    /// <summary>Whether a query typed into the popup finds it: in its file's name, what it is in, or its code.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Member?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
        || Lines.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Recent Locations: the places left and edited, the latest first, each with its file, what it is in and its
/// lines of code. Show edited only (or the action's keys again) keeps the places edited. Typing narrows them by file,
/// member or code; Up and Down move, Enter or a double-click goes, Delete forgets a place, Escape leaves.</summary>
public sealed class RecentLocationsPopup : UserControl
{
    private readonly Func<bool, IReadOnlyList<RecentLocation>> places;
    private readonly TextBlock title;
    private readonly CheckBox editedOnly;
    private readonly TextBox query;
    private readonly ListBox list;
    private readonly TextBlock empty;
    private IReadOnlyList<RecentLocation> all = [];

    public RecentLocationsPopup(Func<bool, IReadOnlyList<RecentLocation>> places, bool editedOnly)
    {
        this.places = places;
        title = new TextBlock { Classes = { "popupTitle" }, VerticalAlignment = VerticalAlignment.Center };
        // Not focusable: the keys stay with the query.
        this.editedOnly = new CheckBox { Content = "Show edited only", IsChecked = editedOnly, Focusable = false, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        this.editedOnly.IsCheckedChanged += (_, _) => Reload();
        DockPanel heading = new();
        DockPanel.SetDock(this.editedOnly, Dock.Right);
        heading.Children.Add(this.editedOnly);
        heading.Children.Add(title);
        query = new TextBox { Classes = { "search" }, PlaceholderText = "A file's name, a member or code" };
        query.TextChanged += (_, _) => Filter();
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        list = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = 560,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = new FuncDataTemplate<RecentLocation>((row, _) => Row(row), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => Choose();
        empty = new TextBlock { Classes = { "muted" }, Margin = new Thickness(12, 0, 12, 12), IsVisible = false, Text = "No recent locations found" };
        StackPanel top = new() { Margin = new Thickness(12, 10, 12, 8), Spacing = 8, Children = { heading, query } };
        DockPanel body = new();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(empty);
        body.Children.Add(list);
        Content = new Border { Classes = { "popup" }, Child = body };
        Width = PopupFrame.Wide;
        Reload();
        Dispatcher.UIThread.Post(() => query.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The place chosen, or null when the popup was dismissed.</summary>
    public event EventHandler<HistoryPlace?>? Done;

    /// <summary>A place forgotten with Delete, which the history lets go of too.</summary>
    public event EventHandler<HistoryPlace>? Forgot;

    public IReadOnlyList<RecentLocation> Rows => list.ItemsSource as IReadOnlyList<RecentLocation> ?? [];

    public RecentLocation? Selected => list.SelectedItem as RecentLocation;

    /// <summary>The title for what is listed: every recent place, or only those edited.</summary>
    public string Title => title.Text ?? string.Empty;

    public bool EditedOnly
    {
        get => editedOnly.IsChecked == true;
        set => editedOnly.IsChecked = value;
    }

    public string Query
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    /// <summary>The places asked for again, as the switch between all of them and those edited turns.</summary>
    private void Reload()
    {
        title.Text = EditedOnly ? "Recently Edited Locations" : "Recent Locations";
        all = places(EditedOnly);
        Filter();
    }

    private void Filter()
    {
        string typed = Query.Trim();
        List<RecentLocation> shown = [.. all.Where(p => p.Matches(typed))];
        list.ItemsSource = shown;
        list.SelectedIndex = shown.Count > 0 ? 0 : -1;
        empty.IsVisible = shown.Count == 0;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(list, e):
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when Query.Length == 0 && Selected is { } chosen:
                Forget(chosen);
                e.Handled = true;
                break;
        }
    }

    private void Forget(RecentLocation chosen)
    {
        int at = list.SelectedIndex;
        all = [.. all.Where(p => p != chosen)];
        Forgot?.Invoke(this, chosen.Place);
        Filter();
        list.SelectedIndex = Math.Min(at, list.ItemCount - 1);
    }

    private void Choose()
    {
        if (Selected is { } row)
        {
            Done?.Invoke(this, row.Place);
        }
    }

    /// <summary>A place's row: its file and what it is in over its lines of code, numbered as the file numbers them,
    /// the place's own line marked.</summary>
    private static Control Row(RecentLocation? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }
        PathIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(row.Place.Path),
            Foreground = Explorer.FileIcons.BrushForFile(row.Place.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock member = new()
        {
            Text = row.Member ?? string.Empty,
            FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        member.Bind(TextBlock.ForegroundProperty, member.GetResourceObservable("Passenger.TextMuted"));
        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Height = ListRow.Height,
            Children = { icon, new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, member },
        };
        ToolTip.SetTip(header, row.Place.Path);

        TextDocument lines = new(row.Lines);
        int count = lines.Snapshot.LineCount;
        int own = Math.Clamp(row.Place.Line - row.FirstLine, 0, count - 1);
        CodeEditor code = new()
        {
            IsReadOnly = true,
            ShowErrorStripe = false,
            ShowLineNumbers = false,
            ShowScrollBars = false,
            Focusable = false,
            // The row takes the clicks, so a click chooses the place and a double-click goes there.
            IsHitTestVisible = false,
        };
        code.Show(lines, SelectionSet.Single(lines.Snapshot.GetLineStart(own)), default);
        code.Highlighter = row.Colours;
        code.Height = count * code.LineHeight;
        TextBlock numbers = new()
        {
            Text = string.Join('\n', Enumerable.Range(row.FirstLine + 1, count)),
            FontFamily = code.FontFamily,
            FontSize = code.FontSize,
            LineHeight = code.LineHeight,
            TextAlignment = TextAlignment.Right,
            MinWidth = 28,
            Margin = new Thickness(8, 0, 10, 0),
        };
        numbers.Bind(TextBlock.ForegroundProperty, numbers.GetResourceObservable("Passenger.Editor.LineNumber"));
        DockPanel fragment = new();
        DockPanel.SetDock(numbers, Dock.Left);
        fragment.Children.Add(numbers);
        fragment.Children.Add(code);
        Border frame = new() { Child = fragment, Classes = { "box" }, ClipToBounds = true, Padding = new Thickness(0, 4) };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Passenger.Editor.Background"));
        // A closed row lets go of the colours it was shown with, whose changes would otherwise still reach it.
        code.DetachedFromVisualTree += (_, _) => code.Highlighter = null;
        return new StackPanel { Margin = new Thickness(0, 2, 0, 6), Children = { header, frame } };
    }
}
