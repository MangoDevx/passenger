using Avalonia.Controls;
using Passenger.App.Workspace;
using Passenger.Lsp;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Target Framework: for a file the language server compiles in several projects (each target framework of a
/// multi-targeted project), the editor's context switcher beside its problem counts, and the action it runs, which
/// offers the others. One chosen has the server answer for the file in it (Roslyn's <c>_vs_projectContext</c>): its
/// colours, diagnostics and what else is asked of it.</summary>
public partial class MainWindow
{
    private void AddTargetFramework()
    {
        actions!.Add(new IdeAction(A.TargetFramework, "Target Framework", "Code", context => ShowTargetFrameworks(context.File!, context.Editor))
        {
            WhyNot = context => context.File is not { } file ? Refusals.NoFile
                : file.ProjectContexts.Count < 2 ? $"{file.Name} is compiled for one target framework only"
                : null,
        });
        Editors.ForEachEditor(editor => editor.ContextSwitchRequested += (_, _) => Request(A.TargetFramework));
    }

    /// <summary>The file's contexts under the editor's switcher, the one the server answers in checked.</summary>
    private void ShowTargetFrameworks(OpenDocument document, Passenger.Editor.CodeEditor editor)
    {
        if (CSharpService?.For(document) is not { } features)
        {
            return;
        }
        List<Control> items = [];
        foreach (ProjectContext context in features.Contexts)
        {
            MenuItem item = new()
            {
                Header = context.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = context.Label == document.ProjectContext,
            };
            item.Click += (_, _) => features.Choose(context);
            items.Add(item);
        }
        ContextMenu menu = new()
        {
            ItemsSource = items,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            PlacementRect = editor.ContextSwitcherBounds,
        };
        Open(menu, editor);
    }
}
