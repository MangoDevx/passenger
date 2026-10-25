using System.Text.Encodings.Web;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>How an agent is given this copy of Passenger as its MCP server, as Settings' MCP Server page shows it: each
/// agent's command or configuration (Claude Code, Codex, Claude Desktop, OpenCode 1 and 2), naming this executable by its
/// real path (inside the app bundle on macOS) with --mcp-attach, and Claude Desktop's with the folder to work in after
/// it, since it starts its servers in no folder.</summary>
public sealed record AgentSetup(string Executable, string? Folder)
{
    /// <summary>What Claude Desktop's configuration names when no folder is open: the person's to fill in.</summary>
    public const string FolderPlaceholder = "FOLDER";

    private static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The running copy's setup, with the window's folder.</summary>
    public static AgentSetup Here(string? folder) => new(ThisExecutable(), folder);

    /// <summary>The executable an agent starts: this process's, or the app host beside it when it runs through dotnet.</summary>
    public static string ThisExecutable()
    {
        string? path = Environment.ProcessPath;
        if (path is null || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");
        }
        return PathNames.Canonical(path);
    }

    /// <summary>Claude Code's command, run in a terminal.</summary>
    public string ClaudeCode => $"claude mcp add passenger -- {Shell(Executable)} --mcp-attach";

    /// <summary>A repository's .mcp.json, which Claude Code reads for everyone working there.</summary>
    public string McpJson => Json(["--mcp-attach"]);

    /// <summary>Codex's command, run in a terminal.</summary>
    public string Codex => $"codex mcp add passenger -- {Shell(Executable)} --mcp-attach";

    /// <summary>Codex's ~/.codex/config.toml entry, which the Codex app shares.</summary>
    public string CodexToml => $"[mcp_servers.passenger]\ncommand = {Quoted(Executable)}\nargs = [\"--mcp-attach\"]";

    /// <summary>Claude Desktop's claude_desktop_config.json entry, with the folder to work in.</summary>
    public string ClaudeDesktop => Json(["--mcp-attach", Folder ?? FolderPlaceholder]);

    /// <summary>OpenCode 1's timeout for a call it hears nothing of: past the person's two questions (2 minutes each at
    /// most), and progress every 10 s starts it over, so a long build never reaches it.</summary>
    public const int OpenCodeTimeout = 600_000;

    /// <summary>How long OpenCode 2 lets a call take in all, which progress does not start over: past a long build or test
    /// run, yet a call that never came back would fail in half an hour rather than its 12 hours.</summary>
    public const int OpenCode2Timeout = 1_800_000;

    /// <summary>OpenCode 1's opencode.json entry: a local server, its command one list, its one timeout every request's.</summary>
    public string OpenCode =>
        "{\n  \"mcp\": {\n    \"passenger\": {\n" +
        "      \"type\": \"local\",\n" +
        $"      \"command\": [{Quoted(Executable)}, \"--mcp-attach\"],\n" +
        "      \"enabled\": true,\n" +
        $"      \"timeout\": {OpenCodeTimeout}\n" +
        "    }\n  }\n}";

    /// <summary>OpenCode 2's: under mcp.servers, Code Mode off, so the agent calls Passenger's tools itself (Code Mode's
    /// permission prompts can leave a call waiting), and a tool call's execution timeout.</summary>
    public string OpenCode2 =>
        "{\n  \"mcp\": {\n    \"servers\": {\n      \"passenger\": {\n" +
        "        \"type\": \"local\",\n" +
        $"        \"command\": [{Quoted(Executable)}, \"--mcp-attach\"],\n" +
        "        \"codemode\": false,\n" +
        $"        \"timeout\": {{ \"execution\": {OpenCode2Timeout} }}\n" +
        "      }\n    }\n  }\n}";

    private string Json(IReadOnlyList<string> arguments) =>
        "{\n  \"mcpServers\": {\n    \"passenger\": {\n" +
        $"      \"command\": {Quoted(Executable)},\n" +
        $"      \"args\": [{string.Join(", ", arguments.Select(Quoted))}]\n" +
        "    }\n  }\n}";

    /// <summary>A string as JSON and TOML both write one: in double quotes, a backslash or quote escaped.</summary>
    private static string Quoted(string text) => JsonSerializer.Serialize(text, Strings);

    /// <summary>A path as the system's shell takes it: as it is when nothing in it needs quoting, else in quotes (double on
    /// Windows, whose shells take a backslash as it is; single elsewhere).</summary>
    private static string Shell(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
        }
        return path.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+@%~:,".Contains(c, StringComparison.Ordinal)) ? path : $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}
