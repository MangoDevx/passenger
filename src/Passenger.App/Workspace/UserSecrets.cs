namespace Passenger.App.Workspace;

/// <summary>Where a project's user secrets are kept on this machine: .NET's own folder (APPDATA's Microsoft/UserSecrets
/// on Windows, ~/.microsoft/usersecrets elsewhere), or <see cref="Variable"/>'s in its place, read for that alone. A
/// container run mounts a project's; an AppHost's parameters are read there.</summary>
public static class UserSecrets
{
    /// <summary>The folder kept in place of .NET's own when set.</summary>
    public const string Variable = "PASSENGER_USER_SECRETS";

    public static string Root => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } given ? given
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");

    /// <summary>The folder of an id's secrets, there or not; null for an id that names no one folder (empty, a separator in it,
    /// a dot or two alone).</summary>
    public static string? FolderOf(string? id) =>
        id is not { Length: > 0 } || id is "." or ".." || id.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) >= 0
            ? null : Path.Combine(Root, id);

    /// <summary>An id's secrets.json, there or not; null for an id that names no one folder.</summary>
    public static string? FileOf(string? id) => FolderOf(id) is { } folder ? Path.Combine(folder, "secrets.json") : null;
}
