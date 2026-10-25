using System.Diagnostics;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class AsyncStepTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task SharpDbgCli_StepRequests_InAsyncMethod_Returns_StoppedEventsAtCorrectLocation()
    {
        bool startSuspended = true;
        using DebugSession debugProtocolHost = DebugSession.ForAttach(testOutputHelper, startSuspended, out Process p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent();
        debugProtocolHost
            .WithBreakpointsRequest(9, Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"))
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        StoppedEvent stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo = stoppedEvent.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo.filePath);
        Assert.Equal(9, stopInfo.line);

        debugProtocolHost.WithClearBreakpointsRequest(Debuggees.Source("DebuggableConsoleApp", "MyAsyncClass.cs"));

        StoppedEvent stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo2 = stoppedEvent2.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo2.filePath);
        Assert.Equal(10, stopInfo2.line);

        StoppedEvent stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo3 = stoppedEvent3.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo3.filePath);
        Assert.Equal(11, stopInfo3.line);

        StoppedEvent stoppedEvent4 = await debugProtocolHost.WithStepOverRequest(stoppedEvent3.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo4 = stoppedEvent4.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo4.filePath);
        Assert.Equal(12, stopInfo4.line);

        // stoppedEvent4's thread, since the thread may have changed after the await.
        StoppedEvent stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent4.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo5 = stoppedEvent5.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo5.filePath);
        Assert.Equal(13, stopInfo5.line);

        StoppedEvent stoppedEvent6 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo6 = stoppedEvent6.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo6.filePath);
        Assert.Equal(18, stopInfo6.line);

        StoppedEvent stoppedEvent7 = await debugProtocolHost.WithStepInRequest(stoppedEvent6.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo7 = stoppedEvent7.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo7.filePath);
        Assert.Equal(19, stopInfo7.line);

        StoppedEvent stoppedEvent8 = await debugProtocolHost.WithStepOutRequest(stoppedEvent7.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo8 = stoppedEvent8.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo8.filePath);
        Assert.Equal(14, stopInfo8.line);

        StoppedEvent stoppedEvent10 = await debugProtocolHost.WithStepInRequest(stoppedEvent8.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo10 = stoppedEvent10.ReadStopInfo();
        Assert.EndsWith("AnotherClass.cs", stopInfo10.filePath);
        Assert.Equal(25, stopInfo10.line);

        StoppedEvent stoppedEvent11 = await debugProtocolHost.WithStepOutRequest(stoppedEvent10.ThreadId!.Value).WaitForStoppedEvent();
        (string filePath, int line, int column) stopInfo11 = stoppedEvent11.ReadStopInfo();
        Assert.EndsWith("MyAsyncClass.cs", stopInfo11.filePath);
        Assert.Equal(14, stopInfo11.line);
    }
}
