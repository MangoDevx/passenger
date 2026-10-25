using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Places in a file as a language server counts its lines (<see cref="ServerLines"/>) and as the editor does,
/// each way read in the file's text: as the editor has it when open, else as its edits read it from disk
/// (<see cref="WorkspaceEdits"/>), whose lone '\r's become '\n's one for one, so the server's lines are the same.</summary>
public static class ServerPlaces
{
    /// <summary>A file's text as the editor has it, else as read from disk (<see cref="EditorArea.TextOf"/>); null when it
    /// cannot be read.</summary>
    public static TextSnapshot? TextOf(EditorArea editors, string path) => editors.TextOf(path);

    /// <summary>A file's text as its edits read it from disk (<see cref="EditorArea.Read"/>); null when it cannot be read.
    /// Safe from any thread.</summary>
    public static TextSnapshot? Read(string path) => EditorArea.Read(path);

    /// <summary>Files' texts for a list of places, each read once for it (<see cref="TextOf"/>).</summary>
    public static Func<string, TextSnapshot?> Texts(EditorArea editors)
    {
        Dictionary<string, TextSnapshot?> read = new(PathNames.Comparer);
        return path => read.TryGetValue(path, out TextSnapshot? text) ? text : read[path] = TextOf(editors, path);
    }

    /// <summary>A server's position as the editor's line and column in a text; as the server's in a file that cannot be
    /// read, the best there is to say of it.</summary>
    public static TextPosition PositionOf(TextSnapshot? text, LspPosition at) =>
        text is null ? new TextPosition(at.Line, at.Character) : text.GetPosition(ServerLines.Of(text).OffsetOf(at));

    /// <summary>A server's position in a file as the editor's line and column there.</summary>
    public static TextPosition PositionOf(EditorArea editors, string path, LspPosition at) => PositionOf(TextOf(editors, path), at);

    /// <summary>The editor's line and column in a text as the server's; as they are in a file that cannot be read.</summary>
    public static LspPosition ServerPositionOf(TextSnapshot? text, TextPosition at) =>
        text is null ? new LspPosition(at.Line, at.Column) : ServerLines.Of(text).PositionOf(text.GetOffset(at));

    /// <summary>A text's lines as the server counts them where they are not the editor's, else null (as for a text that
    /// could not be read): what a long list of places keeps of each file it read, since few hold such a break.</summary>
    public static ServerLines? Differing(TextSnapshot? text) =>
        text is not null && ServerLines.Of(text) is { } lines && lines.LineCount != text.LineCount ? lines : null;

    /// <summary>A server's position as the editor's line and column through lines kept by <see cref="Differing"/>.</summary>
    public static TextPosition PositionOf(ServerLines? lines, LspPosition at) =>
        lines is null ? new TextPosition(at.Line, at.Character) : lines.Text.GetPosition(lines.OffsetOf(at));

    /// <summary>Whether Roslyn's compilers read the file (C#, Visual Basic, Razor), so the lines a compiler's message or a
    /// stack frame (its PDB's) names in it are counted as the server counts them, to be read through its text.</summary>
    public static bool CountedByRoslyn(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csx" or ".vb" or ".razor" or ".cshtml";

    /// <summary>Whether a file is XAML, whose compilers (Avalonia's XamlIl, WPF's markup compiler) count its lines as an XML
    /// reader does (<see cref="ServerLines.OfXml"/>) in the places they write: a PDB's sequence points, an element's own.</summary>
    public static bool CountedAsXml(string path) => Xaml.XamlService.IsXaml(path);

    /// <summary>Whether a file's compiler counts its lines apart from the editor's, Roslyn's or XAML's.</summary>
    public static bool CountedApart(string path) => CountedByRoslyn(path) || CountedAsXml(path);

    /// <summary>A text's lines as its file's compiler counts them (Roslyn's, else an XML reader's for XAML) where they are
    /// not the editor's, else null, as for a text that could not be read or a file no such compiler reads.</summary>
    public static ServerLines? Compiled(string path, TextSnapshot? text) =>
        text is null ? null
        : CountedByRoslyn(path) ? Differing(text)
        : CountedAsXml(path) && ServerLines.OfXml(text) is { } lines && lines.LineCount != text.LineCount ? lines : null;

    /// <summary>A place a compiler wrote of a file (a PDB's line, a XAML element's; from zero) as the editor's line and
    /// column in the file's text; as written in a file that cannot be read.</summary>
    public static TextPosition PositionOfCompiled(string path, TextSnapshot? text, LspPosition at) => PositionOf(Compiled(path, text), at);

    /// <summary>The editor's line and column in a file's text as its compiler counts them; as they are in a file that
    /// cannot be read.</summary>
    public static LspPosition CompiledPositionOf(string path, TextSnapshot? text, TextPosition at) =>
        text is not null && Compiled(path, text) is { } lines ? lines.PositionOf(text.GetOffset(at)) : new LspPosition(at.Line, at.Column);

    /// <summary>Edits in a text's offsets as the server's, in its lines, for a workspace edit applied to that text.</summary>
    public static List<LspTextEdit> EditsOf(TextSnapshot text, IEnumerable<TextEdit> edits)
    {
        ServerLines lines = ServerLines.Of(text);
        return [.. edits.OrderBy(e => e.Start).Select(e => new LspTextEdit(
            lines.RangeOf(TextRange.FromBounds(Math.Min(e.Start, text.Length), Math.Min(e.End, text.Length))), e.Text))];
    }
}
