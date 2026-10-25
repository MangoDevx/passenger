using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Asks which of several ways to go on: what each does, a button for each and Cancel after them
/// (DialogFooter.Listed), closing with the words of the button chosen, or null for Cancel. Escape cancels.</summary>
public sealed class ChoiceDialog : Window
{
    /// <param name="chosen">The choice Enter takes, or null for Cancel, since either answer may change the
    /// file.</param>
    public ChoiceDialog(string title, string text, IReadOnlyList<string> choices, string? chosen)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        List<Button> answers = [];
        foreach (string choice in choices)
        {
            Button button = new() { Content = choice, IsDefault = choice == chosen };
            button.Click += (_, _) => Close(choice);
            answers.Add(button);
        }
        Button cancel = new() { Content = "Cancel", IsCancel = true, IsDefault = chosen is null };
        cancel.Click += (_, _) => Close(null);
        answers.Add(cancel);
        foreach (Button button in answers.Where(b => b.IsDefault))
        {
            button.Classes.Add("primary");
        }
        StackPanel buttons = DialogFooter.Listed([.. answers]);
        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            },
        }, DialogFooter.Bar(buttons));
    }
}
