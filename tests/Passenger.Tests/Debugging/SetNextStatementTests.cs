using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>Set Next Statement through the adapter's gotoTargets and goto: a paused thread goes on from another line of
/// its method, skipping what lies between or doing it again.</summary>
[Collection(DebugSession.Collection)]
public class SetNextStatementTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task The_next_statement_skips_ahead_and_goes_back_within_the_method()
    {
        int first = Debuggees.Line("PassengerCases", "jumps += 1;");
        int second = Debuggees.Line("PassengerCases", "jumps += 10;");
        int third = Debuggees.Line("PassengerCases", "jumps += 100;");
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", first, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        int thread = stop.ThreadId!.Value;
        Assert.Equal(first, stop.ReadStopInfo().line);

        // Past the first two additions, so only the hundred is added.
        await GoTo(host, thread, third);
        host.WithStepOverRequest(thread);
        await host.WaitForStoppedEvent();
        Assert.Equal("100", Evaluate(host, thread, "jumps"));

        // Back to the second, which then adds the ten and the hundred again.
        await GoTo(host, thread, second);
        host.WithStepOverRequest(thread);
        await host.WaitForStoppedEvent();
        host.WithStepOverRequest(thread);
        await host.WaitForStoppedEvent();
        Assert.Equal("210", Evaluate(host, thread, "jumps"));

        GotoTargetsResponse elsewhere = host.Request<GotoTargetsResponse>("gotoTargets",
            new GotoTargetsArguments(new DapSource { Path = Debuggees.Source("PassengerCases", "Program.cs") }, Debuggees.Line("PassengerCases", "Console.WriteLine(\"interpreter\");"), null));
        Assert.Empty(elsewhere.Targets);
    }

    private static async Task GoTo(DebugSession host, int thread, int line)
    {
        GotoTargetsResponse targets = host.Request<GotoTargetsResponse>("gotoTargets",
            new GotoTargetsArguments(new DapSource { Path = Debuggees.Source("PassengerCases", "Program.cs") }, line, null));
        GotoTarget target = Assert.Single(targets.Targets);
        Assert.Equal(line, target.Line);
        host.Request("goto", new GotoArguments(thread, target.Id));
        StoppedEvent moved = await host.WaitForStoppedEvent();
        Assert.Equal("goto", moved.Reason);
        Assert.Equal(line, moved.ReadStopInfo().line);
    }

    private static string Evaluate(DebugSession host, int thread, string expression)
    {
        host.WithEvaluateRequest(host.GetTopStackFrame(thread).Id, expression, out EvaluateResponse response);
        return response.Result;
    }
}
