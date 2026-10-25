using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Passenger.Tests;

/// <summary>An agent with no window (<c>--mcp</c>) finding its way through C# code: the solution's own symbols found
/// without a source generator's, usages read in order with a generator's place named for what it is, and a place with no
/// symbol said to be one, or the symbol its line declares taken.</summary>
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

    private const string Tally = "static class Tally\n{\n    public static int Of(string text) => Words.Count(text) + Words.Count(text, 1);\n}\n";

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
        Assert.Equal(["Aaa.cs:3:48", "Words.cs:8:23"], counted.Select(u => $"{u.GetProperty("path").GetString()}:{u.GetProperty("line").GetInt32()}:{u.GetProperty("column").GetInt32()}"));

        // A column off the name, on the line that declares it, still finds it.
        JsonElement[] fromStart = [.. (await Call(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 8, ["column"] = 1 })).EnumerateArray()];
        Assert.Equal(counted.Select(u => u.GetRawText()), fromStart.Select(u => u.GetRawText()));
        string declared = (await Call(client, "symbol_info", new() { ["path"] = "Words.cs", ["line"] = 10, ["column"] = 2 })).GetString()!;
        Assert.Contains("int Words.Count(string text, int from)", declared, StringComparison.Ordinal);

        CallToolResult nothing = await Result(client, "usages", new() { ["path"] = "Words.cs", ["line"] = 2, ["column"] = 1 });
        Assert.True(nothing.IsError == true && Text(nothing).Contains("No symbol at Words.cs:2:1", StringComparison.Ordinal),
            $"usages where no symbol is answered: {Text(nothing)}");
    }
}
