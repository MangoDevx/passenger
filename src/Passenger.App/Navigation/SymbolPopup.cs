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
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Navigation;

/// <summary>A place a symbol popup offers: a type, a member, a file. <see cref="Depth"/> indents it under its
/// container (File Structure's members under their type).</summary>
public sealed record SymbolRow(string Name, string? Detail, string? Where, int Kind, string Path, TextPosition Position, int Depth = 0, int? EndLine = null)
{
    /// <summary>Whether a line (from zero) is inside the declaration: the caret's member is chosen when File
    /// Structure opens.</summary>
    public bool Spans(int line) => Position.Line <= line && line <= (EndLine ?? Position.Line);

    /// <summary>Where the language server put it, in its lines, read again in its file as it is gone to, which may have
    /// changed since the row's <see cref="Position"/> was read from it.</summary>
    public Passenger.Lsp.LspPosition? ServerPosition { get; init; }

    /// <summary>A file, not a symbol: shown with its file icon (Go to Everything's files).</summary>
    public const int FileKind = -1;
}

/// <summary>A query over symbols, answered as it is typed (a moment after the last key, the previous answer given
/// up): Go to Symbol, Go to Everything, File Structure and Recent Files, whose switch (Show edited only) asks again as it
/// turns. Up and Down move, Enter goes, Escape leaves. A search the language server cannot answer fails with a
/// <see cref="LanguageServerFailed"/>, whose message the popup shows.</summary>
public sealed class SymbolPopup : UserControl
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(120);

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search;
    private readonly TextBox query;
    private readonly ListBox results;
    private readonly TextBlock empty;
    private readonly Debounce typing;
    private readonly CheckBox? option;
    private CancellationTokenSource? asking;

    public SymbolPopup(string title, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<SymbolRow>>> search, string? initialQuery = null,
        (string Label, bool Checked)? option = null)
    {
        this.search = search;
        query = new TextBox { Classes = { "search" }, PlaceholderText = placeholder, Text = initialQuery ?? string.Empty };
        results = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = ListRow.Height * 15,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = new FuncDataTemplate<SymbolRow>((row, _) => Row(row), supportsRecycling: false),
        };
        empty = new TextBlock { Classes = { "muted" }, Margin = new Thickness(12, 0, 12, 12), IsVisible = false };
        results.DoubleTapped += (_, _) => Choose();
        typing = new Debounce(Pause, Refresh);
        query.TextChanged += (_, _) => typing.Restart();
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        DockPanel body = new();
        DockPanel heading = new();
        if (option is { } shown)
        {
            // Not focusable: the keys stay with the query.
            this.option = new CheckBox { Content = shown.Label, IsChecked = shown.Checked, Focusable = false, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            this.option.IsCheckedChanged += (_, _) =>
            {
                OptionChanged?.Invoke(this, IsOptionChecked);
                _ = Refresh();
            };
            DockPanel.SetDock(this.option, Dock.Right);
            heading.Children.Add(this.option);
        }
        heading.Children.Add(new TextBlock { Classes = { "popupTitle" }, Text = title, VerticalAlignment = VerticalAlignment.Center });
        StackPanel top = new() { Margin = new Thickness(12, 10, 12, 8), Spacing = 8, Children = { heading, query } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(empty);
        body.Children.Add(results);
        Content = new Border { Classes = { "popup" }, Child = body };
        Width = PopupFrame.Wide;
        _ = Refresh();
        Dispatcher.UIThread.Post(() =>
        {
            query.Focus();
            query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The place chosen, or null when the popup was dismissed.</summary>
    public event EventHandler<SymbolRow?>? Done;

    /// <summary>The popup's switch turned, before it asks again; its search reads what it is now.</summary>
    public event EventHandler<bool>? OptionChanged;

    /// <summary>Whether the popup's switch (Recent Files' Show edited only) is on; false for a popup without one.</summary>
    public bool IsOptionChecked
    {
        get => option?.IsChecked == true;
        set
        {
            if (option is not null)
            {
                option.IsChecked = value;
            }
        }
    }

    public IReadOnlyList<SymbolRow> Rows => results.ItemsSource as IReadOnlyList<SymbolRow> ?? [];

    public SymbolRow? Selected => results.SelectedItem as SymbolRow;

    /// <summary>The answer to the query as it now is is in.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>Which of the first rows to choose (the caret's member), before anything is typed.</summary>
    public Func<IReadOnlyList<SymbolRow>, int>? Initial { get; init; }

    private async Task Refresh()
    {
        asking?.Cancel();
        CancellationTokenSource mine = asking = new CancellationTokenSource();
        IsSettled = false;
        BackgroundWork.Begin();
        try
        {
            IReadOnlyList<SymbolRow> found = await search(query.Text ?? string.Empty, mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }
            results.ItemsSource = found;
            int chosen = Initial is { } initial && (query.Text ?? string.Empty).Length == 0 ? initial(found) : 0;
            results.SelectedIndex = found.Count > 0 ? Math.Clamp(chosen, 0, found.Count - 1) : -1;
            if (results.SelectedIndex > 0)
            {
                results.ScrollIntoView(results.SelectedIndex);
            }
            empty.IsVisible = found.Count == 0;
            empty.Text = "Nothing matches";
            IsSettled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (LanguageServerFailed failed)
        {
            empty.Text = failed.Message;
            empty.IsVisible = true;
            IsSettled = true;
        }
        finally
        {
            BackgroundWork.End();
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (ListKeys.Move(results, e))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
        }
    }

    private void Choose()
    {
        if (results.SelectedItem is SymbolRow row)
        {
            Done?.Invoke(this, row);
        }
    }

    /// <summary>A symbol's row as every symbol list draws it: its badge, its name and type, where it is.</summary>
    public static Control Row(SymbolRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }
        Control badge;
        if (row.Kind == SymbolRow.FileKind)
        {
            badge = new PathIcon { Data = Explorer.FileIcons.ForFile(row.Path), Foreground = Explorer.FileIcons.BrushForFile(row.Path), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            (string letter, string brush) = Badge(row.Kind);
            badge = KindBadge(letter, brush);
            badge.Margin = new Thickness(0, 0, 8, 0);
        }
        TextBlock where = new()
        {
            [DockPanel.DockProperty] = Dock.Right,
            Text = row.Where ?? string.Empty,
            Foreground = InfoCard.Brush("Passenger.TextMuted"),
            FontSize = 12,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        StackPanel name = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center },
                // A member's type follows its name close, as "Sum(int): decimal"; a container stands apart.
                new TextBlock
                {
                    Text = row.Detail ?? string.Empty,
                    Foreground = InfoCard.Brush("Passenger.TextMuted"),
                    FontSize = 12,
                    Margin = new Thickness(row.Detail?.StartsWith(':') == true ? -8 : 0, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 300,
                },
            },
        };
        return new DockPanel { Height = ListRow.Height, Margin = new Thickness(row.Depth * 16, 0, 0, 0), Children = { where, badge, name } };
    }

    /// <summary>A kind's letter on its colour, marking a symbol or a kind of type.</summary>
    public static Control KindBadge(string letter, string brush) => new Border
    {
        Width = 16,
        Height = 16,
        Classes = { "kindBadge" },
        Background = InfoCard.Brush(brush),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = letter,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = InfoCard.Brush("Passenger.BadgeText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>The letter and colour of a symbol of each kind (the protocol's SymbolKind).</summary>
    public static (string Letter, string Brush) Badge(int kind) => kind switch
    {
        3 => ("N", "Passenger.Syntax.Keyword"),
        5 => ("C", "Passenger.Syntax.Class"),
        6 or 12 => ("m", "Passenger.Syntax.Method"),
        7 => ("p", "Passenger.Syntax.Property"),
        8 => ("f", "Passenger.Syntax.Field"),
        9 => ("m", "Passenger.Syntax.Method"),
        10 => ("E", "Passenger.Syntax.Enum"),
        11 => ("I", "Passenger.Syntax.Interface"),
        13 => ("v", "Passenger.TextMuted"),
        14 => ("c", "Passenger.Syntax.Constant"),
        22 => ("e", "Passenger.Syntax.EnumMember"),
        23 => ("S", "Passenger.Syntax.Struct"),
        24 => ("e", "Passenger.Syntax.Event"),
        25 => ("o", "Passenger.TextMuted"),
        26 => ("T", "Passenger.Syntax.TypeParameter"),
        _ => ("·", "Passenger.TextMuted"),
    };
}
