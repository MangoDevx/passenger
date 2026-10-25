using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, usages read in order with a generator's place named for what it is, a place with no
/// symbol said to be one, or the symbol its line declares taken; and symbols named rather than placed, found exactly, each
/// overload told apart by its documentation id.</summary>
public sealed class McpNavigationTests : McpRig, IClassFixture<RestoredFolders>
{
    public McpNavigationTests(RestoredFolders folders)
        : base(folders)
    {
    }

    private const string Words = "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
        + "    [GeneratedRegex(@\"\\w+\")]\n    private static partial Regex Word();\n\n"
        + "    public static int Count(string text) => Word().Count(text);\n\n"
        + "    public static int Count(string text, int from) => Word().Count(text[from..]);\n}\n";

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n\n"
        + "    public static int CountAll(string[] texts) => texts.Sum(Words.Count);\n}\n";

    [Fact]
    public async Task An_agent_finds_the_solutions_own_symbols_reads_usages_in_order_and_hears_when_no_symbol_is_where_it_asked()
    {
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        await using McpClient client = await Start(["--mcp", Folder]);

        // The regex generator declares Words and Word again in a file on no disk, which the agent could never open.
        JsonElement[] found = [.. (await Call(client, "find_symbol", new() { ["query"] = "Word" })).EnumerateArray()];
        Assert.True(found.Length > 0 && found.All(s => s.GetProperty("path").GetString() == "Words.cs"),
            $"find_symbol Word answered places beyond the solution's own files: {string.Join(", ", found.Select(s => s.GetRawText()))}");

        JsonElement[] used = [.. (await Call(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 34 })).EnumerateArray()];
        Assert.Equal(["Words.cs:6", "Words.cs:8", "Words.cs:10", "RegexGenerator.g.cs (generated):"],
            used.Select(u => $"{u.GetProperty("path").GetString()}:{(u.GetProperty("path").GetString()!.EndsWith("(generated)", StringComparison.Ordinal) ? "" : u.GetProperty("line").GetInt32())}"));

        // By file, then line, whichever the server found first.
        JsonElement[] counted = [.. (await Call(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 23 })).EnumerateArray()];
        Assert.Equal(["Aaa.cs:3:48", "Aaa.cs:5:67", "Words.cs:8:23"], counted.Select(u => $"{u.GetProperty("path").GetString()}:{u.GetProperty("line").GetInt32()}:{u.GetProperty("column").GetInt32()}"));

        // A column off the name, on the line that declares it, still finds it.
        JsonElement[] fromStart = [.. (await Call(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 1 })).EnumerateArray()];
        Assert.Equal(counted.Select(u => u.GetRawText()), fromStart.Select(u => u.GetRawText()));
        string declared = (await Call(client, "symbol_info", new() { ["path"] = "Words.cs", ["line"] = 10, ["column"] = 2 })).GetString()!;
        Assert.Contains("int Words.Count(string text, int from)", declared, StringComparison.Ordinal);

        CallToolResult nothing = await Result(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 2, ["column"] = 1 });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("No symbol at Words.cs:2:1", StringComparison.Ordinal),
            $"usages where no symbol is answered: {Text(nothing)}");
    }

    [Fact]
    public async Task An_agent_names_a_symbol_in_place_of_its_place_finds_names_exactly_and_tells_overloads_apart_by_their_documentation_ids()
    {
        Write("Words.cs", Words);
        Write("Aaa.cs", Tally);
        await using McpClient client = await Start(["--mcp", Folder]);

        // Go to Symbol's matches take in CountAll; an exact find, the two overloads alone, each by its id.
        Assert.Contains((await Call(client, "find_symbol", new() { ["query"] = "Count" })).EnumerateArray(), s => s.GetProperty("name").GetString() == "CountAll");
        Assert.Equal(["Count Words.cs:8 M:Words.Count(System.String)", "Count Words.cs:10 M:Words.Count(System.String,System.Int32)"],
            (await Call(client, "find_symbol", new() { ["query"] = "Count", ["exact"] = true })).EnumerateArray()
                .Select(s => $"{s.GetProperty("name").GetString()} {s.GetProperty("path").GetString()}:{s.GetProperty("line").GetInt32()} {s.GetProperty("id").GetString()}"));

        string both = (await Call(client, "symbol_info", new() { ["symbol"] = "Words.Count" })).GetString()!;
        Assert.True(both.Contains("Words.Count(string) (method) at Words.cs:8:23, M:Words.Count(System.String)", StringComparison.Ordinal)
            && both.Contains("Words.Count(string, int) (method) at Words.cs:10:23, M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal)
            && both.Contains("int Words.Count(string text, int from)", StringComparison.Ordinal), $"symbol_info of Words.Count answered:\n{both}");
        Assert.EndsWith("Documentation id: T:System.Text.RegularExpressions.Regex",
            (await Call(client, "symbol_info", new() { ["path"] = "Words.cs", ["line"] = 6, ["column"] = 28 })).GetString(), StringComparison.Ordinal);

        Assert.Equal(["Aaa.cs:3:68", "Words.cs:10:23"], (await Call(client, "usages", new() { ["symbol"] = "Words.Count(string, int)" })).EnumerateArray()
            .Select(u => $"{u.GetProperty("path").GetString()}:{u.GetProperty("line").GetInt32()}:{u.GetProperty("column").GetInt32()}"));
        CallToolResult ambiguous = await Result(client, "usages", new() { ["symbol"] = "Count" });
        Assert.True(ambiguous.IsError == true && Text(ambiguous).Contains("Count names 2 symbols", StringComparison.Ordinal)
            && Text(ambiguous).Contains("M:Words.Count(System.String,System.Int32)", StringComparison.Ordinal), $"usages of a name two overloads share answered: {Text(ambiguous)}");
        JsonElement declared = Assert.Single((await Call(client, "definition", new() { ["symbol"] = "M:Words.Count(System.String)" })).EnumerateArray());
        Assert.Equal(("Words.cs", 8), (declared.GetProperty("path").GetString(), declared.GetProperty("line").GetInt32()));
        Assert.Equal(["Words.Count(string)", "Words.Count(string, int)"],
            (await Call(client, "hierarchy", new() { ["symbol"] = "Words.Word" })).EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        Assert.Equal("Renamed in 1 file: Aaa.cs", (await Call(client, "rename", new() { ["symbol"] = "Tally.CountAll", ["newName"] = "All" })).GetString());
        Assert.Contains("public static int All(string[] texts)", File.ReadAllText(Path.Combine(Folder, "Aaa.cs")), StringComparison.Ordinal);
    }
}
