namespace Passenger.App;

/// <summary>Which regions a file opens folded: those it was folded with when it last closed (kept with the folder,
/// after a restart too, in its session's <see cref="ProjectSession.Folds"/>), else those Settings' Code Folding folds
/// by default. Code, Folding's actions are the editor's own.</summary>
public partial class MainWindow
{
    private void AddFolding()
    {
        Editors.Opened += (_, document) =>
            document.FoldsAtFirst = session.Folds.Of(document.Path) ?? (region => settings.FoldsByDefault(region.Kind));
        Editors.Closed += (_, document) => session.Folds.Remember(document);
    }
}
