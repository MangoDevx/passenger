using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class BreakpointTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task SharpDbgCli_SetBreakpoint_RaisesBreakpointEvent()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "MyClass.cs");
        debugProtocolHost
            .WithBreakpointsRequest([11], breakpointedFilePath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        var expectedBreakpointEvent1 = new BreakpointEvent
        {
            Reason = "changed",
            Breakpoint = new DapBreakpoint
            {
                Id = 1,
                Message = "Breakpoint has not been processed by the debugger.",
                Verified = false,
                Line = 11
            }
        };
        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>();
        Assert.Equivalent(expectedBreakpointEvent1, breakpointEvent, strict: true);

        var expectedBreakpointEvent2 = new BreakpointEvent
        {
            Reason = "changed",
            Breakpoint = new DapBreakpoint
            {
                Id = 1,
                Verified = true,
                Line = 11,
                Column = 3,
                EndLine = 11,
                EndColumn = 17,
                Offset = 0,
                Source = new DapSource { Path = breakpointedFilePath, Name = "MyClass.cs", SourceReference = 0 }
            }
        };

        BreakpointEvent breakpointEvent2 = await debugProtocolHost.WaitForEvent<BreakpointEvent>();
        Assert.Equivalent(expectedBreakpointEvent2, breakpointEvent2, strict: true);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyClass.cs", stopInfo.filePath);
        Assert.Equal(11, stopInfo.line);
    }

    [Fact]
    public async Task SharpDbgCli_SetBreakpoint_WithColumn_StopsAtMatchingStatementColumn()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = GetLineNumber(breakpointedFilePath, "column-breakpoint-line");
        int column = GetColumnNumber(breakpointedFilePath, line, "var third");
        int endColumn = column + "var third = second + 1;".Length;

        debugProtocolHost
            .WithBreakpointsRequest(breakpointedFilePath, [new DapSourceBreakpoint(line, Column: column)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified);
        Assert.Equal(line, breakpointEvent.Breakpoint.Line);
        Assert.Equal(column, breakpointEvent.Breakpoint.Column);
        Assert.Equal(endColumn, breakpointEvent.Breakpoint.EndColumn);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(line, topFrame.Line);
        Assert.Equal(column, topFrame.Column);
        Assert.Equal(endColumn, topFrame.EndColumn);
    }

    [Fact]
    public async Task SharpDbgCli_SetBreakpoint_WithoutColumnOnMultiStatementLine_StopsAtFirstStatementColumn()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = GetLineNumber(breakpointedFilePath, "column-breakpoint-line");
        int expectedColumn = GetColumnNumber(breakpointedFilePath, line, "var first");

        debugProtocolHost
            .WithBreakpointsRequest(breakpointedFilePath, [new DapSourceBreakpoint(line)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(line, topFrame.Line);
        Assert.Equal(expectedColumn, topFrame.Column);
    }

    [Fact]
    public async Task SharpDbgCli_SetBreakpoint_WithColumnOnMultilineStatement_StopsAtStatementStartColumn()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int statementStartLine = GetLineNumber(breakpointedFilePath, "var multi =");
        int requestedLine = statementStartLine + 1;
        int requestedColumn = GetColumnNumber(breakpointedFilePath, requestedLine, "1 + 25");
        int expectedColumn = GetColumnNumber(breakpointedFilePath, statementStartLine, "var multi");

        debugProtocolHost
            .WithBreakpointsRequest(breakpointedFilePath, [new DapSourceBreakpoint(requestedLine, Column: requestedColumn)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        BreakpointEvent breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(s => s.Breakpoint.Verified);
        Assert.Equal(statementStartLine, breakpointEvent.Breakpoint.Line);
        Assert.Equal(expectedColumn, breakpointEvent.Breakpoint.Column);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out StackTraceResponse? stackTraceResponse);
        DapStackFrame topFrame = stackTraceResponse.StackFrames.Single();

        Assert.EndsWith("ColumnBreakpointClass.cs", topFrame.Source?.Path);
        Assert.Equal(statementStartLine, topFrame.Line);
        Assert.Equal(expectedColumn, topFrame.Column);
    }

    [Fact]
    public async Task SharpDbgCli_SetBreakpoint_AfterSymbolsLoaded_ResponseIncludesResolvedColumns()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();

        string breakpointedFilePath = Debuggees.Source("DebuggableConsoleApp", "ColumnBreakpointClass.cs");
        int line = GetLineNumber(breakpointedFilePath, "column-breakpoint-line");

        debugProtocolHost
            .WithBreakpointsRequest(breakpointedFilePath, [new DapSourceBreakpoint(line)])
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        await debugProtocolHost.WaitForStoppedEvent();

        int column = GetColumnNumber(breakpointedFilePath, line, "var second");
        int endColumn = column + "var second = first + 1;".Length;

        debugProtocolHost.WithBreakpointsRequest(breakpointedFilePath, [new DapSourceBreakpoint(line, Column: column)], out SetBreakpointsResponse? response);

        DapBreakpoint breakpoint = response.Breakpoints.Single();

        Assert.True(breakpoint.Verified);
        Assert.Equal(line, breakpoint.Line);
        Assert.Equal(column, breakpoint.Column);
        Assert.Equal(line, breakpoint.EndLine);
        Assert.Equal(endColumn, breakpoint.EndColumn);
    }

    [Fact]
    public async Task Breakpoint_NamingASameNamedFileInAnotherFolder_DoesNotBindToTheRealOne()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        // MyClass.cs is in the project's root; SomeFolder holds other files, none of them this one.
        string wrongFolder = Debuggees.Source("DebuggableConsoleApp", "SomeFolder", "MyClass.cs");
        SetBreakpointsResponse wrong = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
            new SetBreakpointsArguments(new DapSource { Path = wrongFolder }, [new DapSourceBreakpoint(22)]));
        string dotted = Debuggees.Source("DebuggableConsoleApp", "Namespace1", "..", "MyClass.cs");
        debugProtocolHost
            .WithBreakpointsRequest(dotted, [new DapSourceBreakpoint(22)], out SetBreakpointsResponse right)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        Assert.Equal(Debuggees.Source("DebuggableConsoleApp", "MyClass.cs"), Path.GetFullPath(stoppedEvent.ReadStopInfo().filePath));
        Assert.Equal(22, stoppedEvent.ReadStopInfo().line);
        Assert.Equal([right.Breakpoints.Single().Id!.Value], stoppedEvent.HitBreakpointIds);
        Assert.False(wrong.Breakpoints.Single().Verified);

        // With the program's symbols loaded, the other folder's name still binds to nothing.
        SetBreakpointsResponse again = debugProtocolHost.Request<SetBreakpointsResponse>(DapCommands.SetBreakpoints,
            new SetBreakpointsArguments(new DapSource { Path = wrongFolder }, [new DapSourceBreakpoint(22)]));
        Assert.False(again.Breakpoints.Single().Verified);
    }

    private static int GetLineNumber(string filePath, string marker)
    {
        return File.ReadLines(filePath)
            .Select((line, index) => (line, index))
            .Single(item => item.line.Contains(marker))
            .index + 1;
    }

    private static int GetColumnNumber(string filePath, int lineNumber, string marker)
    {
        string line = File.ReadLines(filePath).ElementAt(lineNumber - 1);
        int index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Marker '{marker}' not found on line {lineNumber} in {filePath}");
        }

        return index + 1;
    }
}
