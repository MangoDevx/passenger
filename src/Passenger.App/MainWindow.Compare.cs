using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The Explorer's Compare: Compare Files on two files chosen together, Compare With... on one and a file the
/// person picks, each in the diff window.</summary>
public partial class MainWindow
{
    /// <summary>The two files chosen, or the row's file and one picked: the first on the left, the other on the right.</summary>
    private async Task<DiffWindow?> CompareAsync(ExplorerNode node)
    {
        List<string> files = [.. Explorer.SelectedNodes.Where(n => n is { Kind: NodeKind.File, IsSolutionItem: false, Path: not null }).Select(n => n.Path!)];
        if (files.Count == 2 && files.Contains(node.Path!))
        {
            return await CompareFilesAsync(files[0], files[1]);
        }
        IReadOnlyList<string> picked = await PickFiles("Select File to Compare", null, true);
        return picked.Count == 0 ? null : await CompareFilesAsync(node.Path!, PathNames.Canonical(picked[0]));
    }

    /// <summary>Two files side by side in the diff window, each change tinted and joined as Show Diff's are, the left's
    /// text as the editor shows it when it is open. The right side is the open file's own text when it is open, so an
    /// edit there is the file's; otherwise it is read-only.</summary>
    public async Task<DiffWindow> CompareFilesAsync(string left, string right)
    {
        string before = Editors.Documents.FirstOrDefault(d => PathNames.Same(d.Path, left)) is { } leftOpen
            ? leftOpen.Text.Snapshot.ToString()
            : FileText.Load(left)?.Text ?? string.Empty;
        OpenDocument? open = Editors.Documents.FirstOrDefault(d => PathNames.Same(d.Path, right));
        TextDocument mine = open?.Text ?? new TextDocument(FileText.Load(right)?.Text ?? string.Empty);
        TextSnapshot text = mine.Snapshot;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(before, text));
        return OpenLocalDiff(right, open, mine, diff, InFolder(left), other: left);
    }

    /// <summary>A file by its path in the open folder, as the Explorer shows it; one outside it by its whole path.</summary>
    private string InFolder(string path) => PathNames.Shown(path, Folder);
}
