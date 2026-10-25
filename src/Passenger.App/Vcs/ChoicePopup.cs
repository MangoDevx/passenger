using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Passenger.App.Vcs;

/// <summary>One thing a <see cref="ChoicePopup"/> offers: what it is called, what it says beside that, and what it is.</summary>
public sealed record Choice(string Label, string Detail, object Value);

/// <summary>The popups that choose a version to compare with (Compare with Branch or Tag, Compare with Revision): a
/// search box over the list, narrowed as words are typed; Enter or a double-click takes the chosen one, Escape gives up.</summary>
public sealed class ChoicePopup : UserControl
{
    private readonly IReadOnlyList<Choice> choices;
    private readonly TextBox query;
    private readonly ListBox results;
    private readonly TextBlock empty;

    public ChoicePopup(string title, string placeholder, IReadOnlyList<Choice> choices, string nothing)
    {
        this.choices = choices;
        Width = PopupFrame.Medium;
        query = new TextBox { Classes = { "search" }, PlaceholderText = placeholder };
        results = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = ListRow.Height * 15,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = new FuncDataTemplate<Choice>((choice, _) => choice is null ? new TextBlock() : Row(choice), supportsRecycling: false),
        };
        empty = new TextBlock { Classes = { "muted" }, Text = nothing, Margin = new Thickness(12, 0, 12, 12), IsVisible = false };
        results.DoubleTapped += (_, _) => Choose();
        query.TextChanged += (_, _) => Refresh();
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        Content = new Border
        {
            Classes = { "popup" },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "popupTitle" }, Text = title, Margin = new Thickness(12, 10, 12, 6) },
                    new Border { Margin = new Thickness(10, 0, 10, 8), Child = query },
                    results,
                    empty,
                },
            },
        };
        Refresh();
        Dispatcher.UIThread.Post(() => query.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>What was chosen, or null when it was given up.</summary>
    public event EventHandler<Choice?>? Done;

    /// <summary>What is listed now.</summary>
    public IReadOnlyList<Choice> Rows => results.ItemsSource as IReadOnlyList<Choice> ?? [];

    public string Query
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    public Choice? Selected
    {
        get => results.SelectedItem as Choice;
        set => results.SelectedItem = value;
    }

    /// <summary>Takes the chosen one, as Enter does.</summary>
    public void Choose()
    {
        if (Selected is { } choice)
        {
            Done?.Invoke(this, choice);
        }
    }

    private void Refresh()
    {
        string[] words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<Choice> found = [.. choices.Where(c => words.All(w => c.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Detail.Contains(w, StringComparison.OrdinalIgnoreCase)))];
        results.ItemsSource = found;
        results.SelectedIndex = found.Count > 0 ? 0 : -1;
        empty.IsVisible = found.Count == 0;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown when ListKeys.Move(results, e):
                e.Handled = true;
                break;
        }
    }

    private static Control Row(Choice choice)
    {
        TextBlock detail = new() { Text = choice.Detail, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable("Passenger.TextMuted"));
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(detail, Dock.Right);
        row.Children.Add(detail);
        row.Children.Add(new TextBlock { Text = choice.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }
}
