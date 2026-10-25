using System.Text;
using System.Text.Json;

namespace Passenger.Lsp;

/// <summary>Reads the protocol's either-or fields: a result that may be a list or an object, a
/// location or a link, a command or a code action, a string or marked-up content.</summary>
public static class Shapes
{
    public static CompletionList? CompletionList(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.Array => new CompletionList { Items = result.Deserialize<List<CompletionItem>>(Json.Options) ?? [] },
        JsonValueKind.Object => result.Deserialize<CompletionList>(Json.Options),
        _ => null,
    };

    /// <summary>A definition's answer as plain locations: one location, several, or links (whose
    /// target selection is the name itself).</summary>
    public static IReadOnlyList<LspLocation> Locations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            return [Location(result)];
        }
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return result.EnumerateArray().Select(Location).ToList();
    }

    /// <summary>Inlay hints, each label as one string whether the server sent it whole or in parts.</summary>
    public static IReadOnlyList<LspInlayHint> InlayHints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<LspInlayHint> hints = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("position", out JsonElement position) || !item.TryGetProperty("label", out JsonElement label))
            {
                continue;
            }
            string text = label.ValueKind == JsonValueKind.Array
                ? string.Concat(label.EnumerateArray().Select(part => part.TryGetProperty("value", out JsonElement value) ? value.GetString() : null))
                : label.GetString() ?? string.Empty;
            hints.Add(new LspInlayHint(
                position.Deserialize<LspPosition>(Json.Options)!,
                text,
                item.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.Number ? kind.GetInt32() : null,
                item.TryGetProperty("paddingLeft", out JsonElement left) && left.ValueKind == JsonValueKind.True,
                item.TryGetProperty("paddingRight", out JsonElement right) && right.ValueKind == JsonValueKind.True,
                item.TryGetProperty("data", out JsonElement data) ? data.Clone() : null));
        }
        return hints;
    }

    public static IReadOnlyList<CodeAction> CodeActions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<CodeAction> actions = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            // A bare command has a string "command"; a code action's "command" (if any) is an object.
            if (item.TryGetProperty("command", out JsonElement command) && command.ValueKind == JsonValueKind.String)
            {
                LspCommand bare = item.Deserialize<LspCommand>(Json.Options)!;
                actions.Add(new CodeAction { Title = bare.Title, Command = bare });
            }
            else if (item.Deserialize<CodeAction>(Json.Options) is { } action)
            {
                actions.Add(action);
            }
        }
        return actions;
    }

    /// <summary>Text to show from a string, marked-up content, or a list of marked strings.</summary>
    public static string Text(JsonElement? content)
    {
        if (content is not { } value)
        {
            return string.Empty;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Object when value.TryGetProperty("value", out JsonElement text):
                return text.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                StringBuilder joined = new();
                foreach (JsonElement part in value.EnumerateArray())
                {
                    if (joined.Length > 0)
                    {
                        joined.Append("\n\n");
                    }
                    joined.Append(Text(part));
                }
                return joined.ToString();
            default:
                return string.Empty;
        }
    }

    /// <summary>A workspace edit as the edits it makes to each file, whichever of its two forms it
    /// came in. File creations, renames and deletions are reported apart.</summary>
    public static IReadOnlyList<FileEdits> WorkspaceEdit(JsonElement edit)
    {
        List<FileEdits> files = [];
        if (edit.TryGetProperty("documentChanges", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("textDocument", out JsonElement document) && change.TryGetProperty("edits", out JsonElement edits))
                {
                    int? version = document.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                    files.Add(new FileEdits(document.GetProperty("uri").GetString()!, edits.Deserialize<List<LspTextEdit>>(Json.Options) ?? [], null) { Version = version });
                }
                else if (change.TryGetProperty("kind", out JsonElement kind))
                {
                    files.Add(new FileEdits(change.TryGetProperty("uri", out JsonElement uri) ? uri.GetString()! : change.GetProperty("oldUri").GetString()!, [], change.Clone()));
                }
            }
        }
        else if (edit.TryGetProperty("changes", out JsonElement byUri) && byUri.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty file in byUri.EnumerateObject())
            {
                files.Add(new FileEdits(file.Name, file.Value.Deserialize<List<LspTextEdit>>(Json.Options) ?? [], null));
            }
        }
        return files;
    }

    private static LspLocation Location(JsonElement item)
    {
        if (item.TryGetProperty("targetUri", out JsonElement target))
        {
            LspRange range = item.GetProperty("targetSelectionRange").Deserialize<LspRange>(Json.Options)!;
            return new LspLocation(target.GetString()!, range);
        }
        return item.Deserialize<LspLocation>(Json.Options)!;
    }
}

/// <summary>A workspace edit's change to one file: its edits (or an operation: create, rename, delete), and the version of
/// the document they were made for, when the server says.</summary>
public sealed record FileEdits(string Uri, IReadOnlyList<LspTextEdit> Edits, JsonElement? Operation)
{
    public int? Version { get; init; }
}
