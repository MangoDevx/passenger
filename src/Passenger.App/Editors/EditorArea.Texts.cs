using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Editors;

public sealed partial class EditorArea
{
    /// <summary>The open file at a path, however it is spelled (<see cref="PathNames.Same"/>); null for one not open.</summary>
    public OpenDocument? Find(string path) => documents.FirstOrDefault(d => PathNames.Same(d.Path, path));

    /// <summary>A file's text as the editor has it when its tab holds it (<see cref="OpenDocument.HoldsText"/>), else as
    /// read from disk (<see cref="Read"/>); null when it cannot be read.</summary>
    public TextSnapshot? TextOf(string path) => Find(path) is { HoldsText: true } open ? open.Text.Snapshot : Read(path);

    /// <summary>The open files' texts as the editor has them now, for work off the UI thread to read what a person sees.</summary>
    public OpenTexts Texts() => new(documents);

    /// <summary>A file's text from disk as the editor reads it (<see cref="FileText.Load"/>), the one read of a closed
    /// file's text; null for one not there, binary or unreadable. Safe from any thread.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? FileText.Load(path)?.Text : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file's text from disk (<see cref="ReadText"/>) as a snapshot. Safe from any thread.</summary>
    public static TextSnapshot? Read(string path) => ReadText(path) is { } text ? TextSnapshot.From(text) : null;
}
