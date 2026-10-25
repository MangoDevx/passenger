using Avalonia.Controls;
using Passenger.Xaml.Features;

namespace Passenger.App.Settings;

/// <summary>The Editor, Code Style, XAML page: the XML formatter's options, which Reformat Code and Code Cleanup lay
/// out Avalonia's and WPF's XAML by, then the attribute order and empty-element style. A file's .editorconfig says over
/// each, by Rider's keys.</summary>
public sealed partial class SettingsDialog
{
    private void AddXamlCodeStyle()
    {
        const string Words = "xaml axaml xml code style formatting reformat format layout wpf avalonia";
        XamlFormatSettings now = Working.XamlFormat;
        void Set(Func<XamlFormatSettings, XamlFormatSettings> change) => Change(s => s with { XamlFormat = change(s.XamlFormat) });

        Add(XamlCodeStylePage, "Attributes format", Choices(AttributeStyles, (int)now.AttributeStyle, i => Set(x => x with { AttributeStyle = (XamlAttributeStyle)i })),
            $"{Words} attributes format separate lines single line first attribute do not touch",
            "Reformat Code lays out .axaml and .xaml by these, Rider's own. A file's .editorconfig overrides each by Rider's key: "
            + "xml_attribute_style, xml_attribute_indent, xml_indent_child_elements, max_line_length and the rest, with "
            + "xaml_enable_attribute_reordering, xaml_attribute_ordering_rule_groups and xaml_remove_ending_tag_of_empty_element.");
        Add(XamlCodeStylePage, "Attributes indenting", Choices(AttributeIndents, (int)now.AttributeIndent, i => Set(x => x with { AttributeIndent = (XamlAttributeIndent)i })),
            $"{Words} attributes indenting indent align first attribute single double");
        Add(XamlCodeStylePage, "Spaces around '=' in attribute", Check(now.SpacesAroundEquals, v => Set(x => x with { SpacesAroundEquals = v })), $"{Words} spaces equals");
        Add(XamlCodeStylePage, "Space after last attribute", Check(now.SpaceAfterLastAttribute, v => Set(x => x with { SpaceAfterLastAttribute = v })), $"{Words} space tag header");
        Add(XamlCodeStylePage, "Space before '/>'", Check(now.SpaceBeforeSelfClosing, v => Set(x => x with { SpaceBeforeSelfClosing = v })), $"{Words} space self closing empty");
        Add(XamlCodeStylePage, "Keep existing line breaks", Check(now.KeepLineBreaks, v => Set(x => x with { KeepLineBreaks = v })), $"{Words} line breaks keep user");
        Add(XamlCodeStylePage, "Maximum blank lines between tags", Number(now.MaxBlankLines, 0, XamlFormatSettings.MostBlankLines, 1, v => Set(x => x with { MaxBlankLines = (int)v })),
            $"{Words} blank lines empty");
        Add(XamlCodeStylePage, "Place multiline elements on a new line", Check(now.MultilineElementsOnNewLine, v => Set(x => x with { MultilineElementsOnNewLine = v })),
            $"{Words} tags line breaks multiline");
        Add(XamlCodeStylePage, "Place single-line elements on a new line", Check(now.SinglelineElementsOnNewLine, v => Set(x => x with { SinglelineElementsOnNewLine = v })),
            $"{Words} tags line breaks single line");
        Add(XamlCodeStylePage, "Line breaks after start-tag and before end-tag in elements that contain other elements and no text",
            Check(now.BreaksInsideElementsWithChildren, v => Set(x => x with { BreaksInsideElementsWithChildren = v })), $"{Words} tag content line breaks children");
        Add(XamlCodeStylePage, "Line breaks after start-tag and before end-tag in multiline elements",
            Check(now.BreaksInsideMultilineElements, v => Set(x => x with { BreaksInsideMultilineElements = v })), $"{Words} tag content line breaks multiline");
        Add(XamlCodeStylePage, "Indent inside element that does not contain text", Choices(ContentIndents, (int)now.ChildIndent, i => Set(x => x with { ChildIndent = (XamlContentIndent)i })),
            $"{Words} indent children elements tag content");
        Add(XamlCodeStylePage, "Indent inside element that contains text", Choices(ContentIndents, (int)now.TextIndent, i => Set(x => x with { TextIndent = (XamlContentIndent)i })),
            $"{Words} indent text tag content");
        Add(XamlCodeStylePage, "Blank line after processing instructions", Check(now.BlankLineAfterProcessingInstructions, v => Set(x => x with { BlankLineAfterProcessingInstructions = v })),
            $"{Words} processing instructions xml declaration blank line");
        Add(XamlCodeStylePage, "Wrap tags and processing instructions", Check(now.WrapTags, v => Set(x => x with { WrapTags = v })), $"{Words} wrap long lines tags margin");
        Add(XamlCodeStylePage, "Wrap before and after elements", Check(now.WrapAroundElements, v => Set(x => x with { WrapAroundElements = v })),
            $"{Words} wrap long lines elements margin");
        Add(XamlCodeStylePage, "Right margin (columns)", Number(now.RightMargin, 0, XamlFormatSettings.WidestMargin, 1, v => Set(x => x with { RightMargin = (int)v })),
            $"{Words} right margin columns wrap hard max line length");

        TextBox order = new() { Classes = { "search" }, Text = Groups(now.AttributeOrder), AcceptsReturn = true, Width = 420, Height = 210, IsEnabled = now.ReorderAttributes };
        // A group longer than the box scrolls into view rather than being cut at its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(order, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        order.TextChanged += (_, _) => Set(x => x with { AttributeOrder = string.Join("; ", (order.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) });
        Add(XamlCodeStylePage, "Reorder attributes", Check(now.ReorderAttributes, v =>
        {
            Set(x => x with { ReorderAttributes = v });
            order.IsEnabled = v;
        }), $"{Words} attributes order ordering sort reorder xaml styler x:name grid",
            "XAML Styler's Enable attribute reordering: each attribute in the group below that names it most nearly, a group to a line.");
        Add(XamlCodeStylePage, "Attribute ordering rule groups", order, $"{Words} attributes order ordering rule groups xaml styler");
        Add(XamlCodeStylePage, "Remove ending tag of empty element", Check(now.CollapseEmptyElements, v => Set(x => x with { CollapseEmptyElements = v })),
            $"{Words} empty element end tag self closing collapse xaml styler");
    }

    /// <summary>A rule groups' text as the box shows it, a group to a line.</summary>
    private static string Groups(string order) =>
        string.Join('\n', order.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static ComboBox Choices(string[] names, int chosen, Action<int> changed)
    {
        ComboBox box = new() { ItemsSource = names, SelectedIndex = Math.Clamp(chosen, 0, names.Length - 1), Classes = { "form" } };
        box.SelectionChanged += (_, _) => changed(Math.Max(0, box.SelectedIndex));
        return box;
    }
}
