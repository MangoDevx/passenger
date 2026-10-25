using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A folder's field with its Browse... button after it, the field filling the row, in reading order for Tab
/// too.</summary>
public static class BrowseRow
{
    public static Grid Of(TextBox field, Button browse)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(browse, 1);
        row.Children.Add(field);
        row.Children.Add(browse);
        return row;
    }
}
