using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>A tree's keys pressed as a person presses them, the same in every tree window: on a row with rows under it,
/// Left closes it if it is open, Right opens it, Down goes to the first row under it, Left goes back up to it, Left again
/// closes it, and Enter, on a row that stands for nothing to open, opens it again.</summary>
internal static class TreeKeysCheck
{
    /// <summary>Presses the keys on <paramref name="row"/> of the list showing <paramref name="rows"/>.</summary>
    public static void Press<T>(TopLevel window, IEnumerable rows, T row, string tree, bool enterOpens = true)
        where T : class, ITreeRow =>
        Press(window, rows, row, r => r, tree, enterOpens);

    /// <summary>Presses the keys on <paramref name="row"/> of the list showing <paramref name="rows"/>, whose items' rows
    /// <paramref name="rowOf"/> reads; <paramref name="enterOpens"/> false where Enter opens what the row stands for.</summary>
    public static void Press<T>(TopLevel window, IEnumerable rows, T row, Func<T, ITreeRow> rowOf, string tree, bool enterOpens = true)
        where T : class
    {
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, rows));
        Assert.True(rowOf(row).Expands, $"The row chosen in {tree} has no rows under it to open");
        Choose(list, row);
        if (rowOf(row).IsExpanded)
        {
            Ui.Press(window, "Left");
            Assert.False(Current(list, row, rowOf).IsExpanded, $"Left on an open row of {tree} did not close it");
            row = Chosen(list, row);
        }
        Ui.Press(window, "Right");
        row = Chosen(list, row);
        Assert.True(rowOf(row).IsExpanded, $"Right on a closed row of {tree} did not open it");
        int at = list.Items.IndexOf(row);
        Assert.True(at + 1 < list.ItemCount && list.Items[at + 1] is T first && rowOf(first).Depth > rowOf(row).Depth,
            $"Right opened a row of {tree} with no rows under it shown");
        Ui.Press(window, "Right");
        Assert.Same(row, list.SelectedItem);
        Ui.Press(window, "Down");
        Assert.True(list.SelectedIndex == at + 1, $"Down on an open row of {tree} did not go to the first row under it");
        if (list.SelectedItem is T child && rowOf(child).IsExpanded)
        {
            Ui.Press(window, "Left");
        }
        Ui.Press(window, "Left");
        Assert.True(ReferenceEquals(list.SelectedItem, row), $"Left on a closed row of {tree} did not go up to the row it sits under");
        Assert.True(list.ContainerFromItem(row)?.IsFocused == true, $"Left up to a row of {tree} left the keys on the row it came from");
        Ui.Press(window, "Left");
        row = Chosen(list, row);
        Assert.False(rowOf(row).IsExpanded, $"Left on an open row of {tree} did not close it");
        Assert.False(at + 1 < list.ItemCount && list.Items[at + 1] is T next && rowOf(next).Depth > rowOf(row).Depth,
            $"Left closed a row of {tree} whose rows still show under it");
        if (enterOpens)
        {
            Ui.Press(window, "Enter");
            row = Chosen(list, row);
            Assert.True(rowOf(row).IsExpanded, $"Enter on a row of {tree} that stands for nothing to open did not open it");
        }
    }

    /// <summary>Chooses a row and gives it the keys, as a click on it does.</summary>
    private static void Choose<T>(ListBox list, T row)
        where T : class
    {
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        list.UpdateLayout();
        list.ContainerFromItem(row)?.Focus(NavigationMethod.Pointer);
        Ui.Settle();
        Assert.True(list.ContainerFromItem(row)?.IsFocused == true, "The row chosen did not take the keys");
    }

    /// <summary>The row chosen now: a tree that draws a row again in its place (its chevron turned) chooses the new one.</summary>
    private static T Chosen<T>(ListBox list, T was)
        where T : class =>
        list.SelectedItem as T ?? was;

    private static ITreeRow Current<T>(ListBox list, T was, Func<T, ITreeRow> rowOf)
        where T : class =>
        rowOf(Chosen(list, was));
}
