using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>Passenger's one rule for what is a secret, and its one mask: a value whose name says it is one
/// (<see cref="Secret"/>) shows as the same dots whatever its length; in any other value, a URL's password
/// (<c>postgres://app:••••••@db</c>) and a connection string's part so named (<c>Password=••••••</c>). An empty value
/// hides nothing and shows as it is.</summary>
public static partial class ContainerSecrets
{
    public const string Masked = "••••••";

    /// <summary>What makes a name a secret's anywhere in it, its case, underscores, dashes and dots aside (DBPASSWORD,
    /// SECRETKEY, ConnectionStrings__Shop).</summary>
    private static readonly string[] Anywhere = ["PASSWORD", "PASSWD", "SECRET", "TOKEN", "CREDENTIAL", "CONNECTIONSTRING"];

    /// <summary>What makes a name a secret's as a word of it (DB_PASS, MYSQL_PW, DB_PWD, API_KEY), or its plural.</summary>
    private static readonly HashSet<string> Whole = new(StringComparer.Ordinal) { "PASS", "PW", "PWD", "KEY", "AUTHORIZATION" };

    /// <summary>The words that end in KEY and name no key (a word ending in KEY otherwise is one: APIKEY, ACCESSKEY).</summary>
    private static readonly HashSet<string> Ordinary = new(StringComparer.Ordinal)
    {
        "MONKEY", "DONKEY", "TURKEY", "JOCKEY", "HOCKEY", "WHISKEY", "LACKEY", "FLUNKEY", "MALARKEY", "HOTKEY", "TURNKEY",
    };

    /// <summary>The shell's working folders, paths a compose file's volumes are written with, whose PWD is no password's.</summary>
    private static readonly HashSet<string> Folders = new(StringComparer.Ordinal) { "PWD", "OLDPWD" };

    /// <summary>Whether a name's value is a secret: it holds one of <see cref="Anywhere"/>, or a word of it (between
    /// underscores, dashes or dots, or where its case turns, as apiKey's Key) is one of <see cref="Whole"/> or its plural,
    /// or ends in KEY but for <see cref="Ordinary"/>'s words; so DBPASSWORD, APIKEY and DB_PASS are and MONKEY is not,
    /// nor the shell's own PWD and OLDPWD (<see cref="Folders"/>).</summary>
    public static bool Secret(string name)
    {
        if (Folders.Contains(name))
        {
            return false;
        }
        string squeezed = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (Anywhere.Any(s => squeezed.Contains(s, StringComparison.Ordinal)))
        {
            return true;
        }
        foreach (string word in Words().Split(name).Where(w => w.Length > 0).Select(w => w.ToUpperInvariant()))
        {
            string single = word.Length > 1 && word[^1] == 'S' ? word[..^1] : word;
            if (Whole.Contains(word) || Whole.Contains(single)
                || (word.EndsWith("KEY", StringComparison.Ordinal) && !Ordinary.Contains(word))
                || (single.EndsWith("KEY", StringComparison.Ordinal) && !Ordinary.Contains(single)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A variable's value as it is shown.</summary>
    public static string Shown(string name, string value) => value.Length > 0 && Secret(name) ? Masked : InValue(value);

    /// <summary>A value whose name says nothing, as it is shown: a URL's password and a connection string's part named as a
    /// secret's masked, the rest as it is.</summary>
    public static string InValue(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }
        string bare = AuthorizationCredentials().Replace(value, $"${{scheme}} {Masked}");
        bare = UrlPassword().Replace(bare, $"${{head}}{Masked}@");
        return ConnectionPart().Replace(bare, part => Secret(part.Groups["name"].Value.Trim()) && part.Groups["value"].Length > 0
            ? $"{part.Groups["head"].Value}{Masked}" : part.Value);
    }

    /// <summary>The shortest value a secret's name holds that <see cref="InSaid"/> masks wherever it stands: a shorter one
    /// would mask words and numbers that are no secret (a line's number, "true").</summary>
    private const int ShortestKnown = 4;

    /// <summary>What a program said of its failure (the docker CLI's and Compose's errors, the aspire CLI's, a resource
    /// service's status), masked wherever it is shown or logged: each value it knows to be a secret's (<paramref name="known"/>,
    /// this process's environment's variables whose names are a secret's, and those of the .env in <paramref name="folder"/>)
    /// as the dots wherever it stands; then on each line a <c>NAME=value</c> or <c>"NAME": "value"</c> whose name is a
    /// secret's, an Authorization header, a URL's credentials and a connection string's secret part.</summary>
    public static string InSaid(string said, string? folder = null, IEnumerable<string>? known = null) => Masking(folder, known)(said);

    /// <summary>A command line as Passenger logs or shows it (<see cref="ChildProcesses.Shown"/>): each word masked on its own
    /// as <see cref="InSaid"/> masks what a program said (so a <c>--password=…</c> masks its value, not the words after it),
    /// knowing too the values of the child's own environment whose names are a secret's.</summary>
    public static string CommandLine(System.Diagnostics.ProcessStartInfo start) =>
        ChildProcesses.Display(start, Masking(known: [.. start.Environment.Where(v => v.Value is { Length: > 0 } && Secret(v.Key)).Select(v => v.Value!)]));

    /// <summary><see cref="InSaid"/> for much that is said a piece at a time (a CLI's output streamed into a console), the
    /// secrets it knows read once.</summary>
    public static Func<string, string> Masking(string? folder = null, IEnumerable<string>? known = null)
    {
        string[] secrets = [.. Known(folder, known).Where(s => s.Length >= ShortestKnown).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
        return said =>
        {
            foreach (string secret in secrets)
            {
                said = said.Replace(secret, Masked, StringComparison.Ordinal);
            }
            return string.Join('\n', said.Split('\n').Select(line =>
            {
                // A Bearer's token first, which a NAME=value would otherwise leave after the scheme it masked.
                string bare = InValue(line);
                bare = Assignment().Replace(bare, pair => Secret(pair.Groups["name"].Value) ? $"{pair.Groups["head"].Value}{Quoted(pair.Groups["value"].Value)}" : pair.Value);
                bare = Member().Replace(bare, pair => Secret(pair.Groups["name"].Value) ? $"{pair.Groups["head"].Value}{Quoted(pair.Groups["value"].Value)}" : pair.Value);
                bare = AuthorizationHeader().Replace(bare, $"${{head}}{Masked}");
                return UrlUser().Replace(bare, $"${{scheme}}{Masked}@");
            }));
        };

        static string Quoted(string value) => value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? $"{value[0]}{Masked}{value[0]}" : Masked;
    }

    /// <summary>The values a failure may echo that are known to be secrets: those given, this process's environment's whose
    /// names are a secret's, and those a folder's .env gives such names (<c>KEY=value</c>, quoted or not, after an
    /// <c>export</c> or not; a line it cannot read passed over).</summary>
    private static IEnumerable<string> Known(string? folder, IEnumerable<string>? known)
    {
        foreach (string given in known ?? [])
        {
            yield return given;
        }
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && Secret(name) && variable.Value is string value)
            {
                yield return value;
            }
        }
        string[] lines = [];
        try
        {
            if (folder is not null && File.Exists(Path.Combine(folder, ".env")))
            {
                lines = File.ReadAllLines(Path.Combine(folder, ".env"));
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // One that cannot be read now gave Compose nothing either.
        }
        foreach (string line in lines)
        {
            if (EnvLine().Match(line) is { Success: true } entry && Secret(entry.Groups["name"].Value))
            {
                string value = entry.Groups["value"].Value.Trim();
                yield return value.Length > 1 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1]
                    : value.IndexOf(" #", StringComparison.Ordinal) is int comment and >= 0 ? value[..comment].TrimEnd() : value;
            }
        }
    }

    /// <summary>A public navigable HTTP URL, without user information, secret query values, or fragments.</summary>
    public static string? PublicHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || value.Any(char.IsControl))
        {
            return null;
        }
        UriBuilder safe = new(uri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
        List<string> query = [];
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string name = Uri.UnescapeDataString((equals < 0 ? part : part[..equals]).Replace('+', ' '));
            if (Secret(name))
            {
                continue;
            }
            if (equals < 0)
            {
                query.Add(part);
                continue;
            }
            string original = Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
            string shown = InValue(original);
            query.Add(shown == original ? part : $"{part[..(equals + 1)]}{Uri.EscapeDataString(shown)}");
        }
        safe.Query = string.Join('&', query);
        return safe.Uri.AbsoluteUri;
    }

    /// <summary>Whether what a line has before and after a place makes it a URL's password (<c>postgres://user:</c> before,
    /// <c>@db</c> after), as a compose file's <c>${PGPASS}</c> there is.</summary>
    public static bool AtUrlPassword(string before, string after) => UserBefore().IsMatch(before) && HostAfter().IsMatch(after);

    /// <summary>An environment entry as the engine lists it ("NAME=value"), its secrets masked.</summary>
    public static string Entry(string entry)
    {
        int equals = entry.IndexOf('=', StringComparison.Ordinal);
        return equals <= 0 ? entry : $"{entry[..(equals + 1)]}{Shown(entry[..equals], entry[(equals + 1)..])}";
    }

    /// <summary>Masks, in place, every environment list in what <c>docker inspect</c> said: each <c>Env</c> array of
    /// "NAME=value" strings anywhere in it (a container's and an image's Config, an older image's ContainerConfig).</summary>
    public static void Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject holder:
                foreach ((string name, JsonNode? value) in holder.ToList())
                {
                    if (name == "Env" && value is JsonArray entries)
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            if (entries[i] is JsonValue one && one.TryGetValue(out string? entry))
                            {
                                entries[i] = Entry(entry);
                            }
                        }
                    }
                    else
                    {
                        Mask(value);
                    }
                }
                break;
            case JsonArray items:
                foreach (JsonNode? item in items)
                {
                    Mask(item);
                }
                break;
        }
    }

    /// <summary>Where Inspect writes its copies, under a state folder.</summary>
    public static string InspectCopies(string stateFolder) => Path.Combine(stateFolder, "cache", "containers", "inspect");

    /// <summary>The state folders whose Inspect copies this process has looked through.</summary>
    private static readonly HashSet<string> Looked = new(StringComparer.Ordinal);

    /// <summary>Inspect's copies an earlier Passenger wrote before it masked them, deleted, once a process for each state
    /// folder: each copy whose masking would change it (a secret written whole), so a copy written masked, which masking
    /// leaves as it is, stays. Reads the disk, so off the UI thread. The paths deleted, canonical; none the second time.</summary>
    public static IReadOnlyList<string> ForgetUnmasked(string stateFolder)
    {
        lock (Looked)
        {
            if (!Looked.Add(PathNames.Canonical(stateFolder)))
            {
                return [];
            }
        }
        List<string> deleted = [];
        string folder = InspectCopies(stateFolder);
        if (!Directory.Exists(folder))
        {
            return deleted;
        }
        string[] copies;
        try
        {
            copies = Directory.GetFiles(folder, "*.json");
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            return deleted;
        }
        foreach (string copy in copies)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(copy)) is not { } read)
                {
                    continue;
                }
                string written = read.ToJsonString();
                Mask(read);
                if (read.ToJsonString() != written)
                {
                    string path = PathNames.Canonical(copy);
                    File.Delete(copy);
                    deleted.Add(path);
                }
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
            {
                // A copy that cannot be read or deleted now is looked at again by the next process.
                IdeErrors.Write($"Inspect's copy {copy} could not be looked through for unmasked secrets: {unread.Message}");
            }
        }
        return deleted;
    }

    [GeneratedRegex(@"[_.\-]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex Words();

    [GeneratedRegex(@"\b(?<scheme>Bearer|Basic)[ \t]+[A-Za-z0-9+/_~.=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationCredentials();

    [GeneratedRegex(@"(?<head>[A-Za-z][A-Za-z0-9+.\-]*://[^\s/@:]*:)[^\s@]*@")]
    private static partial Regex UrlPassword();

    /// <summary>A connection string's part, "name=value" between semicolons.</summary>
    [GeneratedRegex(@"(?<head>(?:^|;)(?<name>[^=;]+)=)(?<value>[^;]*)")]
    private static partial Regex ConnectionPart();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s/@]*:[^\s/@]*$")]
    private static partial Regex UserBefore();

    /// <summary>A <c>NAME=value</c> in what a program said (an option's <c>--password=</c>, a query's <c>token=</c>, a
    /// connection string's part), its value quoted or up to a space, comma, semicolon, ampersand or closing bracket.</summary>
    [GeneratedRegex("""(?<head>(?<![\w.])(?<name>[A-Za-z_][\w.\-]*)\s*=\s*)(?<value>"[^"]*"|'[^']*'|[^\s"',;&}\]]+)""")]
    private static partial Regex Assignment();

    /// <summary>A JSON or YAML member whose name is quoted (<c>"apiKey": "…"</c>), its value quoted or up to a comma or bracket.</summary>
    [GeneratedRegex("""(?<head>(?<q>["'])(?<name>[A-Za-z_][\w.\-]*)\k<q>\s*:\s*)(?<value>"[^"]*"|'[^']*'|[^\s,}\]]+)""")]
    private static partial Regex Member();

    [GeneratedRegex(@"(?<head>\bAuthorization\s*[:=]\s*)\S.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    /// <summary>A URL's user information with no password (<c>https://token@host</c>), which can be a token.</summary>
    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^\s/@:]+@")]
    private static partial Regex UrlUser();

    /// <summary>A .env file's line: <c>KEY=value</c>, after an <c>export</c> or not.</summary>
    [GeneratedRegex(@"^\s*(?:export\s+)?(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=(?<value>.*)$")]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^[^\s/@]*@")]
    private static partial Regex HostAfter();
}
