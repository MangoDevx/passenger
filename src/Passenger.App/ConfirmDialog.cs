using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Asks before something that cannot be undone: what happens, what it happens to when there is a list of it
/// (Rollback's files), the button that does it and the one that does not (Cancel, or a choice of its own, as File Cache
/// Conflict's Keep Memory Changes), closing with whether the person agreed. Escape answers no.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string text, string yes, string no = "Cancel", Control? detail = null)
    {
        Title = title;
        Width = detail is null ? 420 : 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Button confirm = new() { Content = yes, IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = no, IsCancel = true };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        StackPanel body = new()
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (detail is not null)
        {
            body.Children.Add(detail);
        }
        Content = DialogFooter.Page(body, DialogFooter.Bar(DialogFooter.Of(confirm, cancel)));
    }
}
