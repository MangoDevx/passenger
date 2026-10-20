using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using System.Text.RegularExpressions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Rules the whole window keeps, each decided once in a shared piece (#213) and held here across what the tests
/// open, as <see cref="DialogButtonsTests.Check"/> holds every dialog's buttons: a menu's current value checked and still
/// enabled (MenuChoice); every tool window's tabs one look (ToolTabs, Shell.axaml's panelTab); every popup's width from
/// one scale, its title one size and its rows one height (PopupFrame); a menu opening against its widget; Rider's
/// scrollbars, thin and arrowless; the open editor tab's ×; Rider's focus ring; and the window at half a 1366-wide screen
/// with nothing cut that Rider would not cut, a stripe putting what has no room behind More. Each rule that a look could
/// break is held under both looks in one window, the look switched in place as Settings switches it.</summary>
public sealed class UiRulesTests : IClassFixture<UiRulesTests.Project>
{
    private readonly Project project;

    public UiRulesTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>The looks a window's rules are held under, in the order a window wears them: Passenger's and Harbour's, whose
    /// shapes are every other theme's (tools/themes.py gives each one or the other's), the rest's colours held by the rules
    /// of tokens alone (<see cref="EveryLook"/>) and their grounds' steps checked as tools/themes.py writes them.</summary>
    private static readonly Look[] Looks = [Look.Passenger, Look.Harbour];

    [AvaloniaFact]
    public void A_menus_current_value_is_checked_with_Rider_s_tick_and_can_still_be_chosen()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello"), "the project's run configuration");
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Current(window, "BranchButton", "main", "the branch menu");
                Current(window, "RunConfigButton", "Hello", "the run configuration menu");
                Current(window, "LineSeparatorButton", "LF - Unix and macOS (\\n)", "the line separator menu");
                Current(window, "EncodingButton", "UTF-8", "the encoding menu");
            });
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_open_editor_tab_shows_its_close_button_as_Rider_s_does_and_the_others_only_under_the_pointer()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            window.OpenFile(project.PathOf("Notes.txt"));
            Ui.Settle();
            UnderEachLook(window, () =>
            {
                Border[] tabs = [.. window.EditorArea.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tab") && b.IsEffectivelyVisible)];
                Assert.Equal(2, tabs.Length);
                foreach (Border tab in tabs)
                {
                    bool open = tab.Classes.Contains("active");
                    Button close = tab.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));
                    Assert.True(close.Opacity == (open ? 1 : 0),
                        $"The {(open ? "open" : "other")} tab's × has an opacity of {close.Opacity}, where Rider shows it on the open tab alone until the pointer is over another");
                }
            });
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_tool_windows_tabs_have_one_look_the_open_one_underlined_under_a_larger_title()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => ToolWindowTabs(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void ToolWindowTabs(MainWindow window)
    {
        Color accent = Brush(window, "Passenger.Accent");
        (string Where, double Height, double Size, Thickness Padding, Thickness Border)? first = null;
        int seen = 0;
        foreach (string action in (string[])[AppActions.ShowProblems, AppActions.ShowGit, AppActions.ShowDebug, AppActions.ShowTerminal, AppActions.ShowLocalHistory])
        {
            Assert.True(window.Perform(action), $"{action} did not run");
            Ui.Settle();
            if (action == AppActions.ShowTerminal)
            {
                Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            }
            Assert.False(ToolWindowParts(window).OfType<Border>().Any(b => b.Classes.Contains("tab")),
                $"{action}'s window has an editor tab's look among its tabs, where every tool window's tab is a panelTab");
            // Its title larger than every word in it (14 over the window's 13), under a header of one height.
            foreach (TextBlock title in ToolWindowParts(window).OfType<TextBlock>().Where(t => t.Classes.Contains("toolTitle")))
            {
                Control header = title.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("toolHeader"));
                Assert.True(title.FontSize == 14 && title.FontWeight == FontWeight.SemiBold && Math.Abs(header.Bounds.Height - 38) < 0.5,
                    $"{action}'s window has its title '{title.Text}' in {title.FontSize} pt {title.FontWeight} under a header {header.Bounds.Height:0.#} tall, "
                    + "where every tool window's title is 14 pt semibold under a header 38 tall");
            }
            // Every row a person picks from is one height, as Rider's lists and trees are.
            foreach (ListBoxItem row in ToolWindowParts(window).OfType<ListBoxItem>().Where(r => r.Bounds.Height > 0))
            {
                Assert.True(Math.Abs(row.Bounds.Height - ListRow.Height) < 0.5,
                    $"A row of {action}'s window is {row.Bounds.Height:0.#} px tall, where every list's row is {ListRow.Height}");
            }
            // Read while the window is shown: one hidden leaves the tree, and its tabs their styles with it.
            foreach (ToggleButton tab in ToolWindowParts(window).OfType<ToggleButton>().Where(t => t.Classes.Contains("panelTab")))
            {
                seen++;
                string where = $"{action}'s tab '{Words(tab)}'";
                first ??= (where, tab.Bounds.Height, tab.FontSize, tab.Padding, tab.BorderThickness);
                Assert.True(tab.Bounds.Height == first.Value.Height && tab.FontSize == first.Value.Size && tab.Padding == first.Value.Padding
                    && tab.BorderThickness == first.Value.Border && tab.BorderThickness.Bottom > 0 && tab.CornerRadius == default,
                    $"{where} is {tab.Bounds.Height:0.#} px tall in {tab.FontSize} pt with padding {tab.Padding} and a border of {tab.BorderThickness}, "
                    + $"where {first.Value.Where} is {first.Value.Height:0.#} px in {first.Value.Size} pt: tool windows' tabs differ");
                if (tab.IsChecked == true)
                {
                    ContentPresenter frame = tab.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                    Assert.True(frame.BorderBrush is ISolidColorBrush underline && underline.Color == accent,
                        $"{where} is open and not underlined in the accent, as Rider's tool window tabs are");
                }
            }
        }
        Assert.True(seen >= 5, $"Only {seen} tool window tabs were found in Problems, Git, Debug and the Terminal");
    }

    [AvaloniaFact]
    public void Every_popup_takes_a_width_of_the_scale_one_title_size_and_rows_of_one_height()
    {
        MainWindow window = Open(1400, 900);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => Popups(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Popups(MainWindow window)
    {
        List<(string What, Border Frame)> seen = [];
        void Popup(string what, Action open)
        {
            open();
            Ui.Settle();
            Ui.WaitFor(() => window.IsPopupOpen, what);
            Border frame = window.Popup!.GetSelfAndVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("popup"));
            Assert.True(PopupFrame.Widths.Any(w => Math.Abs(frame.Bounds.Width - w) < 0.5),
                $"{what} is {frame.Bounds.Width:0.#} px wide, not one of the popups' widths ({string.Join(", ", PopupFrame.Widths)})");
            foreach (ListBoxItem row in frame.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible))
            {
                Assert.True(Math.Abs(row.Bounds.Height - ListRow.Height) < 0.5,
                    $"A row of {what} is {row.Bounds.Height:0.#} px tall, where every popup's row is {ListRow.Height}");
            }
            seen.Add((what, frame));
            Ui.Press(window, "ESCAPE");
        }
        Popup("Find Action", () => window.Perform(AppActions.FindAction));
        Popup("Go to File", () => window.Perform(AppActions.GoToFile));
        Popup("Go to Symbol", () => window.Perform(AppActions.GoToSymbol));
        Popup("Recent Files", () => window.ShowRecentFiles());
        Popup("Recent Locations", () => window.Perform(AppActions.RecentLocations));
        Popup("Find in Files", () => window.ShowFindInFiles(false));
        Popup("Stash Changes", () => window.Perform(AppActions.Stash));
        Popup("IDE Internal Errors", () => window.ShowErrors());

        TextBlock[] titles = [.. seen.SelectMany(s => s.Frame.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("popupTitle")))];
        Assert.True(titles.Length >= seen.Count - 2, $"Only {titles.Length} of {seen.Count} popups have a title of the popups' look");
        Assert.True(titles.All(t => t.FontSize == titles[0].FontSize && t.FontWeight == titles[0].FontWeight),
            "Popups' titles differ in size or weight: " + string.Join(", ", titles.Select(t => $"'{t.Text}' {t.FontSize} {t.FontWeight}")));
    }

    [AvaloniaFact]
    public void Every_scrollbar_is_Rider_s_thin_and_arrowless_and_a_sideways_one_shows_only_for_what_is_wider()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            window.Perform(AppActions.ShowProblems);
            Ui.Settle();
            UnderEachLook(window, () => Scrollbars(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Scrollbars(MainWindow window)
    {
        ScrollBar[] bars = [.. window.GetVisualDescendants().OfType<ScrollBar>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0)];
        Assert.Contains(bars, b => b.Orientation == Orientation.Vertical && b.FindAncestorOfType<Passenger.Editor.CodeEditor>() is not null);
        foreach (ScrollBar bar in bars)
        {
            string where = bar.FindAncestorOfType<Passenger.Editor.CodeEditor>() is not null ? "The editor's" : $"A {bar.Parent?.GetType().Name}'s";
            Assert.False(bar.GetVisualDescendants().OfType<RepeatButton>().Any(b => b.Name is "PART_LineUpButton" or "PART_LineDownButton"),
                $"{where} {bar.Orientation} scrollbar has arrows, where Rider's has none");
            double across = bar.Orientation == Orientation.Vertical ? bar.Bounds.Width : bar.Bounds.Height;
            Assert.True(across <= 14, $"{where} {bar.Orientation} scrollbar is {across:0.#} px across, where Rider's are thin");
            Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            double thumbAcross = bar.Orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height;
            Assert.True(thumb.CornerRadius.TopLeft > 0 && thumbAcross < across, $"{where} {bar.Orientation} scrollbar's thumb is not Rider's thin rounded one");
            if (bar.Orientation == Orientation.Horizontal && bar.TemplatedParent is ScrollViewer viewer)
            {
                Assert.True(viewer.Extent.Width > viewer.Viewport.Width, $"{where} sideways scrollbar shows for what is no wider than its view");
            }
        }
    }

    [AvaloniaFact]
    public void At_half_a_1366_wide_screen_the_window_cuts_nothing_and_a_stripe_too_short_puts_the_rest_behind_More()
    {
        MainWindow window = Open(683, 768);
        try
        {
            window.OpenFile(project.PathOf("Program.cs"));
            Ui.Settle();
            UnderEachLook(window, () => HalfScreen(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void HalfScreen(MainWindow window)
    {
        // Each look from half a 1366-wide screen, as the last left it shorter and narrower.
        window.Width = 683;
        window.Height = 768;
        Ui.Settle();
        Assert.True(Math.Abs(window.Bounds.Width - 683) < 0.5, $"The window is {window.Bounds.Width:0.#} wide at half a 1366-wide screen: its least width is wider");
        Control end = Named<Control>(window, "ToolbarEnd");
        Control start = Named<Control>(window, "ToolbarStart");
        foreach (Button button in end.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
        {
            Rect at = Placed(window, button);
            Assert.True(at.Right <= window.Bounds.Width + 0.5, $"The toolbar's {ToolTip.GetTip(button)} is cut off at the window's right edge");
        }
        Assert.True(Placed(window, start).Right <= Placed(window, end).Left + 0.5,
            "The toolbar's project and branch widgets run under its run widget, where Rider's give way, ending in an ellipsis");
        TextBlock project = Named<TextBlock>(window, "ProjectName");
        Assert.True(project.TextTrimming == TextTrimming.CharacterEllipsis, "The project's name is cut rather than ending in an ellipsis");
        StripeButtonsFit(window);
        Assert.True(window.EditorArea.Bounds.Width > 200, $"The editor keeps {window.EditorArea.Bounds.Width:0.#} px beside the tool windows");

        // NuGet's search, feeds, Prerelease and Refresh stand inside its window, the search and the count giving way.
        Assert.True(window.Perform(AppActions.ShowNuGet), "NuGet did not show");
        Ui.Settle();
        CheckBox prerelease = window.DockHost.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == "Prerelease" && c.IsEffectivelyVisible);
        Rect nuget = Placed(window, prerelease.FindAncestorOfType<Passenger.App.ToolWindows.NuGetView>()!);
        foreach (Control part in ((Panel)prerelease.Parent!).Children.Where(c => c.IsVisible))
        {
            Rect at = Placed(window, part);
            Assert.True(at.Right <= nuget.Right + 0.5 && at.Width > 0, $"NuGet's {part.GetType().Name} is cut off at its window's right edge ({at.Right:0.#} past {nuget.Right:0.#})");
        }

        // What a tool window says while it has nothing to show wraps inside it, never running past its edges.
        foreach (string action in (string[])[AppActions.ShowTests, AppActions.ShowFind, AppActions.ShowDebug, AppActions.ShowProfiler, AppActions.ShowLocalHistory])
        {
            Assert.True(window.Perform(action), $"{action} did not run");
            Ui.Settle();
            foreach (TextBlock empty in window.DockHost.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("emptyText") && t.IsEffectivelyVisible))
            {
                Rect text = Placed(window, empty);
                Rect room = Placed(window, (Visual)empty.Parent!);
                Assert.True(text.Left >= room.Left - 0.5 && text.Right <= room.Right + 0.5,
                    $"After {action}, '{empty.Text}' runs past its pane ({text.Left:0.#} to {text.Right:0.#} in {room.Left:0.#} to {room.Right:0.#})");
            }
        }

        // A window too short for every stripe button puts the rest behind More, which lists them.
        window.Height = 500;
        window.Width = MainWindow.SmallestWidth;
        Ui.Settle();
        StripeButtonsFit(window);
        ToggleButton more = window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => ToolTip.GetTip(b) as string == "More Tool Windows" && b.IsEffectivelyVisible);
        string[] hidden = [.. window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Tag is string && !b.IsVisible).Select(b => (string)b.Tag!)];
        Assert.NotEmpty(hidden);
        Point centre = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Ui.Settle();
        ContextMenu menu = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)
            ?? throw new Xunit.Sdk.XunitException("More opened no menu of the tool windows it holds");
        Assert.Equal(hidden.Length, menu.Items.Count);
        menu.Close();
        Ui.Settle();
    }

    /// <summary>A window opens inside the working area of a screen smaller than its default size, as Rider's does: the
    /// Windows VM's (1500 by 830 at 200%, under its taskbar) opened one partly off the screen, its close button and the
    /// Debug window's foot under the taskbar. Centred when new; one placed past the area's edge is brought onto it.</summary>
    [AvaloniaFact]
    public void A_window_opens_inside_a_small_screens_working_area_centred_and_one_placed_past_its_edge_is_brought_onto_it()
    {
        PixelRect area = new(0, 0, 3000, 1660);
        MainWindow window = new() { Autosaves = false };
        MainWindow placed = new() { Autosaves = false, Width = 2400, Height = 1400, Position = new PixelPoint(2900, 1500) };
        try
        {
            window.FitWithin(area, 2, keepPlace: false);
            window.Show();
            placed.FitWithin(area, 2, keepPlace: true);
            placed.Show();
            Ui.Settle();
            foreach ((MainWindow shown, string what) in (IEnumerable<(MainWindow, string)>)[(window, "A new window"), (placed, "A window placed past the screen's edge")])
            {
                PixelRect at = new(shown.Position, PixelSize.FromSize(shown.ClientSize, 2));
                Assert.True(area.Contains(at), $"{what} opened at {at}, {shown.ClientSize} large, past the screen's working area {area}");
                Assert.True(shown.ClientSize.Width >= MainWindow.SmallestWidth && shown.ClientSize.Height >= MainWindow.SmallestHeight,
                    $"{what} opened {shown.ClientSize} large, under the window's smallest");
            }
            PixelRect centred = new(window.Position, PixelSize.FromSize(window.ClientSize, 2));
            Assert.True(Math.Abs(centred.Center.X - area.Center.X) <= 2 && Math.Abs(centred.Center.Y - area.Center.Y) <= 2,
                $"A new window opened at {centred}, not centred on the screen's working area {area}");
        }
        finally
        {
            window.Close();
            placed.Close();
        }
    }

    /// <summary>Every corner Passenger rounds is one of the look's radii (Shell.axaml's kindBadge, tag and box on them), so
    /// either look's reach it: a Git log's ref tags (the project's v1.0) and the Keymap page's keystroke boxes had 3 and 4
    /// written in.</summary>
    [AvaloniaFact]
    public void Every_rounded_corner_is_one_of_the_looks_radii()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            Assert.True(window.Perform(AppActions.ShowGit), "Git did not show");
            Ui.WaitFor(() => window.DockHost.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.EndsWith("v1.0", StringComparison.Ordinal) == true && t.IsEffectivelyVisible), "the Git log's v1.0 tag");
            UnderEachLook(window, () =>
            {
                Radii(window, "the window with the Git log");
                SettingsDialog settings = window.ShowSettings();
                try
                {
                    settings.Page = SettingsDialog.KeymapPage;
                    Ui.Settle();
                    Radii(settings, "Settings' Keymap page");
                    settings.Page = SettingsDialog.McpPage;
                    Ui.Settle();
                    Radii(settings, "Settings' MCP Server page");
                }
                finally
                {
                    settings.Close();
                    Ui.Settle();
                }
            });
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Holds every Border Passenger made in a window (not a control template's) to the look's radii.</summary>
    private static void Radii(Window window, string what)
    {
        HashSet<double> radii = [0];
        foreach (string key in (string[])["ControlRadius", "ToolRadius", "PopupRadius", "CardRadius", "BadgeRadius", "ChipRadius", "TabRadius"])
        {
            if (window.TryFindResource($"Passenger.{key}", window.ActualThemeVariant, out object? found) && found is CornerRadius corners)
            {
                radii.UnionWith([corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft]);
            }
        }
        foreach (Border border in window.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.TemplatedParent is null))
        {
            CornerRadius r = border.CornerRadius;
            Assert.True(((double[])[r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft]).All(radii.Contains),
                $"In {what}, a {string.Join(".", ["Border", .. border.Classes.Where(c => !c.StartsWith(':'))])} round '{(border.Child as TextBlock)?.Text ?? border.Child?.GetType().Name}' "
                + $"has corners of {r}, none of the look's radii ({string.Join(", ", radii.Order())})");
        }
    }

    /// <summary>The Debug window's toolbar down its left keeps every button inside the window, as Rider's does: at 1400 by
    /// 880 its last went under the status bar. A window too short for them all puts the rest behind More, which lists them.</summary>
    [AvaloniaFact]
    public void The_debug_windows_toolbar_fits_inside_it_and_puts_what_has_no_room_behind_More()
    {
        MainWindow window = Open(1400, 880);
        try
        {
            Assert.True(window.Perform(AppActions.ShowDebug), "Debug did not show");
            Ui.Settle();
            Passenger.App.ToolWindows.DebugView debug = window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.DebugView>().First(v => v.IsEffectivelyVisible);
            Passenger.App.ToolWindows.ToolColumn tools = debug.GetVisualDescendants().OfType<Passenger.App.ToolWindows.ToolColumn>().Single();
            // A button with no room is put above the column, which clips it away.
            static bool Drawn(Control button) => button.IsVisible && button.Bounds.Bottom > 0;
            void Fits()
            {
                Rect room = Placed(window, debug);
                foreach (Button button in tools.Children.OfType<Button>().Where(Drawn))
                {
                    Rect at = Placed(window, button);
                    Assert.True(at.Bottom <= room.Bottom + 0.5, $"The Debug window's {ToolTip.GetTip(button)} reaches {at.Bottom:0.#}, past the window's foot at {room.Bottom:0.#}");
                }
            }
            Fits();
            Assert.Equal(tools.Overflowing.Count > 0, Drawn(tools.More));

            // The bottom tool windows dragged short, as a small screen leaves them.
            window.DockHost.Apply(window.DockHost.Capture() with { BottomHeight = 150 });
            Ui.Settle();
            Fits();
            Assert.NotEmpty(tools.Overflowing);
            Assert.True(Drawn(tools.More), "The Debug window's toolbar has buttons with no room and no More to reach them");
            string[] cut = [.. tools.Overflowing.Select(b => (string)ToolTip.GetTip(b)!)];
            Point centre = tools.More.TranslatePoint(new Point(tools.More.Bounds.Width / 2, tools.More.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Ui.Settle();
            ContextMenu menu = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(m => m.IsOpen)
                ?? throw new Xunit.Sdk.XunitException("More opened no menu of the buttons it holds");
            Assert.Equal(cut, menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void At_the_smallest_width_the_status_bars_path_never_draws_under_its_widgets()
    {
        MainWindow window = Open(MainWindow.SmallestWidth, MainWindow.SmallestHeight);
        try
        {
            window.OpenFile(project.PathOf(Project.Deep));
            Ui.Settle();
            Rect widgets = Placed(window, (Visual)Named<TextBlock>(window, "CaretText").Parent!);
            Button[] crumbs = [.. Named<Control>(window, "BreadcrumbHost").GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
            Assert.NotEmpty(crumbs);
            foreach (Button crumb in crumbs)
            {
                Rect seen = Visible(window, crumb);
                Assert.True(seen.Width <= 0 || seen.Right <= widgets.Left + 0.5,
                    $"The status bar's path shows {crumb.Content} under its widgets ({seen.Left:0.#} to {seen.Right:0.#}, the widgets from {widgets.Left:0.#})");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The status bar's words are a link while they have a place to go (an internal error's, IDE Internal Errors):
    /// the hand, a key's reach, and the link's colour underlined under the pointer, from the shared style in every look;
    /// words that go nowhere stay the bar's muted words under the pointer, and the keys pass them by.</summary>
    [AvaloniaFact]
    public void The_status_bars_words_are_a_link_under_the_pointer_only_while_they_have_a_place_to_go()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () =>
            {
                Button said = Named<Button>(window, "StatusMessage");
                TextBlock words = Named<TextBlock>(window, "StatusText");
                Ui.Press(window, Ui.Shortcut(AppActions.ApplyHotReloadChanges));
                Hover(window, said);
                Assert.True(window.StatusPlace is null && !said.Focusable && said.Cursor is null && Colour(words) == Brush(window, "Passenger.TextMuted") && !Underlined(words),
                    $"\"{window.Status}\", which goes nowhere, is drawn in {Colour(words)} {(Underlined(words) ? "underlined" : "")} under the pointer with the cursor {said.Cursor}, "
                    + "where the status bar's words that go nowhere stay its muted words");

                Avalonia.Threading.Dispatcher.UIThread.Post(() => throw new InvalidOperationException("A status bar link's own error"));
                try
                {
                    Ui.Settle();
                    Assert.True(window.Status == "IDE error: A status bar link's own error" && window.StatusPlace is not null, $"The status bar says \"{window.Status}\" with no place, not the IDE error going to IDE Internal Errors");
                    window.MouseMove(new Point(1, 1));
                    Hover(window, said);
                    Assert.True(said.Focusable && said.Cursor?.ToString() == "Hand" && Colour(words) == Brush(window, "Passenger.Link") && Underlined(words),
                        $"\"{window.Status}\", which goes to IDE Internal Errors, is drawn in {Colour(words)} {(Underlined(words) ? "underlined" : "not underlined")} under the pointer "
                        + $"with the cursor {said.Cursor}, {(said.Focusable ? "" : "out of the keys' reach, ")}not a link in the link's colour ({Brush(window, "Passenger.Link")}) underlined, with the hand");
                    said.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Ui.Settle();
                    Assert.True(window.Popup is ErrorsPopup, $"A click on \"{window.Status}\" showed {window.Popup?.GetType().Name ?? "nothing"}, not IDE Internal Errors");
                    Ui.Press(window, "ESCAPE");
                    window.MouseMove(new Point(1, 1));
                }
                finally
                {
                    // Thrown on purpose: the check that fails a test over a caught error is not to see it.
                    IdeErrors.Clear();
                }
            });
        }
        finally
        {
            window.Close();
        }

        static void Hover(MainWindow window, Control control)
        {
            window.MouseMove(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);
            Ui.Settle();
        }

        static Color? Colour(TextBlock words) => (words.Foreground as ISolidColorBrush)?.Color;

        static bool Underlined(TextBlock words) => words.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
    }

    /// <summary>What of a control is drawn: its place cut to each ancestor that clips what it holds.</summary>
    private static Rect Visible(Visual window, Visual control)
    {
        Rect seen = Placed(window, control);
        for (Visual? up = control.GetVisualParent(); up is not null && up != window; up = up.GetVisualParent())
        {
            if (up.ClipToBounds)
            {
                seen = seen.Intersect(Placed(window, up));
            }
        }
        return seen;
    }

    [AvaloniaFact]
    public void Focus_is_Rider_s_rounded_ring_centred_on_what_has_the_keys_a_check_box_s_round_its_box_alone()
    {
        string other = TestFolders.Create("ui-rules-other-");
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () => FocusRings(window, other));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(other);
        }
    }

    private static void FocusRings(MainWindow window, string other)
    {
        window.Open(other);
        Ui.Settle();
        Window dialog = window.Asking ?? throw new Xunit.Sdk.XunitException("Opening another folder over one open did not ask where");
        bool sawBox = false;
        bool sawButton = false;
        for (int i = 0; i < 8 && !(sawBox && sawButton); i++)
        {
            Ui.Press(dialog, "TAB");
            if (dialog.FocusManager?.GetFocusedElement() is not Control focused || AdornerLayer.GetAdornerLayer(focused) is not { } layer)
            {
                continue;
            }
            FocusRing ring = layer.Children.OfType<FocusRing>().SingleOrDefault(r => r.IsVisible)
                ?? throw new Xunit.Sdk.XunitException($"{focused.GetType().Name} has the keys from Tab and no focus ring round it");
            (Rect edge, double radius) = ring.Surrounds();
            Point centre = ring.TranslatePoint(edge.Center, dialog)!.Value;
            if (focused is CheckBox box)
            {
                Control square = box.GetVisualDescendants().OfType<Control>().First(c => c.Name == "NormalRectangle");
                Point boxCentre = square.TranslatePoint(new Point(square.Bounds.Width / 2, square.Bounds.Height / 2), dialog)!.Value;
                Assert.True(Math.Abs(edge.Width - square.Bounds.Width) < 0.5 && Off(centre, boxCentre) < 0.5,
                    $"The check box's focus ring surrounds {edge.Width:0.#} px centred {Off(centre, boxCentre):0.#} px off its box, where Rider's rings the box alone");
                sawBox = true;
            }
            else if (focused is Button button)
            {
                Point buttonCentre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
                Assert.True(Math.Abs(edge.Width - button.Bounds.Width) < 0.5 && Off(centre, buttonCentre) < 0.5 && radius == button.CornerRadius.TopLeft && radius > 0,
                    $"'{button.Content}''s focus ring is not centred on it with its corners, as Rider's rounded ring is");
                sawButton = true;
            }
        }
        Assert.True(sawBox && sawButton, "Tab never reached the dialog's check box and a button");
        dialog.Close();
        Ui.Settle();
    }

    [AvaloniaFact]
    public void The_windows_areas_are_told_apart_by_shade_with_a_line_only_where_two_tool_windows_meet()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            window.OpenFile(project.PathOf("Notes.txt"));
            Assert.True(window.Perform(AppActions.ShowProblems), "Problems did not show");
            Assert.True(window.Perform(AppActions.ShowStructure), "Structure did not show");
            Ui.Settle();
            UnderEachLook(window, () => Areas(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Areas(MainWindow window)
    {
        Color frame = Brush(window, "Passenger.Frame");
        Color panel = Brush(window, "Passenger.Panel");
        Color editor = Brush(window, "Passenger.Editor.Background");
        Color line = Brush(window, "Passenger.Divider");

        // The frame round the edges is one ground with no rule round it; the tool windows a step lighter; the editor
        // a step from both, each step one a person sees.
        foreach (Border part in window.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible
            && b.Classes.Any(c => c is "titleBar" or "stripeBar" or "tabStrip" or "statusBar")))
        {
            string what = part.Classes.First(c => c is "titleBar" or "stripeBar" or "tabStrip" or "statusBar");
            Assert.True(part.BorderThickness == default, $"The {what} has a rule of {part.BorderThickness} along it, where the window's areas are told apart by shade");
            Assert.True(what == "titleBar" || (part.Background is ISolidColorBrush ground && ground.Color == frame),
                $"The {what} is drawn on {(part.Background as ISolidColorBrush)?.Color}, not the frame's ground {frame}, as the stripes, the tabs' strip and the status bar are");
        }
        // The title bar's words and icons in its own colours (white on Passenger's blue), whatever the window's are.
        Border bar = Named<Border>(window, "Toolbar");
        Color[] own = [.. ((string[])["Text", "Icon", "Run", "Stop", "HotReload", "BadgeText"]).Select(k => Brush(window, $"Passenger.TitleBar.{k}"))];
        foreach (TextBlock words in bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            Assert.True(words.Foreground is ISolidColorBrush ink && own.Contains(ink.Color),
                $"The title bar's '{words.Text}' is drawn {(words.Foreground as ISolidColorBrush)?.Color}, not in the title bar's own colours");
        }
        foreach (PathIcon icon in bar.GetVisualDescendants().OfType<PathIcon>().Where(i => i.IsEffectivelyVisible))
        {
            string where = icon.FindAncestorOfType<Button>() is { } button ? ToolTip.GetTip(button) as string ?? button.Name ?? "a button" : "the bar";
            Assert.True(icon.Foreground is ISolidColorBrush ink && own.Contains(ink.Color),
                $"An icon on the title bar ({where}) is drawn {(icon.Foreground as ISolidColorBrush)?.Color}, not in the title bar's own colours");
        }
        Assert.True(Step(frame, panel) >= MinimumStep && Step(panel, editor) >= MinimumStep && Step(frame, editor) >= MinimumStep,
            $"The frame ({frame}), the tool windows ({panel}) and the editor ({editor}) are {Step(frame, panel):0.#}, {Step(panel, editor):0.#} and "
            + $"{Step(frame, editor):0.#} apart in lightness, where each must be at least {MinimumStep} apart for a person to tell the areas apart without a rule");

        Rect explorer = Placed(window, Pane(window.ExplorerView));
        Rect structure = Placed(window, Pane(window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.StructureView>().First(v => v.IsEffectivelyVisible)));
        Rect problems = Placed(window, Pane(window.DockHost.GetVisualDescendants().OfType<Passenger.App.ToolWindows.ProblemsView>().First(v => v.IsEffectivelyVisible)));
        Rect code = Placed(window, window.EditorArea.CodeEditor);
        Rect status = Placed(window, window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("statusBar")));
        using Picture picture = new(window);
        // Where two grounds meet, nothing but the two: the stripe and a tool window, a tool window and the editor, the
        // tabs' strip and the editor, the editor and the bottom tool window, a tool window and the status bar.
        picture.Meets("the left stripe and the Explorer", new Point(explorer.Left, explorer.Top + (explorer.Height * 0.8)), across: true, frame, panel);
        picture.Meets("the Explorer and the editor", new Point(explorer.Right, code.Top + (code.Height * 0.7)), across: true, panel, editor);
        // The caret's line, the file's first, lit across the editor.
        picture.Meets("the tabs' strip and the editor", new Point(code.Right - 60, code.Top), across: false, frame, Brush(window, "Passenger.Editor.CurrentLine"));
        picture.Meets("the editor and Problems", new Point(code.Left + 6, problems.Top), across: false, editor, panel);
        picture.Meets("Problems and the status bar", new Point(problems.Left + (problems.Width * 0.6), status.Top), across: false, panel, frame);
        // Where two tool windows meet, one ground, a line shows the edge: the Explorer over Structure, Structure over
        // Problems.
        picture.Seam("the Explorer and Structure", new Point(explorer.Left + (explorer.Width / 2), structure.Top), panel, line);
        picture.Seam("Structure and Problems", new Point(structure.Left + (structure.Width / 2), problems.Top), panel, line);
    }

    [AvaloniaFact]
    public void A_tooltip_and_a_drop_downs_list_are_the_themes_popups_and_a_field_stands_on_the_fields_ground()
    {
        MainWindow window = Open(1200, 800);
        try
        {
            UnderEachLook(window, () => PopupGrounds(window));
        }
        finally
        {
            window.Close();
        }
    }

    private static void PopupGrounds(MainWindow window)
    {
        SettingsDialog? settings = null;
        try
        {
            Color popup = Brush(window, "Passenger.Popup");
            Color edge = Brush(window, "Passenger.PopupBorder");
            Color text = Brush(window, "Passenger.Text");
            Color field = Brush(window, "Passenger.Field");
            Color chosenRow = Brush(window, "Passenger.RowSelected");
            ToggleButton stripe = window.DockHost.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Classes.Contains("stripe") && b.IsEffectivelyVisible);
            ToolTip.SetIsOpen(stripe, true);
            Ui.Settle();
            ToolTip tip = window.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault(t => t.IsEffectivelyVisible)
                ?? throw new Xunit.Sdk.XunitException($"The stripe button's tooltip '{ToolTip.GetTip(stripe)}' did not open");
            Assert.True(tip.Background is ISolidColorBrush tipGround && tipGround.Color == popup && tip.BorderBrush is ISolidColorBrush tipEdge && tipEdge.Color == edge
                && tip.Foreground is ISolidColorBrush tipWords && tipWords.Color == text,
                $"A tooltip is drawn on {(tip.Background as ISolidColorBrush)?.Color} with an edge of {(tip.BorderBrush as ISolidColorBrush)?.Color}, "
                + $"where it is the popup's ground ({popup}) and edge ({edge}) with the window's words, not Fluent's greys");
            ToolTip.SetIsOpen(stripe, false);

            settings = window.ShowSettings();
            Ui.Settle();
            ComboBox combo = settings.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            Border ground = combo.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background");
            Assert.True(ground.Background is ISolidColorBrush comboGround && comboGround.Color == field,
                $"A drop-down is drawn on {(ground.Background as ISolidColorBrush)?.Color}, where a field's ground is {field}");
            TextBox search = settings.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
            combo.Focus();
            Ui.Settle();
            Border searchGround = search.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            Assert.True(searchGround.Background is ISolidColorBrush searchColour && searchColour.Color == field,
                $"A text field without the keys is drawn on {(searchGround.Background as ISolidColorBrush)?.Color}, where a field's ground is {field}");

            combo.IsDropDownOpen = true;
            Ui.Settle();
            Border list = combo.GetVisualDescendants().OfType<Popup>().First().Child as Border ?? throw new Xunit.Sdk.XunitException("The drop-down's list has no frame");
            Assert.True(list.Background is ISolidColorBrush listGround && listGround.Color == popup && list.BorderBrush is ISolidColorBrush listEdge && listEdge.Color == edge,
                $"A drop-down's list is drawn on {(list.Background as ISolidColorBrush)?.Color} with an edge of {(list.BorderBrush as ISolidColorBrush)?.Color}, "
                + $"where it is the popup's ground ({popup}) and edge ({edge}), as a menu is");
            ComboBoxItem chosen = (ComboBoxItem)combo.ContainerFromIndex(combo.SelectedIndex)!;
            ContentPresenter band = chosen.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            object? radius = window.TryFindResource("Passenger.ControlRadius", window.ActualThemeVariant, out object? found) ? found : null;
            Assert.True(band.Background is ISolidColorBrush row && row.Color == chosenRow && radius is CornerRadius rounded && chosen.CornerRadius == rounded
                && Math.Abs(chosen.Bounds.Height - ListRow.Height) < 0.5 && chosen.Margin.Left > 0,
                $"A drop-down's chosen item is drawn on {(band.Background as ISolidColorBrush)?.Color} with corners of {chosen.CornerRadius}, {chosen.Bounds.Height:0.#} px tall, "
                + $"where it is a list's chosen row ({chosenRow}) on a band inset from the frame with the controls' corners, {ListRow.Height} tall, as a menu's item is");
            combo.IsDropDownOpen = false;
            Ui.Settle();
        }
        finally
        {
            settings?.Close();
            Ui.Settle();
        }
    }

    /// <summary>Every theme, for a rule held of its tokens alone, which costs no window.</summary>
    public static TheoryData<Look> EveryLook => [.. Enum.GetValues<Look>()];

    [AvaloniaTheory]
    [MemberData(nameof(EveryLook))]
    public void Text_icons_and_focus_rings_meet_WCAG_AA_on_what_they_are_drawn_on_dark_and_light(Look look)
    {
        using IDisposable worn = Wearing(look);
        List<string> below = [];
        foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
        {
            foreach ((string fore, string ground, double least) in ContrastPairs)
            {
                // A see-through ground is drawn over what it stands on: the title bar's hover over the bar, a row's over a tool window.
                Color[] under = Colours(ground.StartsWith("Passenger.TitleBar.", StringComparison.Ordinal) ? "Passenger.TitleBar" : ground == "Passenger.HoverOnFrame" ? "Passenger.Frame" : "Passenger.Panel", variant);
                Color[] grounds = Colours(ground == "Passenger.HoverOnFrame" ? "Passenger.Hover" : ground, variant);
                double worst = (from f in Colours(fore, variant) from g in grounds from u in under select Ratio(f, Over(g, u))).Min();
                if (worst < least)
                {
                    below.Add($"{variant}: {fore} on {ground} is {worst:0.00}:1, under {least}:1");
                }
            }
        }
        Assert.True(below.Count == 0, $"In {look}, {below.Count} pairs fall below WCAG AA (text 4.5:1, icons, focus rings and the caret 3:1):\n" + string.Join("\n", below));
    }

    [AvaloniaFact]
    public void Every_theme_gives_every_colour_in_Dark_and_Light_and_Rider_s_scheme_only_the_codes()
    {
        ResourceDictionary passenger = Dictionary("Tokens.axaml");
        ResourceDictionary rider = Dictionary("RiderScheme.axaml");
        string[] keys = Keys(passenger, ThemeVariant.Dark);
        string[] shapes = [.. passenger.Keys.Select(k => k.ToString()!).Order(StringComparer.Ordinal)];
        Assert.Equal(keys, Keys(passenger, ThemeVariant.Light));
        List<(string, ResourceDictionary)> files = [("Tokens.axaml", passenger), ("RiderScheme.axaml", rider)];
        foreach (Look look in Enum.GetValues<Look>().Where(l => l != Look.Passenger))
        {
            ResourceDictionary theme = Dictionary($"{look}.axaml");
            files.Add(($"{look}.axaml", theme));
            Assert.True(keys.SequenceEqual(Keys(theme, ThemeVariant.Dark)) && keys.SequenceEqual(Keys(theme, ThemeVariant.Light)),
                $"{look} does not give every colour Tokens.axaml gives, in Dark and Light, and no other: a colour it lacks shows Passenger's own under it");
            Assert.True(shapes.SequenceEqual(theme.Keys.Select(k => k.ToString()!).Order(StringComparer.Ordinal)), $"{look} does not give every shape Tokens.axaml gives");
        }
        // The code's colours, which Rider's scheme gives in its place; the editor's ground, gutter and cards stay the theme's.
        string[] scheme = [.. keys.Where(k => (k.StartsWith("Passenger.Editor.", StringComparison.Ordinal) && k is not ("Passenger.Editor.Background" or "Passenger.Editor.Gutter" or "Passenger.Editor.PopupBorder"))
            || k.StartsWith("Passenger.Syntax.", StringComparison.Ordinal) || k.StartsWith("Passenger.SyntaxStyle.", StringComparison.Ordinal) || k.StartsWith("Passenger.Diff.", StringComparison.Ordinal))];
        Assert.Equal(scheme, Keys(rider, ThemeVariant.Dark));
        Assert.Equal(scheme, Keys(rider, ThemeVariant.Light));
        // A theme is tokens alone: Fluent's own keys are FluentKeys.axaml's, once for every theme.
        foreach ((string file, ResourceDictionary theme) in files)
        {
            string[] others = [.. theme.Keys.Concat(theme.ThemeDictionaries.Values.SelectMany(v => ((ResourceDictionary)v).Keys)).Select(k => k.ToString()!)
                .Where(k => !k.StartsWith("Passenger.", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal)];
            Assert.True(others.Length == 0, $"{file} gives {string.Join(", ", others)}, which are not Passenger's tokens: name Fluent's keys once for every theme in FluentKeys.axaml");
        }
    }

    /// <summary>Every colour, brush and corner Fluent gives is the look's own token in either look, Dark and Light
    /// (FluentKeys.axaml), so a control left to Fluent never shows another theme's colours or Fluent's greys: a tooltip, a
    /// drop-down's list and an unfocused field had Fluent's grey under both themes, each theme mapping Fluent's keys by
    /// itself. A key is left to Fluent only by the reasons below; an Avalonia update that adds one fails here until it is
    /// mapped.</summary>
    [AvaloniaFact]
    public void Every_colour_and_corner_Fluent_gives_is_the_looks_own_token_dark_and_light()
    {
        Application app = Application.Current!;
        FluentTheme fluent = app.Styles.OfType<FluentTheme>().Single();
        HashSet<string> keys = new(StringComparer.Ordinal);
        KeysOf(fluent.Resources, keys);
        foreach (Styles child in ((IEnumerable<IStyle>)fluent).OfType<Styles>())
        {
            KeysOf(child.Resources, keys);
        }
        string source = UiSource();
        List<string> unmapped = [];
        List<string> wrong = [];
        List<string> mapped = [];
        foreach (string key in keys.Order(StringComparer.Ordinal))
        {
            object? dark = fluent.TryGetResource(key, ThemeVariant.Dark, out object? d) ? d : null;
            object? light = fluent.TryGetResource(key, ThemeVariant.Light, out object? l) ? l : null;
            if (!Painted(dark) && !Painted(light))
            {
                continue;
            }
            if (Clear(dark) && Clear(light))
            {
                continue;
            }
            if (LeftToFluent(key, dark ?? light, source) is { } why)
            {
                Assert.True(!why.StartsWith('!'), $"{key} is left to Fluent, but {why[1..]}");
                continue;
            }
            if (!FluentKeys.Tokens.TryGetValue(key, out string? token))
            {
                unmapped.Add(key);
                continue;
            }
            mapped.Add(key);
        }
        Assert.True(unmapped.Count == 0, $"Fluent gives {unmapped.Count} keys Passenger's controls can reach that no theme answers for, each showing Fluent's own colour "
            + $"under every look: name each in FluentKeys.axaml as the token it is, or say in this test why no control of Passenger's reads it:\n{string.Join("\n", unmapped)}");
        foreach (Look look in Enum.GetValues<Look>())
        {
            using IDisposable worn = Wearing(look);
            foreach (ThemeVariant variant in (ThemeVariant[])[ThemeVariant.Dark, ThemeVariant.Light])
            {
                foreach ((string key, string token) in FluentKeys.Tokens)
                {
                    bool has = app.TryGetResource(token, variant, out object? own);
                    if (!has || !app.TryGetResource(key, variant, out object? found) || !ReferenceEquals(found, own))
                    {
                        wrong.Add($"{look} {variant}: {key} is not {token}{(has ? "" : ", which the look lacks")}");
                    }
                }
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} of Fluent's keys are not the look's own token:\n{string.Join("\n", wrong)}");
        string[] stale = [.. FluentKeys.Tokens.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal)];
        Assert.True(stale.Length == 0, $"FluentKeys.axaml names {string.Join(", ", stale)}, which Fluent no longer gives");
    }

    /// <summary>Why a key Fluent gives is left to it, or null when a control of Passenger's can reach it; a reason that
    /// does not hold starts with '!'.</summary>
    private static string? LeftToFluent(string key, object? value, string source)
    {
        // Fluent's palette colours, read only into its own brushes as they load: a control asks for a brush (mapped), and
        // nothing of Passenger's asks for a colour by name.
        if (value is Color)
        {
            return Regex.IsMatch(source, $@"\b{key}\b") ? $"!Passenger's source reads {key}, a colour Fluent's palette gives" : "a palette colour";
        }
        // ControlThemes.axaml's scrollbar is Passenger's whole, so Fluent's scrollbar keys are never read.
        if (key.StartsWith("ScrollBar", StringComparison.Ordinal))
        {
            Application.Current!.TryGetResource(typeof(ScrollBar), null, out object? ours);
            FluentTheme fluent = Application.Current.Styles.OfType<FluentTheme>().Single();
            fluent.TryGetResource(typeof(ScrollBar), null, out object? theirs);
            return ReferenceEquals(ours, theirs) ? "!the scrollbar's control theme is Fluent's, not ControlThemes.axaml's" : "Passenger's own scrollbar";
        }
        // Fluent's Button.accent alone reads these; Passenger's emphasised button is Button.primary (Shell.axaml).
        if (key.StartsWith("AccentButton", StringComparison.Ordinal))
        {
            return Regex.IsMatch(source, "\"accent\"") ? "!a control of Passenger's takes Fluent's accent class" : "Fluent's accent button";
        }
        // Controls Passenger never shows: none of its windows names one.
        foreach ((string prefix, string[] controls) in Unshown)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                string? shown = controls.FirstOrDefault(c => Regex.IsMatch(source, $@"\b{c}\b"));
                return shown is null ? $"no {controls[0]}" : $"!Passenger shows a {shown}, whose {prefix} keys must be named in FluentKeys.axaml";
            }
        }
        return null;
    }

    private static readonly (string Prefix, string[] Controls)[] Unshown =
    [
        ("AutoCompleteBox", ["AutoCompleteBox"]),
        ("Calendar", ["Calendar", "CalendarDatePicker"]),
        ("DatePicker", ["DatePicker", "CalendarDatePicker"]),
        ("TimePicker", ["TimePicker"]),
        ("DateTime", ["DatePicker", "TimePicker"]),
        ("ToggleSwitch", ["ToggleSwitch"]),
        ("Slider", ["Slider"]),
        ("TreeView", ["TreeView", "TreeViewItem"]),
        ("TabItem", ["TabItem", "TabControl"]),
        ("TabControl", ["TabControl"]),
        ("TabbedPage", ["TabbedPage"]),
        ("SplitButton", ["SplitButton", "ToggleSplitButton"]),
        ("SplitView", ["SplitView"]),
        ("HyperlinkButton", ["HyperlinkButton"]),
        ("HyperlinkVisited", ["HyperlinkButton"]),
        ("Expander", ["Expander"]),
        ("GroupBox", ["GroupBox"]),
        ("NotificationCard", ["NotificationCard", "WindowNotificationManager"]),
        ("PipsPager", ["PipsPager"]),
        ("TableView", ["TableView"]),
        ("RefreshVisualizer", ["RefreshContainer", "RefreshVisualizer"]),
        ("NavigationBar", ["NavigationPage", "DrawerPage"]),
        ("NavigationControl", ["NavigationPage", "DrawerPage"]),
        ("Flyout", ["Flyout", "FlyoutPresenter"]),
    ];

    private static bool Painted(object? value) => value is Color or IBrush or CornerRadius;

    private static bool Clear(object? value) => value is ISolidColorBrush { Color.A: 0 } or Color { A: 0 };

    private static void KeysOf(IResourceProvider provider, HashSet<string> keys)
    {
        if (provider is ResourceInclude include)
        {
            KeysOf(include.Loaded, keys);
            return;
        }
        if (provider is IResourceDictionary dictionary)
        {
            keys.UnionWith(dictionary.Keys.OfType<string>());
            foreach (IThemeVariantProvider variant in dictionary.ThemeDictionaries.Values)
            {
                KeysOf(variant, keys);
            }
            foreach (IResourceProvider merged in dictionary.MergedDictionaries)
            {
                KeysOf(merged, keys);
            }
        }
    }

    /// <summary>No colour and no rounded corner is written outside the theme files (Theme/): each is a token of the look
    /// in force, so a look draws the whole window. The editor's palette had 54 colours of its own for a token a theme
    /// left out, the profiler's charts and the terminal named colours. Where a colour is the content's, not the
    /// window's, it is allowed below, saying why.</summary>
    [Fact]
    public void No_colour_or_rounded_corner_is_written_outside_the_theme_files()
    {
        Regex colour = new(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|\bColor\.(?:FromRgb|FromArgb|FromUInt32|FromHsl|FromHsv|Parse)\b|\bColors\.\w+|\bBrushes\.(?!Transparent\b)\w+|new SolidColorBrush\(\s*(?:Color|Colors)\.");
        Regex corners = new(@"new CornerRadius\(\s*([0-9.,\s]+)\)|CornerRadius\s*=\s*([0-9.]+)\b|CornerRadius=""([0-9.,\s]+)""");
        List<string> found = [];
        foreach ((string path, string text) in UiFiles())
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (Match match in colour.Matches(line))
                {
                    if (!ContentsColour(path, match.Value))
                    {
                        found.Add($"{path}:{i + 1}: {match.Value}");
                    }
                }
                foreach (Match match in corners.Matches(line))
                {
                    string numbers = string.Concat(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                    if (numbers.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Any(n => double.Parse(n, System.Globalization.CultureInfo.InvariantCulture) != 0))
                    {
                        found.Add($"{path}:{i + 1}: {match.Value}");
                    }
                }
            }
        }
        Assert.True(found.Count == 0, $"{found.Count} colours or rounded corners are written outside Theme/, so one look's would show under another: give each a token "
            + $"(Tokens.axaml and Harbour.axaml, Dark and Light) or a radius resource, and read that:\n{string.Join("\n", found)}");
    }

    /// <summary>A colour that is the content's own, not the window's, so no look has a token for it.</summary>
    private static bool ContentsColour(string path, string match) => (path, match) switch
    {
        // The colour being chosen in the colour picker, and the shades of it its square and slider show.
        (_, _) when path.EndsWith("Xaml/ColourPicker.cs", StringComparison.Ordinal) => true,
        // A colour written in the XAML being edited, which the editor shows beside it.
        (_, _) when path.EndsWith("Xaml/XamlService.cs", StringComparison.Ordinal) => true,
        // A named colour's swatch in XAML completion: the colour the name means.
        (_, "Color.FromArgb" or "new SolidColorBrush(Color.") when path.EndsWith("CSharp/CodeAssist.cs", StringComparison.Ordinal) => true,
        // A colour written in the CSS or HTML being edited, as its server reads it.
        (_, "Color.FromArgb") when path.EndsWith("Web/WebDocument.cs", StringComparison.Ordinal) => true,
        // A colour the program in the terminal names: xterm's 24-bit colours, its 256-colour cube and grey ramp.
        (_, "Color.FromRgb" or "new SolidColorBrush(Color.") when path.EndsWith("TerminalView.cs", StringComparison.Ordinal) => true,
        _ => false,
    };

    private static string UiSource() => string.Join("\n", UiFiles().Select(f => f.Text));

    /// <summary>The C# and XAML of the projects that draw the window, less the theme files.</summary>
    private static IEnumerable<(string Path, string Text)> UiFiles()
    {
        string root = Passenger.Tests.Debugging.Helpers.Debuggees.Root;
        foreach (string project in (string[])["Passenger.App", "Passenger.Editor", "Passenger.Terminal", "Passenger.Profiler"])
        {
            string folder = Path.Combine(root, "src", project);
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if ((file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal))
                    && !relative.Contains("/Theme/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    yield return (relative, File.ReadAllText(file));
                }
            }
        }
    }

    /// <summary>Wears a theme for a test, Passenger's own put back after it, pass or fail.</summary>
    public static IDisposable Wearing(Look look)
    {
        Themes.Apply(Application.Current!, look, ColourScheme.Theme);
        return new Worn();
    }

    private sealed class Worn : IDisposable
    {
        public void Dispose() => Themes.Apply(Application.Current!, Look.Passenger, ColourScheme.Theme);
    }

    /// <summary>The least step in lightness (CIELAB's L*) between two grounds side by side that tells them apart.</summary>
    private const double MinimumStep = 2;

    private static double Step(Color one, Color other) => Math.Abs(Lightness(one) - Lightness(other));

    private static double Lightness(Color colour)
    {
        double y = Luminance(colour);
        return y > 216.0 / 24389 ? (116 * Math.Cbrt(y)) - 16 : y * 24389 / 27;
    }

    private static double Luminance(Color colour)
    {
        static double Channel(byte value) => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow(((value / 255.0) + 0.055) / 1.055, 2.4);
        return (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));
    }

    private static double Ratio(Color fore, Color ground)
    {
        double a = Luminance(Over(fore, ground));
        double b = Luminance(ground);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Color Over(Color fore, Color ground)
    {
        double a = fore.A / 255.0;
        byte Mix(byte f, byte g) => (byte)Math.Round((f * a) + (g * (1 - a)));
        return Color.FromRgb(Mix(fore.R, ground.R), Mix(fore.G, ground.G), Mix(fore.B, ground.B));
    }

    /// <summary>A token's colours in a theme variant, its opacity in their alpha: one, or a gradient's every stop.</summary>
    private static Color[] Colours(string key, ThemeVariant variant)
    {
        if (!Application.Current!.TryGetResource(key, variant, out object? found))
        {
            throw new Xunit.Sdk.XunitException($"The theme has no {key} in {variant}");
        }
        return found switch
        {
            ISolidColorBrush solid => [Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B)],
            IGradientBrush gradient => [.. gradient.GradientStops.Select(s => s.Color)],
            _ => throw new Xunit.Sdk.XunitException($"{key} is a {found?.GetType().Name}, not a brush"),
        };
    }

    /// <summary>What the window draws, word or mark, on what ground, and the least contrast WCAG AA asks of it: text 4.5:1,
    /// an icon, a focus ring or the caret 3:1. Disabled text is exempt, as WCAG's inactive components are.</summary>
    private static readonly (string Fore, string Ground, double Least)[] ContrastPairs = BuildPairs();

    private static (string, string, double)[] BuildPairs()
    {
        const double Text = 4.5;
        const double Mark = 3;
        List<(string, string, double)> pairs = [];
        foreach (string ground in (string[])["Passenger.Panel", "Passenger.Frame", "Passenger.Field", "Passenger.Popup", "Passenger.List", "Passenger.Hover", "Passenger.HoverOnFrame",
            "Passenger.Selected", "Passenger.RowSelected", "Passenger.Editor.Background", "Passenger.Banner", "Passenger.Button", "Passenger.Welcome.Tile"])
        {
            pairs.Add(("Passenger.Text", ground, Text));
            pairs.Add(("Passenger.TextMuted", ground, Text));
        }
        foreach (string ground in (string[])["Passenger.Panel", "Passenger.Frame", "Passenger.Popup", "Passenger.Editor.Background"])
        {
            pairs.Add(("Passenger.Link", ground, Text));
            pairs.Add(("Passenger.Error", ground, Text));
            pairs.Add(("Passenger.Warning", ground, Text));
            pairs.Add(("Passenger.Focus", ground, Mark));
            pairs.Add(("Passenger.Icon", ground, Mark));
        }
        pairs.Add(("Passenger.Focus", "Passenger.Field", Mark));
        pairs.Add(("Passenger.LinkHover", "Passenger.Panel", Text));
        pairs.Add(("Passenger.Accent", "Passenger.Panel", Mark));
        pairs.Add(("Passenger.AccentText", "Passenger.Accent", Text));
        pairs.Add(("Passenger.AccentText", "Passenger.AccentHover", Text));
        pairs.Add(("Passenger.AccentText", "Passenger.Welcome.Badge", Text));
        pairs.Add(("Passenger.Menu.SelectedText", "Passenger.Menu.Selected", Text));
        pairs.Add(("Passenger.Tab.ActiveText", "Passenger.Tab.Active", Text));
        pairs.Add(("Passenger.Stripe.SelectedIcon", "Passenger.Stripe.Selected", Mark));
        pairs.Add(("Passenger.Welcome.ChipIcon", "Passenger.Welcome.Chip", Mark));
        foreach (string file in (string[])["Modified", "Added", "Unversioned", "Conflicted"])
        {
            pairs.Add(($"Passenger.Vcs.File.{file}", "Passenger.Panel", Text));
            pairs.Add(($"Passenger.Vcs.File.{file}", "Passenger.RowSelected", Text));
        }
        foreach (string ground in (string[])["Passenger.TitleBar", "Passenger.TitleBar.Hover", "Passenger.TitleBar.Pressed"])
        {
            pairs.Add(("Passenger.TitleBar.Text", ground, Text));
            pairs.Add(("Passenger.TitleBar.Focus", ground, Mark));
        }
        foreach (string mark in (string[])["Icon", "Run", "Stop", "HotReload"])
        {
            pairs.Add(($"Passenger.TitleBar.{mark}", "Passenger.TitleBar", Mark));
        }
        pairs.Add(("Passenger.TitleBar.BadgeText", "Passenger.TitleBar.Badge", Text));
        pairs.Add(("Passenger.Terminal.Foreground", "Passenger.Terminal.Background", Text));
        foreach (string ground in (string[])["Passenger.Editor.Background", "Passenger.Editor.CurrentLine", "Passenger.Editor.Selection", "Passenger.Editor.ExecutionLine"])
        {
            pairs.Add(("Passenger.Editor.Text", ground, Text));
        }
        pairs.Add(("Passenger.Editor.LineNumber", "Passenger.Editor.Gutter", Text));
        pairs.Add(("Passenger.Editor.LineNumberCurrent", "Passenger.Editor.Gutter", Text));
        pairs.Add(("Passenger.Editor.InlineValue", "Passenger.Editor.Background", Text));
        pairs.Add(("Passenger.Editor.InlineValue", "Passenger.Editor.ExecutionLine", Text));
        pairs.Add(("Passenger.Editor.InlayHint", "Passenger.Editor.InlayHintBack", Text));
        pairs.Add(("Passenger.Editor.Caret", "Passenger.Editor.Background", Mark));
        pairs.Add(("Passenger.Editor.Caret", "Passenger.Editor.CurrentLine", Mark));
        foreach (string kind in (string[])["Keyword", "ControlKeyword", "Comment", "DocComment", "String", "StringEscape", "Number", "Preprocessor", "Class", "Interface",
            "Struct", "Enum", "Delegate", "TypeParameter", "Method", "Property", "Event", "Field", "Constant", "EnumMember", "ErrorOutput", "UserInput", "Link",
            "Brace1", "Brace2", "Brace3", "Tag", "AttributeName", "AttributeValue", "Entity", "JsonKey", "YamlKey", "Heading", "Code", "Quote", "LinkDestination"])
        {
            pairs.Add(($"Passenger.Syntax.{kind}", "Passenger.Editor.Background", Text));
            pairs.Add(($"Passenger.Syntax.{kind}", "Passenger.Editor.CurrentLine", Text));
        }
        return [.. pairs];
    }

    private static ResourceDictionary Dictionary(string file) =>
        (ResourceDictionary)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri($"avares://Passenger/Theme/{file}"));

    private static string[] Keys(ResourceDictionary dictionary, ThemeVariant variant) =>
        [.. ((ResourceDictionary)dictionary.ThemeDictionaries[variant]).Keys.Select(k => k.ToString()!).Where(k => k.StartsWith("Passenger.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>The tool window's pane a view is shown in.</summary>
    private static Control Pane(Control view) => view.GetVisualAncestors().OfType<Control>().First(a => a.Classes.Contains("toolPane"));

    /// <summary>The window as the compositor last drew it, read pixel by pixel.</summary>
    private sealed class Picture : IDisposable
    {
        private readonly Avalonia.Media.Imaging.WriteableBitmap frame;
        private readonly double scale;

        public Picture(TopLevel window)
        {
            frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window has drawn no frame.");
            scale = window.RenderScaling;
        }

        public void Dispose() => frame.Dispose();

        public Color At(Point at)
        {
            byte[] pixel = new byte[4];
            System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                PixelPoint point = PixelPoint.FromPoint(at, scale);
                frame.CopyPixels(new PixelRect(point.X, point.Y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4);
            }
            finally
            {
                pinned.Free();
            }
            return frame.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(pixel[0], pixel[1], pixel[2]) : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }

        /// <summary>Two grounds meet at an edge, read across it (left to right, or top to bottom): the first before it, the
        /// second after it, and nothing else between, no rule.</summary>
        public void Meets(string what, Point edge, bool across, Color before, Color after)
        {
            Point Off(double by) => across ? edge + new Point(by, 0) : edge + new Point(0, by);
            Color[] seen = [.. Enumerable.Range(-4, 9).Select(by => At(Off(by)))];
            Assert.True(Near(seen[0], before) && Near(seen[^1], after) && seen.All(c => Near(c, before) || Near(c, after)),
                $"Where {what} meet, the window draws {string.Join(" ", seen)} across the edge, where it would draw {before} then {after} and no rule between");
        }

        /// <summary>Two panes of one ground meet at an edge, read top to bottom: the ground, a line of the divider's colour,
        /// the ground again.</summary>
        public void Seam(string what, Point edge, Color ground, Color line)
        {
            Color[] seen = [.. Enumerable.Range(-4, 9).Select(by => At(edge + new Point(0, by)))];
            Assert.True(Near(seen[0], ground) && Near(seen[^1], ground) && seen.Any(c => Near(c, line)),
                $"Where {what} meet, both on {ground}, the window draws {string.Join(" ", seen)} across the edge, where a line of {line} would show it");
        }

        private static bool Near(Color one, Color other) => Math.Abs(one.R - other.R) <= 3 && Math.Abs(one.G - other.G) <= 3 && Math.Abs(one.B - other.B) <= 3;
    }

    /// <summary>Each stripe button shown lies wholly inside its stripe, above the status bar.</summary>
    private static void StripeButtonsFit(MainWindow window)
    {
        Rect dock = Placed(window, window.DockHost);
        foreach (ToggleButton button in window.DockHost.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("stripe") && b.IsEffectivelyVisible))
        {
            Rect at = Placed(window, button);
            Assert.True(at.Top >= dock.Top - 0.5 && at.Bottom <= dock.Bottom + 0.5,
                $"The stripe's {ToolTip.GetTip(button)} button is cut off at {at.Bottom:0.#}, past the stripe's foot at {dock.Bottom:0.#}");
        }
    }

    /// <summary>Opens a status bar or toolbar widget's menu with a click and holds it to the rule: the current value
    /// checked and enabled, the others not checked.</summary>
    private static void Current(MainWindow window, string widget, string current, string what)
    {
        Button button = Named<Button>(window, widget);
        Point at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        Ui.WaitFor(() => window.OpenContextMenu is not null, what);
        ContextMenu menu = window.OpenContextMenu!;
        MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
        MenuItem[] checkedOnes = [.. items.Where(i => i.IsChecked)];
        Assert.True(checkedOnes.Length == 1 && Header(checkedOnes[0]) == current,
            $"In {what}, [{string.Join(", ", checkedOnes.Select(Header))}] are checked, where {current}, the one in force, alone is");
        Assert.True(checkedOnes[0].IsEnabled, $"In {what}, {current}, the one in force, is greyed where Rider checks it");
        Assert.True(checkedOnes[0].ToggleType is MenuItemToggleType.Radio or MenuItemToggleType.CheckBox, $"In {what}, {current} shows no tick");
        DropsFrom(button, menu, what);
        menu.Close();
        Ui.Settle();
    }

    /// <summary>A widget's menu opens against it, its frame's edge meeting the widget's and one of its sides in line with
    /// the widget's, wherever the room for its shadow put the menu's popup (MenuShadow).</summary>
    private static void DropsFrom(Control widget, ContextMenu menu, string what)
    {
        Border frame = menu.GetVisualDescendants().OfType<Border>().First(b => b.BoxShadow.Count > 0);
        PixelPoint w = widget.PointToScreen(default);
        PixelPoint f = frame.PointToScreen(default);
        double scale = TopLevel.GetTopLevel(widget)!.RenderScaling;
        Rect at = new(w.X / scale, w.Y / scale, widget.Bounds.Width, widget.Bounds.Height);
        Rect drawn = new(f.X / scale, f.Y / scale, frame.Bounds.Width, frame.Bounds.Height);
        bool meets = Math.Abs(drawn.Top - at.Bottom) <= 2 || Math.Abs(drawn.Bottom - at.Top) <= 2;
        bool lined = Math.Abs(drawn.Left - at.Left) <= 2 || Math.Abs(drawn.Right - at.Right) <= 2;
        Assert.True(meets && lined, $"{what}'s frame is drawn at {drawn}, not against its widget at {at}: the menu opens away from what opened it");
    }

    private static string? Header(MenuItem item) => item.Header switch
    {
        string words => words.Replace("__", "_", StringComparison.Ordinal),
        Control control => string.Concat(control.GetSelfAndLogicalDescendants().OfType<TextBlock>().Take(1).Select(t => t.Text)),
        _ => null,
    };

    private static IEnumerable<Visual> ToolWindowParts(MainWindow window) =>
        window.DockHost.GetVisualDescendants().Where(v => v.IsEffectivelyVisible && !window.EditorArea.IsVisualAncestorOf(v) && v != window.EditorArea);

    private static string Words(ToggleButton tab) => string.Join(" ", tab.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static Color Brush(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : default;

    private static T Named<T>(MainWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException($"The window has no {name}");

    private static double Off(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Rect Placed(Visual window, Visual control) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    /// <summary>A window on the class's project, on the server the last one left.</summary>
    private MainWindow Open(double width, double height)
    {
        MainWindow window = new() { Width = width, Height = height, Autosaves = false, WarmServer = project.Server };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        return window;
    }

    /// <summary>Holds a window to a check under each look, switched in place as Settings switches it (the window's settings
    /// put in force, so it is restyled where it stands), a failure naming the look; Passenger's own put back after it.</summary>
    private static void UnderEachLook(MainWindow window, Action check)
    {
        try
        {
            foreach (Look look in Looks)
            {
                window.ApplySettings(window.Settings with { Look = look });
                Ui.Settle();
                try
                {
                    check();
                }
                catch (Xunit.Sdk.XunitException failed)
                {
                    throw new Xunit.Sdk.XunitException($"Under the {look} look: {failed.Message}", failed);
                }
            }
        }
        finally
        {
            Themes.Apply(Application.Current!, Look.Passenger, ColourScheme.Theme);
        }
    }

    /// <summary>A small project in a Git repository (main, a branch other, the tag v1.0), restored once for every test, and
    /// the warm server they share on it: a file long and wide enough to scroll both ways, a text file, and a file deep enough
    /// that its path fills the status bar at the window's smallest.</summary>
    public sealed class Project : SharedProject
    {
        public const string Deep = "Source/Features/Payments/Processing/RefundCalculator.cs";

        public Project()
            : base("ui-rules-")
        {
            File.WriteAllText(PathOf("Hello.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(PathOf("Program.cs"),
                string.Concat(Enumerable.Range(1, 120).Select(i => $"// Line {i} of a file long enough to scroll, and wide enough to scroll sideways too: {new string('x', 160)}\n")));
            File.WriteAllText(PathOf("Notes.txt"), "one\ntwo\n");
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(Deep))!);
            File.WriteAllText(PathOf(Deep), "class RefundCalculator\n{\n}\n");
            TestFolders.Prepare(() =>
            {
                TestGit.Init(Folder);
                TestGit.Run(Folder, "branch", "other");
                TestGit.Run(Folder, "tag", "v1.0");
            }, Folder);
            Prepare();
        }
    }
}
