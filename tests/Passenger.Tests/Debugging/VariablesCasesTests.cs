using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>What the Locals scope lists in the shapes Passenger's tests add (PassengerCases).</summary>
[Collection(DebugSession.Collection)]
public class VariablesCasesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task A_parameter_a_lambda_captures_is_listed_once_with_its_live_value()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(f());");
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);

        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        List<DapVariable> locals = host.Locals(stop);
        Assert.Equal("41", Assert.Single(locals, v => v.Name == "captured").Value);
        Assert.Single(locals, v => v.Name == "f");
    }

    [Fact]
    public async Task An_exception_wrapping_another_reports_the_inner_one()
    {
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", Debuggees.Line("PassengerCases", "Console.WriteLine(f());"), [DapExceptionFilters.All]);

        // Every exception stops, the earlier cases' too.
        int wrapping = Debuggees.Line("PassengerCases", "throw new InvalidOperationException(\"Message2\", e1)");
        StoppedEvent stop = await host.WaitForStoppedEvent();
        for (int stops = 0; stop.ReadStopInfo().line != wrapping && stops < 10; stops++)
        {
            stop = await host.WithContinueRequest().WaitForStoppedEvent();
        }
        Assert.Equal((StopReasons.Exception, wrapping), (stop.Reason, stop.ReadStopInfo().line));

        ExceptionInfoResponse info = host.Request<ExceptionInfoResponse>(DapCommands.ExceptionInfo, new ThreadArguments(stop.ThreadId!.Value));
        Assert.Equal("System.InvalidOperationException", info.Details?.FullTypeName);
        DapExceptionDetails inner = Assert.Single(info.Details!.InnerException!);
        Assert.Equal(("System.Exception", "Message1", "$exception.InnerException"), (inner.FullTypeName, inner.Message, inner.EvaluateName));
        Assert.Empty(inner.InnerException!);
        Assert.Contains("Innermost exception: System.Exception", info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebuggerDisplay_renders_as_Visual_Studio_does()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(\"displays\");");
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        List<DapVariable> locals = host.Locals(stop);
        DapVariable Local(string name) => Assert.Single(locals, v => v.Name == name);

        // A string fragment is quoted unless ",nq" says not; \{ and \} are braces.
        Assert.Equal("12.5 USD", Local("money").Value);
        Assert.Equal("12.5 \"USD\"", Local("quoted").Value);
        Assert.Equal("{3}", Local("braced").Value);
        // A fragment that fails says so in its own place; the rest still shows.
        Assert.StartsWith("Value=2 Bad=<error: The name 'NoSuchMember' does not exist", Local("missing").Value, StringComparison.Ordinal);
        Assert.Equal("head -> tail", Local("head").Value);
        Assert.StartsWith("tail -> <error:", Local("tail").Value, StringComparison.Ordinal);
        // Type= is the type column; Name= names a member's row, not a local's.
        Assert.Equal(("11", "RetypedThing"), (Local("labelled").Value, Local("labelled").Type));
        host.WithVariablesRequest(Local("holder").VariablesReference, out List<DapVariable> holder);
        DapVariable item = Assert.Single(holder);
        Assert.Equal(("renamed", "11", "RetypedThing"), (item.Name, item.Value, item.Type));
        // The attribute is inherited; a value inside a display is shown by its own.
        Assert.Equal("base b", Local("derived").Value);
        Assert.Equal("box of 1 EUR", Local("box").Value);
        host.WithVariablesRequest(Local("byName").VariablesReference, out List<DapVariable> entries);
        DapVariable entry = Assert.Single(entries, e => e.Name != "Raw View");
        Assert.Equal(("[\"origin\"]", "(0, 0)"), (entry.Name, entry.Value));

        // RootHidden lifts an object's members as it does an array's elements.
        host.WithVariablesRequest(Local("lifted").VariablesReference, out List<DapVariable> lifted);
        Assert.Equal(["A", "B"], lifted.Select(v => v.Name).Order());
        // A proxy whose constructor throws leaves the value's own members, not the exception's.
        host.WithVariablesRequest(Local("angry").VariablesReference, out List<DapVariable> angry);
        Assert.Equal("1", Assert.Single(angry, v => v.Name == "Real").Value);
        Assert.DoesNotContain(angry, v => v.Name is "_message" or "StackTrace");
    }

    /// <summary>A value whose ToString throws shows its type and still opens to its members.</summary>
    [Fact]
    public async Task A_value_whose_ToString_throws_shows_its_type_and_opens_to_its_members()
    {
        int line = Debuggees.Line("PassengerCases", "Console.WriteLine(\"displays\");");
        using DebugSession host = await DebugSession.LaunchToBreakpoint(testOutputHelper, "PassengerCases", line, []);
        StoppedEvent stop = await host.WaitForStoppedEvent();
        Assert.Equal(line, stop.ReadStopInfo().line);
        DapVariable unprintable = Assert.Single(host.Locals(stop), v => v.Name == "unprintable");
        Assert.True(unprintable.Value == "{Unprintable}" && unprintable.VariablesReference > 0,
            $"The value whose ToString throws shows \"{unprintable.Value}\" with {(unprintable.VariablesReference > 0 ? "" : "no ")}members to open");
        host.WithVariablesRequest(unprintable.VariablesReference, out List<DapVariable> members);
        Assert.Equal("5", Assert.Single(members, v => v.Name == "Kept").Value);

        // Evaluated, it is the same value, not a failure.
        host.WithEvaluateRequest(host.GetTopStackFrame(stop.ThreadId!.Value).Id, "unprintable", out EvaluateResponse evaluated);
        Assert.Equal("{Unprintable}", evaluated.Result);
    }
}
