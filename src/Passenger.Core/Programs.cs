namespace Passenger.Core;

/// <summary>Programs found as a terminal finds them, on the login shell's PATH (<see cref="ShellEnvironment"/>), in one
/// place: git here, the .NET command line in <see cref="Dotnet"/>.</summary>
public static class Programs
{
    /// <summary>The first of that name on the PATH, or null.</summary>
    public static string? OnPath(string name)
    {
        ShellEnvironment.Ready();
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Node.js on the PATH; null when there is none.</summary>
    public static string? Node() => OnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>Git on the PATH, but not Apple's /usr/bin/git when there are no command line tools behind it: that stub
    /// only asks to install them, on every status. Null when there is none.</summary>
    public static string? Git()
    {
        string? git = OnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
        if (OperatingSystem.IsMacOS() && git == "/usr/bin/git"
            && !File.Exists("/Library/Developer/CommandLineTools/usr/bin/git") && !Directory.Exists("/Applications/Xcode.app"))
        {
            return null;
        }
        return git;
    }
}
