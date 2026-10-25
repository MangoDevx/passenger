using Passenger.App.Run;

namespace Passenger.App;

/// <summary>The window's side of the development certificate every run that serves HTTPS takes (a container run's, an
/// Aspire AppHost's: <see cref="BuildRunController"/>'s one path): the dotnet its <c>dev-certs</c> runs through, and the
/// question asked of the person when it is missing or not trusted.</summary>
public partial class MainWindow
{
    /// <summary>The dotnet a run's <c>dev-certs</c> runs through: the machine's, unless one is given.</summary>
    public DotnetWords? DevCertificates { get; init; }

    /// <summary>The question a run asked last of its HTTPS development certificate.</summary>
    public CertificateQuestionDialog? CertificateQuestion { get; private set; }

    /// <summary>Whether this is an agent's own IDE (<c>--mcp</c>): nobody is at it, so what would ask the person (a run's
    /// certificate, containers a container run would replace) takes the way that asks nothing and says so.</summary>
    public bool AgentsOwn { get; init; }

    /// <summary>Asks whether to make and trust the development certificate for what a run serves over HTTPS, or run it over
    /// HTTP; a start stopped meanwhile closes the question, answered Run over HTTP.</summary>
    private async Task<bool> AskCertificateAsync(string configuration, string services, bool missing, CancellationToken stop)
    {
        CertificateQuestionDialog question = new(configuration, services, missing);
        CertificateQuestion = question;
        try
        {
            return await AskAsync(question, stop);
        }
        finally
        {
            if (CertificateQuestion == question)
            {
                CertificateQuestion = null;
            }
        }
    }
}
