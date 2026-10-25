using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Asks before something that cannot be undone: what happens, what it happens to when there is a list of it
/// (Rollback's files), the button that does it and the one that does not (Cancel, or a choice of its own, as File Cache
/// Conflict's Keep Memory Changes), closing with whether the person agreed. Escape answers no.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string text, string yes, string no = "Cancel", Control? detail = null)
    {
        Button confirm = new() { Content = yes, IsDefault = true, Classes = { "primary" } };
        Button cancel = new() { Content = no, IsCancel = true };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        DialogFooter.Question(this, title, [DialogFooter.Paragraph(text), .. detail is null ? [] : new[] { detail }], DialogFooter.Of(confirm, cancel));
    }
}
