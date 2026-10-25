using System.Text;
using System.Text.Json;
using Passenger.App.Workspace;

namespace Passenger.App.Run;

/// <summary>A launch profile from a project's <c>Properties/launchSettings.json</c>: what it launches (its
/// <c>commandName</c>), with what arguments, environment and working directory, and the browser it opens. Passenger
/// launches the two kinds that run a program (<see cref="ProjectCommand"/> and <see cref="ExecutableCommand"/>) and the
/// two that run the project in a container (<c>Docker</c>, <c>SdkContainer</c>, with Visual Studio's container
/// properties: its ports, whether it serves HTTPS, its <c>docker run</c> words and env files); every other kind is read
/// too, and says why it does not run here.</summary>
public sealed record LaunchProfile(
    string Name,
    string? Command,
    string? ExecutablePath,
    string? CommandLineArgs,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? ApplicationUrl,
    bool LaunchBrowser,
    string? LaunchUrl)
{
    /// <summary>A container profile's host port for the container's HTTP port (<c>httpPort</c>), or null for the engine's choice.</summary>
    public int? HttpPort { get; init; }

    /// <summary>A container profile's host port for its HTTPS port (<c>sslPort</c>).</summary>
    public int? SslPort { get; init; }

    /// <summary>Whether the container serves HTTPS (<c>useSSL</c>), which takes the development certificate.</summary>
    public bool UseSsl { get; init; }

    /// <summary>Whether every port the image exposes is published (<c>publishAllPorts</c>).</summary>
    public bool PublishAllPorts { get; init; }

    /// <summary>The container's <c>docker run</c> words (<c>DockerfileRunArguments</c> or <c>containerRunArguments</c>).</summary>
    public string? RunArguments { get; init; }

    /// <summary>Which of the two names the profile gives its <c>docker run</c> words under, or null when it gives none.</summary>
    public string? RunArgumentsKey { get; init; }

    /// <summary>The names a profile gives its container's <c>docker run</c> words under, Visual Studio's Docker one first.</summary>
    public static readonly IReadOnlyList<string> RunArgumentsKeys = ["DockerfileRunArguments", "containerRunArguments"];

    /// <summary>The env files the container reads (<c>containerRunEnvironmentFiles</c>, separated by ';').</summary>
    public string? EnvironmentFiles { get; init; }

    /// <summary>Whether it runs the project in a container: a <c>Docker</c> (its Dockerfile) or <c>SdkContainer</c>
    /// (the SDK's own container) profile.</summary>
    public bool RunsInContainer => Is(DockerCommand) || Is(SdkContainerCommand);

    public const string DockerCommand = "Docker";

    public const string SdkContainerCommand = "SdkContainer";

    /// <summary>The project's own program, built.</summary>
    public const string ProjectCommand = "Project";

    /// <summary>Another program, named by <c>executablePath</c>.</summary>
    public const string ExecutableCommand = "Executable";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string SettingsPath(ProjectModel project) => Path.Combine(project.Folder, "Properties", "launchSettings.json");

    /// <summary>Null for a profile Passenger launches, else why it does not, for each command known.</summary>
    public string? Unsupported => Command switch
    {
        null or "" => "its profile names no commandName",
        _ when Is(ProjectCommand) || Is(ExecutableCommand) || RunsInContainer => null,
        _ when Is("IISExpress") => "an IIS Express profile runs under IIS Express on Windows, which Passenger does not start",
        _ when Is("IIS") => "an IIS profile runs under IIS on Windows, which Passenger does not drive",
        _ when Is("MsixPackage") => "an MSIX package profile runs a deployed Windows package, which Passenger does not deploy",
        _ when Is("DebugRoslynComponent") => "a Roslyn component profile runs the compiler over another project, which Passenger does not",
        _ when Is("DockerCompose") => "a Docker Compose profile belongs to a Docker Compose project (.dcproj), whose configurations run it",
        _ when Is("WSL2") => "a WSL 2 profile runs in Linux under Windows, which Passenger does not start",
        _ => $"Passenger does not know the {Command} command",
    };

    /// <summary>Whether this profile runs the project's own program.</summary>
    public bool RunsProject => Is(ProjectCommand);

    public bool Is(string command) => string.Equals(Command, command, StringComparison.OrdinalIgnoreCase);

    /// <summary>The project's profiles of every kind in the file's order; none when it has no settings or they do not read.</summary>
    public static IReadOnlyList<LaunchProfile> Read(ProjectModel project)
    {
        string path = SettingsPath(project);
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            if (!settings.RootElement.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            List<LaunchProfile> found = [];
            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                JsonElement value = profile.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                Dictionary<string, string> environment = [];
                if (value.TryGetProperty("environmentVariables", out JsonElement variables) && variables.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty variable in variables.EnumerateObject())
                    {
                        environment[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
                    }
                }
                bool browser = value.TryGetProperty("launchBrowser", out JsonElement launch) && launch.ValueKind == JsonValueKind.True;
                found.Add(new LaunchProfile(profile.Name, Text(value, "commandName"), Text(value, "executablePath"), Text(value, "commandLineArgs"),
                    Text(value, "workingDirectory"), environment, Text(value, "applicationUrl"), browser, Text(value, "launchUrl"))
                {
                    HttpPort = Number(value, "httpPort"),
                    SslPort = Number(value, "sslPort"),
                    UseSsl = value.TryGetProperty("useSSL", out JsonElement ssl) && ssl.ValueKind == JsonValueKind.True,
                    PublishAllPorts = value.TryGetProperty("publishAllPorts", out JsonElement all) && all.ValueKind == JsonValueKind.True,
                    RunArguments = Text(value, RunArgumentsKeys[0]) ?? Text(value, RunArgumentsKeys[1]),
                    RunArgumentsKey = RunArgumentsKeys.FirstOrDefault(k => Text(value, k) is not null),
                    EnvironmentFiles = Text(value, "containerRunEnvironmentFiles"),
                });
            }
            return found;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        static string? Text(JsonElement value, string key) =>
            value.TryGetProperty(key, out JsonElement found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

        static int? Number(JsonElement value, string key)
        {
            if (!value.TryGetProperty(key, out JsonElement found))
            {
                return null;
            }
            int port = 0;
            bool read = found.ValueKind == JsonValueKind.Number ? found.TryGetInt32(out port)
                : found.ValueKind == JsonValueKind.String && int.TryParse(found.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port);
            return read && port is > 0 and < 65536 ? port : null;
        }
    }

    /// <summary>The environment a run of the profile gets: its variables, with its application URL as ASP.NET Core's
    /// <c>ASPNETCORE_URLS</c> unless it sets that itself, as <c>dotnet run</c> passes it.</summary>
    public Dictionary<string, string> Environment()
    {
        Dictionary<string, string> environment = new(EnvironmentVariables);
        if (RunsProject && !string.IsNullOrWhiteSpace(ApplicationUrl) && !environment.ContainsKey("ASPNETCORE_URLS"))
        {
            environment["ASPNETCORE_URLS"] = ApplicationUrl;
        }
        return environment;
    }

    /// <summary>Where the browser opens when the profile asks for one: its launch URL, relative to its first application
    /// URL unless absolute; empty with neither.</summary>
    public string BrowserUrl()
    {
        string? root = ApplicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(LaunchUrl))
        {
            return root ?? string.Empty;
        }
        if (Uri.TryCreate(LaunchUrl, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.ToString();
        }
        return root is not null && Uri.TryCreate(root.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri) && Uri.TryCreate(baseUri, LaunchUrl, out Uri? joined)
            ? joined.ToString()
            : LaunchUrl;
    }
}

/// <summary>A command line as one string, split the way .NET splits one into a program's arguments on
/// every platform: spaces separate, double quotes group, and backslashes escape only the quotes they
/// precede.</summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string? line)
    {
        List<string> arguments = [];
        if (string.IsNullOrWhiteSpace(line))
        {
            return arguments;
        }
        StringBuilder current = new();
        bool quoted = false;
        bool started = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int run = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    run++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                // Two quotes inside a quoted run are one literal quote.
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
        {
            arguments.Add(current.ToString());
        }
        return arguments;
    }
}
