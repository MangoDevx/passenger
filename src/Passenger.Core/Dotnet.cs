using System.Runtime.InteropServices;

namespace Passenger.Core;

/// <summary>Where the .NET command line is, found with the login shell's PATH (<see cref="ShellEnvironment"/>).</summary>
public static class Dotnet
{
    /// <summary>The <c>dotnet</c> that runs this app (framework-dependent, its runtime three folders under the host), else
    /// one on DOTNET_ROOT or the PATH, else where the installers put it.</summary>
    public static string Host()
    {
        ShellEnvironment.Ready();
        string name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string fromRuntime = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", name));
        if (File.Exists(fromRuntime))
        {
            return fromRuntime;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, name)))
        {
            return Path.Combine(root, name);
        }
        return Programs.OnPath(name) ?? Installed(name).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No .NET command line found: install the .NET SDK, or set DOTNET_ROOT to where it is.");
    }

    /// <summary>Where Microsoft's installers, the install script and the package managers put the .NET command line.</summary>
    private static IEnumerable<string> Installed(string name)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet/dotnet";
            yield return "/opt/homebrew/opt/dotnet/libexec/dotnet";
        }
        else
        {
            yield return "/usr/share/dotnet/dotnet";
            yield return "/usr/lib/dotnet/dotnet";
            yield return "/usr/lib64/dotnet/dotnet";
        }
        yield return Path.Combine(home, ".dotnet", name);
    }
}
