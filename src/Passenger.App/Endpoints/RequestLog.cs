using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.App.Containers;
using Passenger.Profiler;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Endpoints;

/// <summary>A database command a request issued: its text, time, the rows it read or changed (once known), why it failed,
/// how many times its text ran in its request (a command run again and again for one request is an N+1), and whether it was slow.</summary>
public sealed class LiveCommand(string id, string text, double milliseconds, int? rows, string? error)
{
    /// <summary>A command over this long is marked slow.</summary>
    public const double SlowMilliseconds = 100;

    /// <summary>A text run this often within one request is marked as repeated.</summary>
    public const int RepeatedAt = 3;

    public string Id { get; } = id;
    public string Text { get; } = text;
    public double Milliseconds { get; } = milliseconds;
    /// <summary>The rows a non-query changed.</summary>
    public int? Rows { get; } = rows;

    /// <summary>How many reads its reader made (EF Core's ReadCount: each Read asked, the last that found no row too).</summary>
    public int? Reads { get; internal set; }

    public string? Error { get; } = error;
    public int Runs { get; internal set; } = 1;

    /// <summary>Its parameters as EF Core logged them ("@__id_0='2'"), their values masked by ContainerSecrets' rule by their
    /// names or their columns' (<see cref="CommandParameters"/>), '?' where the app logs none; null until heard.</summary>
    public string? Parameters { get; internal set; }

    public bool Repeated => Runs >= RepeatedAt;
    public bool Slow => Milliseconds >= SlowMilliseconds;
}

/// <summary>A request the app served, as it told: its method, path and query, when it began, its status and time once it
/// ended, the exception that ended it, the commands it issued, and the route of the folder's code it reached.</summary>
public sealed class LiveRequest(string trace, string method, string path, string query, DateTime began)
{
    public string Trace { get; } = trace;
    public string Method { get; } = method;
    public string Path { get; } = path;
    public string Query { get; } = query;
    public DateTime Began { get; } = began;
    public int? Status { get; internal set; }
    public double? Milliseconds { get; internal set; }
    public string? Error { get; internal set; }
    public List<LiveCommand> Commands { get; } = [];

    /// <summary>EF Core's logs of commands not yet told of, their values masked.</summary>
    internal List<string> Logged { get; } = [];

    /// <summary>Its commands by their text, so a command's runs are counted by its text alone, never by comparing it with each
    /// command before it (an N+1 of a thousand commands).</summary>
    internal Dictionary<string, List<LiveCommand>> ByText { get; } = new(StringComparer.Ordinal);

    /// <summary>Its own id, as the app told it, by which it is found and forgotten.</summary>
    internal string Request { get; init; } = string.Empty;

    public WebRoute? Route { get; internal set; }

    /// <summary>The program that served it, as the window names it.</summary>
    public string From { get; internal set; } = string.Empty;

    /// <summary>The project of the program that served it, when known.</summary>
    public string? Project { get; internal set; }

    /// <summary>Whether a command of it ran again and again (an N+1).</summary>
    public bool HasRepeats => Commands.Any(c => c.Repeated);
}

/// <summary>What a batch of events changed: the requests that came (oldest first), those already listed that changed (an end,
/// a command, a route), and those that went past <see cref="RequestLog.Most"/>; or everything, the list cleared.</summary>
public sealed record RequestChanges(IReadOnlyList<LiveRequest> Came, IReadOnlyCollection<LiveRequest> Changed, IReadOnlyCollection<LiveRequest> Went, bool All = false);

/// <summary>The live requests of the app followed, newest last, as many as <see cref="Most"/>: each event the stream thread
/// hears queued and taken on the UI thread a batch at a time, a command tied to its request by its activity's root id. What
/// the Requests window shows, kept whether or not its view is built (#447), with what the window says it follows.</summary>
public sealed class RequestLog
{
    /// <summary>The most requests kept, the oldest going first.</summary>
    public const int Most = 500;

    /// <summary>The folder of the state's cache SQL is opened from, read-only: a command's text, or an EF Core query's SQL the
    /// debugger read.</summary>
    public const string CommandCopies = "requests";

    /// <summary>Whether a file is SQL copied to open it (<see cref="CommandCopies"/>), which the editor never writes.</summary>
    public static bool IsCommandCopy(string path) =>
        System.IO.Path.GetDirectoryName(path) is { } folder && System.IO.Path.GetFileName(folder) == CommandCopies
        && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(folder)) == "cache" && path.EndsWith(".sql", StringComparison.Ordinal);

    private readonly ConcurrentQueue<(WebEvent Told, string From, string? Project)> heard = new();
    private readonly List<LiveRequest> requests = [];
    /// <summary>Requests by the program that served them and their activity's root id, and by that program and their own id:
    /// a request one service makes of another carries its trace on, so a root id alone names a request in each.</summary>
    private readonly Dictionary<(string From, string Id), LiveRequest> byTrace = [];
    private readonly Dictionary<(string From, string Id), LiveRequest> byRequest = [];

    /// <summary>A program's request by its own id, else by its activity's root id.</summary>
    private LiveRequest? Of(string from, string request, string trace) =>
        request.Length > 0 && byRequest.TryGetValue((from, request), out LiveRequest? found) ? found
        : trace.Length > 0 && byTrace.TryGetValue((from, trace), out LiveRequest? traced) ? traced
        : null;
    private readonly Func<string, string, string?, WebRoute?> routeOf;
    private int posted;

    /// <param name="routeOf">The route a method and path reach in a project (any when null), from the folder's routes.</param>
    public RequestLog(Func<string, string, string?, WebRoute?> routeOf) => this.routeOf = routeOf;

    public IReadOnlyList<LiveRequest> Requests => requests;

    /// <summary>What the Requests window says it follows, or why it follows nothing.</summary>
    public string Following
    {
        get => following;
        set
        {
            UiThread.Check();
            if (following != value)
            {
                following = value;
                FollowingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private string following = string.Empty;

    /// <summary>What the window says it follows changed.</summary>
    public event EventHandler? FollowingChanged;

    /// <summary>Requests came, ended, issued commands or went: what a batch changed, so a list redraws that alone.</summary>
    public event EventHandler<RequestChanges>? Changed;

    private readonly List<LiveRequest> came = [];
    private readonly HashSet<LiveRequest> changed = [];
    private readonly HashSet<LiveRequest> went = [];

    /// <summary>An event heard on the stream's thread, taken on the UI thread with those heard meanwhile, with the program that told it
    /// and its project.</summary>
    public void Hear(WebEvent told, string from, string? project)
    {
        heard.Enqueue((told, from, project));
        if (Interlocked.Exchange(ref posted, 1) == 0)
        {
            UiThread.Post(Take);
        }
    }

    /// <summary>The routes were read again: each request that reached none known yet is looked for again.</summary>
    public void Reroute()
    {
        UiThread.Check();
        foreach (LiveRequest request in requests.Where(r => r.Route is null))
        {
            request.Route = routeOf(request.Method, request.Path, request.Project);
            if (request.Route is not null)
            {
                changed.Add(request);
            }
        }
        Tell();
    }

    /// <summary>Forgets every request, as a new run begins or the person clears the list.</summary>
    public void Clear()
    {
        UiThread.Check();
        requests.Clear();
        byTrace.Clear();
        byRequest.Clear();
        came.Clear();
        changed.Clear();
        went.Clear();
        Changed?.Invoke(this, new RequestChanges([], [], [], All: true));
    }

    private void Take()
    {
        Volatile.Write(ref posted, 0);
        while (heard.TryDequeue(out (WebEvent Told, string From, string? Project) one))
        {
            Apply(one.Told, one.From, one.Project);
        }
        Tell();
    }

    /// <summary>Says what the batch changed, if anything: a request that came and went within it is neither.</summary>
    private void Tell()
    {
        if (came.Count + changed.Count + went.Count == 0)
        {
            return;
        }
        HashSet<LiveRequest> fresh = [.. came];
        RequestChanges told = new([.. came.Where(r => !went.Contains(r))], [.. changed.Where(r => !went.Contains(r) && !fresh.Contains(r))],
            [.. went.Where(r => !fresh.Contains(r))]);
        came.Clear();
        changed.Clear();
        went.Clear();
        Changed?.Invoke(this, told);
    }

    private void Apply(WebEvent told, string from, string? project)
    {
        switch (told)
        {
            case RequestBegan began:
                LiveRequest request = new(began.Trace, began.Method, began.Path, began.Query, began.At)
                {
                    From = from, Project = project, Route = routeOf(began.Method, began.Path, project), Request = began.Request,
                };
                requests.Add(request);
                came.Add(request);
                if (began.Trace.Length > 0)
                {
                    byTrace[(from, began.Trace)] = request;
                }
                if (began.Request.Length > 0)
                {
                    byRequest[(from, began.Request)] = request;
                }
                if (requests.Count > Most)
                {
                    LiveRequest oldest = requests[0];
                    if (byTrace.TryGetValue((oldest.From, oldest.Trace), out LiveRequest? traced) && traced == oldest)
                    {
                        byTrace.Remove((oldest.From, oldest.Trace));
                    }
                    if (byRequest.TryGetValue((oldest.From, oldest.Request), out LiveRequest? named) && named == oldest)
                    {
                        byRequest.Remove((oldest.From, oldest.Request));
                    }
                    requests.RemoveAt(0);
                    went.Add(oldest);
                }
                return;
            case RequestEnded ended when Of(from, ended.Request, ended.Trace) is { } done:
                done.Status = ended.Status;
                done.Milliseconds = (ended.At - done.Began).TotalMilliseconds;
                changed.Add(done);
                return;
            case RequestFailed failed when Of(from, failed.Request, failed.Trace) is { } broke:
                broke.Error = failed.Error;
                changed.Add(broke);
                return;
            case CommandRan ran when byTrace.TryGetValue((from, ran.Trace), out LiveRequest? issuer):
                LiveCommand command = new(ran.Id, ran.Text, ran.Milliseconds, ran.Rows, ran.Error);
                if (!issuer.ByText.TryGetValue(ran.Text, out List<LiveCommand>? same))
                {
                    issuer.ByText[ran.Text] = same = [];
                }
                same.Add(command);
                foreach (LiveCommand each in same)
                {
                    each.Runs = same.Count;
                }
                issuer.Commands.Add(command);
                // EF Core logs a command before it tells of it: its log, heard first, gives it its parameters.
                if (issuer.Logged.FirstOrDefault(m => Logs(m, ran.Text)) is { } logged)
                {
                    issuer.Logged.Remove(logged);
                    command.Parameters = CommandParameters.Of(logged);
                }
                changed.Add(issuer);
                return;
            case CommandRead read when byTrace.TryGetValue((from, read.Trace), out LiveRequest? reader)
                && reader.Commands.LastOrDefault(c => c.Id == read.Id) is { } readCommand:
                readCommand.Reads = read.Reads;
                changed.Add(reader);
                return;
            case CommandLogged log when byTrace.TryGetValue((from, log.Trace), out LiveRequest? logger):
                if (logger.Commands.FirstOrDefault(c => c.Parameters is null && Logs(log.Message, c.Text)) is { } unnamed)
                {
                    unnamed.Parameters = CommandParameters.Of(log.Message);
                    changed.Add(logger);
                    return;
                }
                // Kept with its values masked until its command comes: what the app logged in the clear is never held.
                logger.Logged.Add(CommandParameters.Masked(log.Message));
                return;
        }
    }

    /// <summary>Whether EF Core's log of a command is of this text: it logs the text trimmed, and SaveChanges' commands end
    /// with a line break.</summary>
    private static bool Logs(string logged, string text) => logged.AsSpan().Contains(text.AsSpan().TrimEnd(), StringComparison.Ordinal);
}

/// <summary>A command's parameters as EF Core's log of it writes them (<c>[Parameters=[@__id_0='2' (DbType = Int32)], …]</c>),
/// each value masked as ContainerSecrets masks a value of that name, or as a secret's when the column it fills or meets is
/// named as one (<see cref="Columns"/>: EF's <c>@p0</c>, <c>@p1</c>… say nothing of their columns), so no secret the app
/// logs is kept or shown in the clear.</summary>
public static partial class CommandParameters
{
    /// <summary>The parameters a log names, masked, as "@name='value'" apart by commas; empty for a command with none.</summary>
    public static string Of(string logged)
    {
        Match list = List().Match(Masked(logged));
        return list.Success ? string.Join(", ", One().Matches(list.Groups["list"].Value).Select(p => p.Value.Trim())) : string.Empty;
    }

    /// <summary>A log's text with each parameter's value masked as <see cref="Of"/> shows it, the command's text read for
    /// the parameters' columns only when one has a value to show.</summary>
    public static string Masked(string logged)
    {
        IReadOnlyDictionary<string, string>? columns = null;
        return One().Replace(logged, p =>
        {
            string name = p.Groups["name"].Value;
            string value = p.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
            string rest = p.Groups["rest"].Value;
            if (value.Length == 0 || value == ContainerSecrets.Masked)
            {
                return p.Value;
            }
            columns ??= Columns(TextOf(logged));
            string shown = columns.TryGetValue(name, out string? column) && ContainerSecrets.Secret(column) ? ContainerSecrets.Masked
                : ContainerSecrets.Shown(name.TrimStart('@', ':', '$'), value);
            // A secret's size says something of it: its dots stand alone.
            return $"{name}='{shown.Replace("'", "''", StringComparison.Ordinal)}'{(shown == ContainerSecrets.Masked ? Size().Replace(rest, string.Empty) : rest)}";
        });
    }

    /// <summary>The command's text in EF Core's log of it: what follows the line that names its parameters (the whole log
    /// when it has none).</summary>
    private static string TextOf(string logged)
    {
        Match list = List().Match(logged);
        if (!list.Success)
        {
            return logged;
        }
        int line = logged.IndexOf('\n', list.Index + list.Length);
        return line < 0 ? string.Empty : logged[(line + 1)..];
    }

    /// <summary>The column each parameter of a command's text fills or meets, by the parameter's name: an INSERT's column
    /// list against each row of its VALUES (SQL Server's MERGE of several rows against the columns its source's alias
    /// names), and a <c>column = @p</c> or <c>@p = column</c> (a SET's, a WHERE's; <c>&lt;&gt;</c>, <c>LIKE</c> and the
    /// rest of the comparisons too), the column the last name of a qualified one (<c>"c"."Password"</c>).</summary>
    private static IReadOnlyDictionary<string, string> Columns(string sql)
    {
        List<SqlToken> tokens = Tokens(sql);
        Dictionary<string, string> columns = new(StringComparer.OrdinalIgnoreCase);
        List<string>? inserting = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            SqlToken token = tokens[i];
            if (token.Kind == SqlKind.Parameter)
            {
                if (i >= 2 && Compares(tokens[i - 1]) && tokens[i - 2].IsName)
                {
                    columns.TryAdd(token.Text, tokens[i - 2].Text);
                }
                else if (i + 2 < tokens.Count && Compares(tokens[i + 1]) && tokens[i + 2].IsName)
                {
                    int last = i + 2;
                    while (last + 2 < tokens.Count && tokens[last + 1].Is(".") && tokens[last + 2].IsName)
                    {
                        last += 2;
                    }
                    columns.TryAdd(token.Text, tokens[last].Text);
                }
            }
            else if (token.Is(";"))
            {
                inserting = null;
            }
            else if (token.Is("INSERT"))
            {
                // INSERT [INTO] [table] (columns): MERGE's INSERT names no table.
                int at = i + 1 < tokens.Count && tokens[i + 1].Is("INTO") ? i + 2 : i + 1;
                while (at < tokens.Count && tokens[at].IsName)
                {
                    at += at + 1 < tokens.Count && tokens[at + 1].Is(".") ? 2 : 1;
                    if (!tokens[at - 1].Is("."))
                    {
                        break;
                    }
                }
                inserting = Names(tokens, at);
            }
            else if (token.Is("VALUES"))
            {
                List<List<string?>> rows = Rows(tokens, i + 1, out int after);
                // MERGE … USING (VALUES (…), (…)) AS i (columns): the source's columns, else the INSERT's.
                IReadOnlyList<string>? named = after + 3 < tokens.Count && tokens[after].Is(")") && tokens[after + 1].Is("AS") && tokens[after + 2].IsName
                    ? Names(tokens, after + 3) : null;
                if ((named ?? inserting) is { } names)
                {
                    foreach (List<string?> row in rows)
                    {
                        for (int k = 0; k < Math.Min(row.Count, names.Count); k++)
                        {
                            if (row[k] is { } parameter)
                            {
                                columns.TryAdd(parameter, names[k]);
                            }
                        }
                    }
                }
                inserting = null;
                i = after - 1;
            }
        }
        return columns;
    }

    /// <summary>Whether a token compares what stands on either side of it.</summary>
    private static bool Compares(SqlToken token) => token.Is("=") || token.Is("<>") || token.Is("!=") || token.Is("<") || token.Is(">")
        || token.Is("<=") || token.Is(">=") || token.Is("LIKE");

    /// <summary>A parenthesised list of names starting at a token, as an INSERT's columns are written; null for anything else.</summary>
    private static List<string>? Names(List<SqlToken> tokens, int open)
    {
        if (open >= tokens.Count || !tokens[open].Is("("))
        {
            return null;
        }
        List<string> names = [];
        for (int at = open + 1; at < tokens.Count; at += 2)
        {
            if (!tokens[at].IsName || at + 1 >= tokens.Count)
            {
                return null;
            }
            names.Add(tokens[at].Text);
            if (tokens[at + 1].Is(")"))
            {
                return names;
            }
            if (!tokens[at + 1].Is(","))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The rows of a VALUES from a token on, each item the parameter it is alone (null for anything else), and the
    /// token after the last row.</summary>
    private static List<List<string?>> Rows(List<SqlToken> tokens, int from, out int after)
    {
        List<List<string?>> rows = [];
        int at = from;
        while (at < tokens.Count && tokens[at].Is("("))
        {
            List<string?> row = [];
            List<SqlToken> item = [];
            int depth = 0;
            at++;
            for (; at < tokens.Count; at++)
            {
                SqlToken token = tokens[at];
                if (depth == 0 && (token.Is(",") || token.Is(")")))
                {
                    row.Add(item is [{ Kind: SqlKind.Parameter } alone] ? alone.Text : null);
                    item.Clear();
                    if (token.Is(")"))
                    {
                        break;
                    }
                    continue;
                }
                depth += token.Is("(") ? 1 : token.Is(")") ? -1 : 0;
                item.Add(token);
            }
            rows.Add(row);
            at++;
            if (at < tokens.Count && tokens[at].Is(",") && at + 1 < tokens.Count && tokens[at + 1].Is("("))
            {
                at++;
            }
            else
            {
                break;
            }
        }
        after = at;
        return rows;
    }

    /// <summary>A command's text as words, quoted names, parameters, literals and symbols, its comments passed over: enough
    /// of SQL's for <see cref="Columns"/>, in every provider's quoting (<c>"Password"</c>, <c>[Password]</c>, <c>`Password`</c>).</summary>
    private static List<SqlToken> Tokens(string sql)
    {
        List<SqlToken> tokens = [];
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && next == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && next == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                StringBuilder text = new();
                int at = i + 1;
                for (; at < sql.Length; at++)
                {
                    if (sql[at] == close)
                    {
                        if (at + 1 < sql.Length && sql[at + 1] == close)
                        {
                            text.Append(close);
                            at++;
                            continue;
                        }
                        break;
                    }
                    text.Append(sql[at]);
                }
                tokens.Add(new SqlToken(c == '\'' ? SqlKind.Literal : SqlKind.Quoted, text.ToString()));
                i = at + 1;
            }
            else if (c is '@' or ':' or '$' && (char.IsLetterOrDigit(next) || next == '_') && !(c == ':' && i > 0 && sql[i - 1] == ':'))
            {
                int at = i + 1;
                while (at < sql.Length && (char.IsLetterOrDigit(sql[at]) || sql[at] == '_'))
                {
                    at++;
                }
                tokens.Add(new SqlToken(SqlKind.Parameter, sql[i..at]));
                i = at;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                int at = i + 1;
                while (at < sql.Length && (char.IsLetterOrDigit(sql[at]) || sql[at] is '_' or '$' or '#'))
                {
                    at++;
                }
                tokens.Add(new SqlToken(char.IsDigit(c) ? SqlKind.Number : SqlKind.Word, sql[i..at]));
                i = at;
            }
            else
            {
                string symbol = (c, next) is ('<', '>') or ('!', '=') or ('<', '=') or ('>', '=') or (':', ':') ? sql.Substring(i, 2) : c.ToString();
                tokens.Add(new SqlToken(SqlKind.Symbol, symbol));
                i += symbol.Length;
            }
        }
        return tokens;
    }

    private enum SqlKind
    {
        Word,
        Quoted,
        Parameter,
        Literal,
        Number,
        Symbol,
    }

    /// <summary>A word of a command's text: a keyword or bare name, a quoted name, a parameter, a literal, a number or a symbol.</summary>
    private readonly record struct SqlToken(SqlKind Kind, string Text)
    {
        /// <summary>A name a column can have: a bare word or a quoted one.</summary>
        public bool IsName => Kind is SqlKind.Word or SqlKind.Quoted;

        /// <summary>Whether it is this keyword (a bare word, in any case) or symbol.</summary>
        public bool Is(string word) => Kind is SqlKind.Word or SqlKind.Symbol && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\[Parameters=\[(?<list>.*?)\], CommandType=", RegexOptions.Singleline)]
    private static partial Regex List();

    [GeneratedRegex(@"(?<name>[@:$]\w+)='(?<value>(?:[^']|'')*)'(?<rest>(?: \([^)]*\))*)")]
    private static partial Regex One();

    /// <summary>The size EF Core's log gives a value (<c>(Size = 9)</c>).</summary>
    [GeneratedRegex(@" \(Size = \d+\)")]
    private static partial Regex Size();
}
