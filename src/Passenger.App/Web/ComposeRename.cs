using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>A name of a compose file's own that Rename renames: its kind (service, volume, network, secret or config), the
/// name, the file that declares it (the file itself, or the one an extends names) and its stretch at the place asked.</summary>
public sealed record ComposeRenaming(string Kind, string Name, string Home, TextRange At)
{
    /// <summary>The top-level key that declares it (services, volumes…).</summary>
    public string Key => Kind + "s";
}

/// <summary>Rename of a compose file's service, volume, network, secret or config where it is declared and everywhere
/// the files Compose reads with it use it: what the place names read from the YAML server's outline
/// (<see cref="ComposeDeclarations.Named"/>, or a declaration's key), and where each file writes it read from its text
/// (<see cref="ComposeFixes.References"/>). Its files are the one asked in, the one Compose reads with it (a base and
/// its override), and for a service an extends names in another file, that file with the compose files beside
/// it.</summary>
public static partial class ComposeRename
{
    /// <summary>What Rename renames at an offset of a compose file whose outline the server gave for <paramref name="text"/>;
    /// null where the offset is on no name of the file's own as written (an image, a key of Compose's, an anchor).</summary>
    public static ComposeRenaming? At(string path, TextSnapshot text, IReadOnlyList<DocumentSymbol> outline, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        LspPosition at = ServerLines.Of(text).PositionOf(offset);
        List<DocumentSymbol> chain = DocumentSymbol.PathTo(outline, at);
        ComposeName? named;
        if (chain is [{ Name: "services" or "volumes" or "networks" or "secrets" or "configs" } top, { } declared] && declared.SelectionRange.Holds(at))
        {
            named = new ComposeName(declared.Name, top.Name[..^1]);
        }
        else
        {
            int line = text.GetLineFromOffset(offset);
            named = ComposeDeclarations.Named(chain, at, text.GetLineText(line), offset - text.GetLineStart(line));
        }
        if (named is not { Name.Length: > 0 })
        {
            return null;
        }
        string home = named.File is { } other ? PathNames.Canonical(Path.Combine(Path.GetDirectoryName(path)!, other)) : path;
        ComposeRenaming renaming = new(named.Kind, named.Name, home, default);
        return Places(path, text.ToString(), renaming).FirstOrDefault(p => p.Start <= offset && offset <= p.End) is { Length: > 0 } here
            ? renaming with { At = here }
            : null;
    }

    /// <summary>The files a rename asked in a file may change: that file, the one Compose reads with it, and the file declaring
    /// the name with the one Compose reads with that; for a service, also the files Compose reads unasked beside the file
    /// declaring it, which may extend it (a common.yaml beside its compose.yaml). Each once.</summary>
    public static IReadOnlyList<string> Files(string path, ComposeRenaming renaming)
    {
        List<string> files = [];
        IReadOnlyList<string> beside = renaming.Kind == "service" ? ComposeCheck.Unasked(Path.GetDirectoryName(renaming.Home)!) : [];
        foreach (string? file in (string?[])[path, ComposeCheck.ReadWith(path)?.Partner, renaming.Home, ComposeCheck.ReadWith(renaming.Home)?.Partner, .. beside])
        {
            if (file is not null && !files.Any(f => PathNames.Same(f, file)))
            {
                files.Add(file);
            }
        }
        return files;
    }

    /// <summary>Why a name cannot be the new one: not a name Compose takes (letters, digits, '.', '_' and '-', as its schema
    /// has every name), or one the file declaring it, or the one Compose reads with that, declares of its kind already
    /// (<paramref name="texts"/> reads a file as the editor has it, else the disk's); null when it can be.</summary>
    public static string? Problem(ComposeRenaming renaming, string name, Func<string, string> texts)
    {
        if (!Valid().IsMatch(name))
        {
            return name.Length == 0 ? $"A {renaming.Kind}'s name is not empty" : $"A {renaming.Kind}'s name has only letters, digits, dots, dashes and underscores";
        }
        foreach (string file in Model(renaming))
        {
            if (name != renaming.Name && ComposeText.Of(texts(file)).At([renaming.Key, name]) is not null)
            {
                return $"{Path.GetFileName(file)} has a {renaming.Kind} named {name} already";
            }
        }
        return null;
    }

    /// <summary>The edits that rename the name in a file's text as it is: the new name in each of its places.</summary>
    public static IReadOnlyList<TextEdit> Edits(string file, string text, ComposeRenaming renaming, string name) =>
        [.. Places(file, text, renaming).Select(p => new TextEdit(p.Start, p.Length, name))];

    /// <summary>Where a file's text writes the name, in order: in the files of its model (the one declaring it and the one
    /// Compose reads with it) its declaration, each place a service uses it and an extends of it naming no file; in any
    /// file, an extends of it naming one of those files.</summary>
    public static IReadOnlyList<TextRange> Places(string file, string text, ComposeRenaming renaming)
    {
        ComposeText read = ComposeText.Of(text);
        bool model = Model(renaming).Any(f => PathNames.Same(f, file));
        List<TextRange> places = [];
        if (model && read.At([renaming.Key, renaming.Name]) is { } declared)
        {
            places.Add(ComposeFixes.KeyName(text, declared));
        }
        foreach (ComposeEntry service in ComposeFixes.Children(read, ["services"]))
        {
            string named = service.Path[^1];
            if (model)
            {
                places.AddRange(ComposeFixes.References(read, text, new ComposeFault(renaming.Kind, named, renaming.Name)).Select(r => r.Name));
            }
            if (renaming.Kind != "service")
            {
                continue;
            }
            string[] extends = ["services", named, "extends"];
            ComposeEntry? extended = read.At(extends) is { Scalar: not null } brief ? brief : read.At([.. extends, "service"]);
            string? from = read.At([.. extends, "file"])?.Scalar;
            if (extended?.Scalar == renaming.Name
                && (from is null ? model : Model(renaming).Any(f => PathNames.Same(f, PathNames.Canonical(Path.Combine(Path.GetDirectoryName(file)!, from))))))
            {
                places.Add(ComposeFixes.ScalarPart(text, extended, 0, renaming.Name.Length));
            }
        }
        return [.. places.Distinct().OrderBy(p => p.Start)];
    }

    /// <summary>The files whose one model declares the name: the file declaring it, and the one Compose reads with it.</summary>
    private static IEnumerable<string> Model(ComposeRenaming renaming) =>
        ComposeCheck.ReadWith(renaming.Home)?.Partner is { } partner ? [renaming.Home, partner] : [renaming.Home];

    /// <summary>A name as Compose's schema takes one, of any kind.</summary>
    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex Valid();
}
