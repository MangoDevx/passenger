using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>Configuration known from what the app reads (the extension's one reading of a project's keys, through
/// <see cref="Handlers.ConnectionReads"/>): completion of a section's keys and of a value's choices in
/// appsettings*.json and the project's secrets.json, merged ahead of the JSON server's items; Go to Declaration from a
/// key there to the options property or the code that reads it, and from a read in C# to where each file gives it; and
/// the fixes of a read's warning that nothing gives its key, which set it in appsettings.json, or in the user secrets
/// for a secret's.</summary>
public sealed partial class CodeAssist
{
    /// <summary>The projects a settings file or a secrets.json configures (the session's <see cref="ProjectSession.ConfiguredBy"/>).</summary>
    public Func<string, IReadOnlyList<ConfigurationProject>>? ConfiguredBy { get; set; }

    /// <summary>The configuration of the project a C# file is of (the session's <see cref="ProjectSession.ConfigurationOf(string)"/>).</summary>
    public Func<string, ConfigurationProject?>? ConfigurationOf { get; set; }

    /// <summary>The kind a configuration key is listed as: a property's.</summary>
    private const int KeyItemKind = 10;

    /// <summary>Every configuration key the projects' code reads or binds, asked of Passenger's extension for the code as the server
    /// holds it; none without the server.</summary>
    private async Task<IReadOnlyList<ConfigurationKey>> KeysOfAsync(IReadOnlyList<ConfigurationProject> projects, CancellationToken cancel = default)
    {
        if (service()?.Session is not { } roslyn || projects.Count == 0)
        {
            return [];
        }
        try
        {
            return (await roslyn.ExtensionAsync<ConnectionReadsFound>(Handlers.ConnectionReads,
                new ConnectionReadsRequest([.. projects.Select(p => p.Project)], Keys: true), cancel)).Keys ?? [];
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    /// <summary>What completion offers in a settings file or a project's secrets.json where a key or a value is typed: the
    /// section's keys the code reads or binds (an options class's properties with their types), those the object has already
    /// left out, or a bool's and an enum's values; with where they go and the text after the caret they replace (a name's
    /// closing quote). Null in any other file, or where the code reads nothing there.</summary>
    private async Task<(int Start, IReadOnlyList<CompletionItem> Items, string Tail)?> SettingsCompletionAsync(WebDocument web, TextSnapshot text, int caret,
        CancellationToken cancel)
    {
        if (web.Kind != WebServerKind.Json || editors.Active is not { } document || ConfiguredBy?.Invoke(document.Path) is not { Count: > 0 } projects)
        {
            return null;
        }
        string written = text.ToString();
        if (JsonSpot.At(written, caret) is not { } spot)
        {
            return null;
        }
        IReadOnlyList<ConfigurationKey> keys = await KeysOfAsync(projects, cancel);
        string[] path = [.. spot.Path.SelectMany(name => name.Split(':'))];
        IReadOnlyList<CompletionItem> items = spot.AtName ? KeyItems(keys, path, spot, written) : ValueItems(keys, [.. path, .. (spot.Property ?? string.Empty).Split(':')]);
        return items.Count == 0 ? null : (spot.Start, items, caret < spot.End ? written[caret..spot.End] : string.Empty);
    }

    /// <summary>A settings file's keys and values listed with no JSON server to merge them with (none runs without Node.js).</summary>
    private async Task StartSettingsCompletion(WebDocument web)
    {
        completing?.Cancel();
        CancellationTokenSource cancel = new();
        completing = cancel;
        int caret = Editor.Selections.Primary.Caret;
        TextSnapshot asked = Editor.Document.Snapshot;
        if (await SettingsCompletionAsync(web, asked, caret, cancel.Token) is not { } own || cancel.IsCancellationRequested || Editor.Document.Snapshot != asked)
        {
            return;
        }
        templatesOffered.Clear();
        postfixOffered.Clear();
        xamlOffered.Clear();
        open = new Completion(own.Items, own.Start, [], false)
        {
            Settings = true,
            Tail = own.Tail,
            Lead = own.Start < caret ? asked.GetText(own.Start, caret - own.Start) : string.Empty,
        };
        Filter();
    }

    /// <summary>The keys the code reads or binds one level under a section, each once: its type and what declares it where the
    /// key holds a value, else a section; a name the object has already left out.</summary>
    private static IReadOnlyList<CompletionItem> KeyItems(IReadOnlyList<ConfigurationKey> keys, string[] path, JsonSpot spot, string written)
    {
        Dictionary<string, ConfigurationKey?> offered = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigurationKey key in keys)
        {
            string[] segments = key.Key.Split(':');
            if (key.Key.Length == 0 || segments.Length <= path.Length || segments[path.Length] == "*"
                || !segments.Take(path.Length).Zip(path).All(p => p.First == "*" || p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string name = segments[path.Length];
            bool leaf = segments.Length == path.Length + 1;
            // The options property's own words over a read's or a section's.
            if (!offered.TryGetValue(name, out ConfigurationKey? known) || leaf && (known is null || known.Kind != ConfigurationKinds.Option && key.Kind == ConfigurationKinds.Option))
            {
                offered[name] = leaf ? key : known;
            }
        }
        string typed = spot.Typed(written, Math.Min(spot.End, written.Length));
        int after = spot.End;
        while (after < written.Length && char.IsWhiteSpace(written[after]))
        {
            after++;
        }
        bool named = after < written.Length && written[after] == ':';
        bool more = after < written.Length && written[after] == '"';
        return [.. offered.Where(o => !spot.Names.Any(n => n.Equals(o.Key, StringComparison.OrdinalIgnoreCase) && !n.Equals(typed, StringComparison.Ordinal)))
            .OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => new CompletionItem
            {
                Label = o.Key,
                Kind = KeyItemKind,
                Detail = o.Value is { } key ? key.Owner is { } owner ? $"{key.TypeName} — {owner}" : $"{key.TypeName} — read by the code" : "section",
                InsertText = named ? Quoted(o.Key) : $"{Quoted(o.Key)}: {Placeholder(o.Value)}{(more ? "," : string.Empty)}",
                InsertTextFormat = 2,
                SortText = " " + o.Key,
            })];

        static string Quoted(string name) => System.Text.Json.JsonSerializer.Serialize(name, JsonText.Written).Replace("$", "\\$", StringComparison.Ordinal);

        static string Placeholder(ConfigurationKey? key) => key?.Type switch
        {
            ConfigurationTypes.Boolean => "${1:false}",
            ConfigurationTypes.Number => "${1:0}",
            ConfigurationTypes.Enum when key.Values is [{ } first, ..] => $"\"${{1:{first}}}\"",
            ConfigurationTypes.Array => "[$1]",
            ConfigurationTypes.Object or ConfigurationTypes.Dictionary or null => "{$1}",
            _ => "\"$1\"",
        };
    }

    /// <summary>A key's values where its type has few: a bool's true and false, an enum's names.</summary>
    private static IReadOnlyList<CompletionItem> ValueItems(IReadOnlyList<ConfigurationKey> keys, string[] key)
    {
        ConfigurationKey? typed = keys.Where(k => k.Type is ConfigurationTypes.Boolean or ConfigurationTypes.Enum && ConfigurationFiles.Matches(k.Key, key))
            .OrderBy(k => k.Kind == ConfigurationKinds.Option ? 0 : 1).FirstOrDefault();
        return typed switch
        {
            { Type: ConfigurationTypes.Boolean } => [.. new[] { "true", "false" }.Select(v => new CompletionItem { Label = v, Kind = 12, Detail = typed.TypeName, InsertText = v, SortText = " " + v })],
            { Type: ConfigurationTypes.Enum, Values: { } names } => [.. names.Select(v => new CompletionItem
            {
                Label = v,
                Kind = 20,
                Detail = typed.TypeName,
                InsertText = System.Text.Json.JsonSerializer.Serialize(v, JsonText.Written),
                SortText = " " + v,
            })],
            _ => [],
        };
    }

    /// <summary>Go to Declaration from a key in a settings file or a secrets.json: the options property it sets, else the code
    /// that reads it or binds its section; false where the caret is on no key, for the JSON server's.</summary>
    private async Task<bool> SettingsGoTo(OpenDocument document, IReadOnlyList<ConfigurationProject> projects, int offset)
    {
        if (await SettingsDeclaredAsync(document, projects, offset) is not (string key, IReadOnlyList<ConfigurationKey> declared))
        {
            return false;
        }
        if (editors.Active != document)
        {
            return true;
        }
        string names = string.Join(" or ", projects.Select(p => p.Name));
        Go([.. declared.Select(k => new LspLocation(DocumentUris.FromPath(k.Path!), new LspRange(new LspPosition(k.At!.Line, k.At.Character), new LspPosition(k.End!.Line, k.End.Character))))],
            offset, "Declarations", $"No code of {names} reads {key}", [.. declared.Select(k => (string?)(k.Owner ?? $"{k.Key} ({k.Kind})"))]);
        return true;
    }

    /// <summary>The key at an offset of a settings file and what declares it in the projects' code: the options properties it
    /// sets first, then the bindings of its section, then the reads; null where the offset is on no key.</summary>
    private async Task<(string Key, IReadOnlyList<ConfigurationKey> Declared)?> SettingsDeclaredAsync(OpenDocument document, IReadOnlyList<ConfigurationProject> projects, int offset)
    {
        if (SettingsKeyAt(document.Text.Snapshot.ToString(), offset) is not { } key)
        {
            return null;
        }
        IReadOnlyList<ConfigurationKey> found = await KeysOfAsync(projects);
        string[] segments = key.Split(':');
        return (key, [.. found.Where(k => k.Path is not null && k.At is not null && k.End is not null && k.Key.Length > 0
                && (k.Kind == ConfigurationKinds.Option ? ConfigurationFiles.Matches(k.Key, segments) : k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k.Kind switch { ConfigurationKinds.Option => 0, ConfigurationKinds.Binding => 1, _ => 2 })
            .DistinctBy(k => (k.Path, k.At!.Line, k.At.Character))]);
    }

    /// <summary>Where Go to Declaration goes from a key of a settings file or a secrets.json, at the editor's lines; null for
    /// any other file or where no key is. Shared with the agents' definition.</summary>
    internal async Task<IReadOnlyList<(string Path, TextPosition Position)>?> SettingsDeclarationsAsync(OpenDocument document, int offset)
    {
        if (ConfiguredBy?.Invoke(document.Path) is not { Count: > 0 } projects
            || await SettingsDeclaredAsync(document, projects, offset) is not (_, IReadOnlyList<ConfigurationKey> declared))
        {
            return null;
        }
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(editors);
        return [.. declared.Select(k => (k.Path!, ServerPlaces.PositionOf(texts(k.Path!), new LspPosition(k.At!.Line, k.At.Character))))];
    }

    /// <summary>The key whose name, or whose value, an offset of a configuration file is on, its sections joined by ':'.</summary>
    internal static string? SettingsKeyAt(string text, int offset)
    {
        if (JsonSpot.At(text, offset) is not { } spot)
        {
            return null;
        }
        string? name = spot.AtName
            ? spot.End > spot.Start && text[spot.Start] == '"' ? Unquoted(text[spot.Start..spot.End]) : null
            : spot.Property;
        return name is { Length: > 0 } ? string.Join(':', [.. spot.Path, name]) : null;

        static string Unquoted(string written)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(written.EndsWith('"') && written.Length > 1 ? written : written + "\"") ?? string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
                return written.Trim('"');
            }
        }
    }

    /// <summary>Go to Declaration from a read of a configuration key in C#: where each file the project runs with gives it (its
    /// settings, a launch profile, its user secrets, a compose file, an AppHost), or saying none does; false where the caret
    /// is on no read of the project's, for the server's own. Only a string's place asks.</summary>
    private async Task<bool> ConfigurationGoTo(LspDocument lsp, int offset)
    {
        if (editors.Active is not { } document || await ReadAtAsync(lsp, document, offset) is not ({ } project, { } read))
        {
            return false;
        }
        Func<string, string?> text = service()?.Texts?.Invoke() ?? ConfigurationFiles.Disk;
        IReadOnlyList<(GivenKey Given, LspLocation Place)> given = await BackgroundWork.Run(() =>
        {
            Dictionary<string, ServerLines?> lines = new(StringComparer.Ordinal);
            return (IReadOnlyList<(GivenKey, LspLocation)>)[.. ConfigurationFiles.Where(ConfigurationFiles.Given(project, text), read.Key, read.Kind != ConfigurationKinds.Read)
                .Select(g =>
                {
                    if (!lines.TryGetValue(g.File!, out ServerLines? counted))
                    {
                        lines[g.File!] = counted = text(g.File!) is { } written ? ServerLines.Of(TextSnapshot.From(written)) : null;
                    }
                    LspPosition start = counted?.PositionOf(Math.Min(g.Start, counted.Text.Length)) ?? new LspPosition(0, 0);
                    LspPosition end = counted?.PositionOf(Math.Min(g.End, counted.Text.Length)) ?? start;
                    return (g, new LspLocation(DocumentUris.FromPath(g.File!), new LspRange(start, end)));
                })];
        });
        if (editors.Active != document)
        {
            return true;
        }
        Go([.. given.Select(g => g.Place)], offset, "Declarations", $"Nothing {project.Name} runs with gives {read.Key}",
            [.. given.Select(g => (string?)$"{Path.GetFileName(g.Given.File)}: {g.Given.Key} = {g.Given.Shown}")]);
        return true;
    }

    /// <summary>The read of a configuration key whose string an offset of a C# file is in, with its project's configuration; null
    /// elsewhere, and asking nothing where the offset is in no string.</summary>
    private async Task<(ConfigurationProject Project, ConfigurationKey Read)?> ReadAtAsync(LspDocument lsp, OpenDocument document, int offset, CancellationToken cancel = default)
    {
        if (Syntax is not { } parsed || !ReferenceEquals(parsed.Text, Editor.Document.Snapshot)
            || !parsed.Root.FindToken(Math.Max(0, offset - 1)).IsKind(SyntaxKind.StringLiteralToken) && !parsed.Root.FindToken(offset).IsKind(SyntaxKind.StringLiteralToken)
            || ConfigurationOf?.Invoke(document.Path) is not { } project)
        {
            return null;
        }
        LspPosition at = lsp.PositionOf(offset);
        IReadOnlyList<ConfigurationKey> keys = await KeysOfAsync([project], cancel);
        return keys.FirstOrDefault(k => k.Kind is ConfigurationKinds.Read or ConfigurationKinds.Section or ConfigurationKinds.Binding && k.Key.Length > 0
            && k.Path is { } path && Passenger.Core.PathNames.Same(path, document.Path) && k.At is { } start && k.End is { } end
            && start.Line == at.Line && start.Character <= at.Character && at.Character <= end.Character) is { } read ? (project, read) : null;
    }

    /// <summary>What the hover card says of a configuration key: on a read in C#, the key and where each file the project runs
    /// with gives it with its value (a user secret's always as dots, any other masked by Passenger's rule for secrets), or that
    /// nothing does; on a key in a settings file, the type the code reads it as and what declares it. None elsewhere.</summary>
    private async Task<string?> ConfigurationHoverAsync(int offset, CancellationToken cancel)
    {
        if (editors.Active is not { } document)
        {
            return null;
        }
        if (WebCurrent is { Kind: WebServerKind.Json } && ConfiguredBy?.Invoke(document.Path) is { Count: > 0 } projects)
        {
            if (SettingsKeyAt(document.Text.Snapshot.ToString(), offset) is not { } key)
            {
                return null;
            }
            string[] segments = key.Split(':');
            ConfigurationKey? read = (await KeysOfAsync(projects, cancel)).Where(k => k.Kind == ConfigurationKinds.Option ? ConfigurationFiles.Matches(k.Key, segments)
                : k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Kind == ConfigurationKinds.Option ? 0 : 1).FirstOrDefault();
            return read is null ? null : $"{key}: {read.TypeName}" + (read.Owner is { } owner ? $" ({owner})" : read.Kind == ConfigurationKinds.Read ? " (read by the code)" : string.Empty)
                + (read.Values is { Count: > 0 } names ? $"\nOne of {string.Join(", ", names)}" : string.Empty);
        }
        if (WebCurrent is not null || service()?.For(document)?.Lsp is not { } lsp || await ReadAtAsync(lsp, document, offset, cancel) is not ({ } project, { } found))
        {
            return null;
        }
        Func<string, string?> text = service()?.Texts?.Invoke() ?? ConfigurationFiles.Disk;
        IReadOnlyList<GivenKey> given = await BackgroundWork.Run(() => ConfigurationFiles.Where(ConfigurationFiles.Given(project, text), found.Key, found.Kind != ConfigurationKinds.Read));
        return given.Count == 0 ? $"{found.Key}: given by nothing {project.Name} runs with"
            : $"{found.Key}:\n" + string.Join('\n', given.Select(g => $"{(g.By == GivenBy.UserSecrets ? "user secrets" : Path.GetFileName(g.File))}"
                + (g.Key.Equals(found.Key, StringComparison.OrdinalIgnoreCase) ? string.Empty : $" ({g.Key})") + (g.Value is null ? string.Empty : $" = {g.Shown}")));
    }

    /// <summary>The fixes of a C# file's warnings, on an offset's line, that nothing gives a configuration key it reads
    /// (<see cref="CSharpDocument.ConfigurationMissingOn"/>), Passenger's own and listed first: its value asked for and set in the
    /// project's appsettings.json, or for a secret's name in its user secrets (masked as it is typed), one command of global
    /// undo each, worked out again as it is chosen and refused once something gives the key.</summary>
    public IReadOnlyList<EditorFix> ConfigurationFixes(OpenDocument file, int offset)
    {
        if (service()?.For(file) is not { Lsp: { } lsp } features)
        {
            return [];
        }
        return [.. features.ConfigurationMissingOn(lsp.PositionOf(offset).Line).DistinctBy(m => m.Key).Select(m => KeyFix(file, m.Range, m.Key, m.Secret))];
    }

    private EditorFix KeyFix(OpenDocument file, TextRange range, string key, bool secret) =>
        new(secret ? $"Add {key} to user secrets…" : $"Add {key} to appsettings.json…", "a configuration key nothing gives", range, string.Empty)
        {
            Again = _ => service()?.For(file)?.ConfigurationMissingOn(null).FirstOrDefault(m => m.Key == key) is { } now ? KeyFix(file, now.Range, key, secret) : null,
            Asks = new ValueAsked(secret ? $"The value of {key}, kept in the project's user secrets, out of its files" : $"The value of {key}, kept in the project's appsettings.json",
                secret, (value, _) => Task.FromResult(SetKey(file, key, secret, value))),
        };

    /// <summary>A configuration key set where its fix keeps it, as one command: the project's appsettings.json (made when it has
    /// none), or its user secrets as <c>dotnet user-secrets set</c> keeps them, giving the project a UserSecretsId first where it
    /// has none; the file's text as the editor has it when open, the read's warning pulled again; what it says never holds the value.</summary>
    private RefactorOutcome SetKey(OpenDocument file, string key, bool secret, string value)
    {
        if (ConfigurationOf?.Invoke(file.Path) is not { } project)
        {
            return RefactorOutcome.Refused($"{key} was not set: {file.Name} is in no project");
        }
        Func<string, string?> text = path => editors.Documents.FirstOrDefault(d => Passenger.Core.PathNames.Same(d.Path, path))?.Text.Snapshot.ToString()
            ?? ConfigurationFiles.Disk(path);
        if (ConfigurationFiles.Gives(ConfigurationFiles.Given(project, text), key, section: false))
        {
            return RefactorOutcome.Refused($"{key} was not set: {project.Name}'s configuration gives it already");
        }
        List<XamlFileChange> changes = [];
        string? path = secret ? project.SecretsIn(text) : project.Settings;
        string? madeId = null;
        if (path is null)
        {
            // No UserSecretsId: given one in the same command.
            madeId = Guid.NewGuid().ToString();
            path = Passenger.Core.PathNames.Canonical(UserSecrets.FileOf(madeId)!);
            changes.Add(new XamlFileChange(project.Project, now => ProjectEdits.WithProperty(now, project.Project, "UserSecretsId", madeId) is { Text: { } then } && then != now
                ? [Difference(now, then)] : []));
        }
        JsonNode written = JsonValue.Create(value);
        // User secrets keep a key whole, as dotnet user-secrets writes it, unless the file already nests its first section.
        IReadOnlyList<string> Where(string now) => secret && !Nests(now, key.Split(':')[0]) ? [key] : key.Split(':');
        string? was = text(path);
        bool made = string.IsNullOrWhiteSpace(was);
        if (made)
        {
            if (File.Exists(path))
            {
                return RefactorOutcome.Refused($"{key} was not set: {Path.GetFileName(path)} is empty");
            }
            (string? created, string? problem) = JsonText.SetKey("{\n}\n", secret ? [key] : key.Split(':'), written);
            if (created is null)
            {
                return RefactorOutcome.Refused($"{key} was not set: {problem}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            changes.Add(new XamlFileChange(path, null, created));
        }
        else
        {
            (string? then, string? problem) = JsonText.SetKey(was!, Where(was!), written);
            if (then is null)
            {
                return RefactorOutcome.Refused($"{key} was not set: {Path.GetFileName(path)} was not changed, {problem}");
            }
            changes.Add(new XamlFileChange(path, now => JsonText.SetKey(now, Where(now), written) is { Text: { } changed } && changed != now ? [Difference(now, changed)] : []));
        }
        if (ApplyFileChanges($"Add {key}", changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        service()?.For(file)?.RefreshDiagnostics();
        string where = secret ? $"{project.Name}'s user secrets" : $"{project.Name}'s appsettings.json";
        List<RefactoredFile> files = [new RefactoredFile(path, made ? "created" : "changed", [])];
        if (madeId is not null)
        {
            files.Add(new RefactoredFile(project.Project, "changed", []));
        }
        return new RefactorOutcome($"Added {key} to {where}" + (madeId is null ? string.Empty : ", giving it a UserSecretsId"), null, files, []);
    }

    /// <summary>Whether a configuration file's root holds a section as an object, letter case aside.</summary>
    private static bool Nests(string text, string section)
    {
        try
        {
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(text,
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return read.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && read.RootElement.EnumerateObject()
                .Any(p => p.Name.Equals(section, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.Object);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The one edit that makes one text another: what lies between their common start and their common end.</summary>
    private static TextEdit Difference(string was, string now)
    {
        int start = 0;
        while (start < was.Length && start < now.Length && was[start] == now[start])
        {
            start++;
        }
        int end = 0;
        while (end < was.Length - start && end < now.Length - start && was[^(end + 1)] == now[^(end + 1)])
        {
            end++;
        }
        return new TextEdit(start, was.Length - start - end, now[start..^end]);
    }
}
