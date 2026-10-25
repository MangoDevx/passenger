using Avalonia.Threading;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>An open file's share of a language server, C#'s (<see cref="CSharpDocument"/>) or a web language's
/// (<see cref="Web.WebDocument"/>): its document at the server, null until the server has it, and the diagnostics last
/// reported, on the server's lines of the text as it stands. What the editor's commands ask (<see cref="CodeAssist"/>).</summary>
public interface IServerDocument
{
    LspDocument? Lsp { get; }

    IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>How a server's diagnostics become the editor's markers, and are carried with the text between reports.</summary>
internal static class DiagnosticMarks
{
    /// <summary>What a diagnostic draws as: errors, warnings and information squiggled; code the
    /// compiler calls unnecessary faded; the server's quieter hints left off the page.</summary>
    public static Marker? Marker(Diagnostic diagnostic, TextRange range)
    {
        bool unnecessary = diagnostic.Tags?.Contains(DiagnosticTag.Unnecessary) == true;
        MarkerKind? kind = Kind(diagnostic.Severity) is MarkerKind.Error || !unnecessary ? Kind(diagnostic.Severity) : MarkerKind.Unnecessary;
        if (kind is not { } k)
        {
            return null;
        }
        string code = diagnostic.Code?.ToString() is { Length: > 0 } c ? $"{c}: " : string.Empty;
        return new Marker(range, k, code + diagnostic.Message);
    }

    /// <summary>What a severity draws as: errors, warnings and information squiggled, the server's quieter hints left off
    /// the page.</summary>
    public static MarkerKind? Kind(DiagnosticSeverity? severity) => severity switch
    {
        DiagnosticSeverity.Error => MarkerKind.Error,
        DiagnosticSeverity.Warning => MarkerKind.Warning,
        DiagnosticSeverity.Information => MarkerKind.Info,
        _ => null,
    };

    /// <summary>A range of the server's as offsets in the text it was given for, never ending before it starts.</summary>
    public static TextRange Offsets(ServerLines lines, LspRange range)
    {
        int start = lines.OffsetOf(range.Start);
        return TextRange.FromBounds(start, Math.Max(start, lines.OffsetOf(range.End)));
    }

    /// <summary>The diagnostics reported, each on the server's lines where its range has been carried to.</summary>
    public static Diagnostic[] Placed(TextSnapshot text, IReadOnlyList<Diagnostic> reported, IReadOnlyList<TextRange> ranges)
    {
        ServerLines lines = ServerLines.Of(text);
        int length = lines.Text.Length;
        Diagnostic[] moved = new Diagnostic[reported.Count];
        for (int i = 0; i < reported.Count; i++)
        {
            Diagnostic diagnostic = reported[i];
            TextRange now = ranges[i];
            moved[i] = new Diagnostic
            {
                Range = new LspRange(lines.PositionOf(Math.Min(now.Start, length)), lines.PositionOf(Math.Min(now.End, length))),
                Severity = diagnostic.Severity,
                Code = diagnostic.Code,
                CodeDescription = diagnostic.CodeDescription,
                Source = diagnostic.Source,
                Message = diagnostic.Message,
                Tags = diagnostic.Tags,
                Data = diagnostic.Data,
            };
        }
        return moved;
    }
}

/// <summary>An open file's diagnostics from one server (C#'s, a web language's, ESLint's): pulled after a pause, each
/// pull ending the one before it and held as background work, or taken as pushed, and carried with the text between
/// reports, so a squiggle never drifts off its code. What each asks of its server is its own.</summary>
internal sealed class ServerDiagnostics(OpenDocument document, bool holdsChecks)
{
    private CancellationTokenSource? pulling;

    /// <summary>The diagnostics as reported, and each one's range in the text as it stands, moved with every edit since.</summary>
    private Diagnostic[] found = [];
    private TextRange[] ranges = [];

    /// <summary>The diagnostics placed on the server's lines of the text as it stands, made when first asked for after an
    /// edit, so a key costs a move of each range rather than a line table of the file.</summary>
    private IReadOnlyList<Diagnostic>? placed = [];

    /// <summary>The diagnostics last reported, moved with the text since, so their lines are the text's as it is now.</summary>
    public IReadOnlyList<Diagnostic> Placed => placed ??= DiagnosticMarks.Placed(document.Text.Snapshot, found, ranges);

    /// <summary>The diagnostics placed so, each with its range in the text as it stands.</summary>
    public IEnumerable<(Diagnostic Diagnostic, TextRange Range)> PlacedRanges => Placed.Zip(ranges);

    /// <summary>Carries the diagnostics' ranges through an edit.</summary>
    public void Move(DocumentChangedEventArgs e)
    {
        if (ranges.Length == 0)
        {
            return;
        }
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = TextEditing.MapRange(ranges[i], e.Edits);
        }
        placed = null;
    }

    /// <summary>Takes diagnostics reported for the text as it stands, and gives the markers they draw as.</summary>
    public IReadOnlyList<Marker> Take(IReadOnlyList<Diagnostic> diagnostics, Func<Diagnostic, TextRange, Marker?> marker)
    {
        ServerLines lines = ServerLines.Of(document.Text.Snapshot);
        found = [.. diagnostics];
        ranges = [.. found.Select(d => DiagnosticMarks.Offsets(lines, d.Range))];
        placed = diagnostics;
        return [.. found.Select((d, i) => marker(d, ranges[i])).OfType<Marker>()];
    }

    /// <summary>Leaves out the diagnostics <paramref name="which"/> names, giving the markers the rest draw as, or null when
    /// it named none.</summary>
    public IReadOnlyList<Marker>? Drop(Func<Diagnostic, bool> which, Func<Diagnostic, TextRange, Marker?> marker)
    {
        if (!found.Any(which))
        {
            return null;
        }
        int[] kept = [.. Enumerable.Range(0, found.Length).Where(i => !which(found[i]))];
        ranges = [.. kept.Select(i => ranges[i])];
        found = [.. kept.Select(i => found[i])];
        placed = null;
        return [.. found.Select((d, i) => marker(d, ranges[i])).OfType<Marker>()];
    }

    /// <summary>Asks the server again after <paramref name="delay"/>, ending the ask under way: <paramref name="ask"/> pulls
    /// (<see cref="PulledAsync"/>) and shows what answers, a server that fails or a later ask leaving the markers as they
    /// stand; when it <c>holdsChecks</c>, the file's checks are held until the ask ends.</summary>
    public void Pull(TimeSpan delay, Func<CancellationToken, Task> ask)
    {
        pulling?.Cancel();
        CancellationTokenSource cancel = new();
        pulling = cancel;
        BackgroundWork.Begin();
        _ = PullAsync(delay, ask, cancel.Token, holdsChecks ? document.Checking() : null);
    }

    /// <summary>The server's diagnostics of the file, when they answer the text as it stands and no later ask has begun.</summary>
    public async Task<IReadOnlyList<Diagnostic>?> PulledAsync(LspDocument lsp, CancellationToken cancel)
    {
        (IReadOnlyList<Diagnostic> diagnostics, int version) = await lsp.DiagnosticsAsync(cancel);
        return !cancel.IsCancellationRequested && version == document.Text.Version ? diagnostics : null;
    }

    /// <summary>Ends the ask under way: the file was let go.</summary>
    public void Stop() => pulling?.Cancel();

    private static async Task PullAsync(TimeSpan delay, Func<CancellationToken, Task> ask, CancellationToken cancel, IDisposable? checking)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancel);
            }
            await ask(cancel);
        }
        catch (OperationCanceledException)
        {
            // Typing went on, or the file was let go.
        }
        catch (LanguageServerFailed)
        {
            // The markers stand until the server answers again, or a server started again does.
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                checking?.Dispose();
                BackgroundWork.End();
            });
        }
    }
}
