using System.Diagnostics;
using Passenger.Core;

namespace Passenger.App.Migrations;

/// <summary>What one <c>dotnet ef</c> came to, read from its <c>--prefix-output</c> lines: its exit code, what it answered
/// (its <c>data:</c> lines, JSON with <c>--json</c>), its errors and warnings each a line, and every line as written.</summary>
public sealed record EfOutcome(int ExitCode, string Data, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Lines)
{
    /// <summary>By its exit code alone: an error EF only logs is one it went past (a new database's missing history
    /// table).</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>A command that never ran (no .NET, a folder gone), its reason its one error.</summary>
    public static EfOutcome Unstarted(string why) => new(-1, string.Empty, [why], [], [why]);
}

/// <summary>Runs <c>dotnet ef</c> through <see cref="ChildProcesses"/>, each line told as it comes and read by its prefix
/// (<c>data:</c>, <c>error:</c>, <c>warn:</c>, <c>info:</c>); a line with none is dotnet's own.</summary>
public static class EfRun
{
    /// <summary>The width of every prefix, padded: <c>data:    </c>, <c>error:   </c>, <c>verbose: </c>.</summary>
    private const int PrefixWidth = 9;

    public static async Task<EfOutcome> RunAsync(ProcessStartInfo start, Action<string> told, CancellationToken stop)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return EfOutcome.Unstarted($"Could not start {Path.GetFileName(start.FileName)}: {failed.Message}");
        }
        using (process)
        {
            List<string> lines = [];
            List<string> data = [];
            List<string> errors = [];
            List<string> warnings = [];
            object gate = new();
            using CancellationTokenRegistration stopping = stop.Register(() => ChildProcesses.Kill(process));
            Task output = Task.Run(() => PumpAsync(process.StandardOutput));
            Task error = Task.Run(() => PumpAsync(process.StandardError));
            await process.WaitForExitAsync(CancellationToken.None);
            await ChildProcesses.OutputEndsAsync(process, Task.WhenAll(output, error), CancellationToken.None);
            lock (gate)
            {
                return new EfOutcome(stop.IsCancellationRequested ? -1 : process.ExitCode, string.Join('\n', data), [.. errors], [.. warnings], [.. lines]);
            }

            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    lock (gate)
                    {
                        lines.Add(line);
                        (string kind, string said) = Split(line);
                        switch (kind)
                        {
                            case "data":
                                data.Add(said);
                                break;
                            case "error":
                                errors.Add(said);
                                break;
                            case "warn":
                                warnings.Add(said);
                                break;
                        }
                    }
                    told(line);
                }
            }
        }
    }

    /// <summary>A line's prefix and what follows it; "" for a line with none, which is dotnet's own.</summary>
    public static (string Kind, string Said) Split(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon is > 0 and < PrefixWidth && line[..colon] is "data" or "error" or "warn" or "info" or "verbose")
        {
            string kind = line[..colon];
            string said = line.Length >= PrefixWidth && string.IsNullOrWhiteSpace(line[(colon + 1)..PrefixWidth]) ? line[PrefixWidth..] : line[(colon + 1)..].TrimStart();
            return (kind, said);
        }
        return (string.Empty, line);
    }
}
