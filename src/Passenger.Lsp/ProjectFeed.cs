using Passenger.Roslyn.Extension;

namespace Passenger.Lsp;

/// <summary>Where a server with Passenger's project loader (Passenger.Roslyn.Extension's ProjectLoader) gets its projects:
/// the client's design-time builds of them, in place of the builds the server would run itself. Asked on any
/// thread.</summary>
public interface IProjectFeed
{
    /// <summary>The projects to load: those to load first (the file shown's, and those it references), then the rest, each
    /// with its design-time builds when the client has them standing now, else to be asked for; null when the client can
    /// build none (its project host would not start), and the server builds its projects itself.</summary>
    Task<(IReadOnlyList<ProjectToOpen> First, IReadOnlyList<ProjectToOpen> Later)?> ProjectsAsync(CancellationToken cancel);

    /// <summary>One project's design-time builds, which the server asks for as it loads it or finds it changed.</summary>
    Task<ProjectInfoResult> InfoAsync(string path, CancellationToken cancel);
}
