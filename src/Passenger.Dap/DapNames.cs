namespace Passenger.Dap;

/// <summary>The requests the IDE and Passenger's debug adapter make of each other, by the protocol's names, each written once
/// for both ends, so a name one end misspells is a build error rather than a request the other never answers. Passenger's
/// own extend the protocol: <see cref="ResolveStackFrame"/>, <see cref="HotReloadState"/>, <see cref="ApplyChanges"/> and
/// <see cref="Input"/>.</summary>
public static class DapCommands
{
    public const string Initialize = "initialize";
    public const string Launch = "launch";
    public const string Attach = "attach";
    public const string ConfigurationDone = "configurationDone";
    public const string SetBreakpoints = "setBreakpoints";
    public const string SetFunctionBreakpoints = "setFunctionBreakpoints";
    public const string SetExceptionBreakpoints = "setExceptionBreakpoints";
    public const string DataBreakpointInfo = "dataBreakpointInfo";
    public const string SetDataBreakpoints = "setDataBreakpoints";
    public const string Threads = "threads";
    public const string StackTrace = "stackTrace";
    public const string Scopes = "scopes";
    public const string Variables = "variables";
    public const string Evaluate = "evaluate";
    public const string SetExpression = "setExpression";
    public const string GotoTargets = "gotoTargets";
    public const string Goto = "goto";
    public const string Continue = "continue";
    public const string Next = "next";
    public const string StepIn = "stepIn";
    public const string StepInTargets = "stepInTargets";
    public const string StepOut = "stepOut";
    public const string Pause = "pause";
    public const string Disconnect = "disconnect";
    public const string Terminate = "terminate";
    public const string ExceptionInfo = "exceptionInfo";

    /// <summary>The adapter's request of the client: start the program in its terminal.</summary>
    public const string RunInTerminal = "runInTerminal";

    /// <summary>Passenger's own: a frame without source given its assembly's decompiled symbols.</summary>
    public const string ResolveStackFrame = "resolveStackFrame";

    /// <summary>Passenger's own: what the language's Edit and Continue needs of the program.</summary>
    public const string HotReloadState = "hotReloadState";

    /// <summary>Passenger's own: Hot Reload's updates applied to the program.</summary>
    public const string ApplyChanges = "applyChanges";

    /// <summary>Passenger's own: text for the program's standard input, or its input closed.</summary>
    public const string Input = "input";
}

/// <summary>The events Passenger's debug adapter tells the IDE, by the protocol's names, each written once for both ends.</summary>
public static class DapEvents
{
    public const string Initialized = "initialized";
    public const string Stopped = "stopped";
    public const string Breakpoint = "breakpoint";
    public const string Exited = "exited";
    public const string Terminated = "terminated";
    public const string Thread = "thread";
    public const string Module = "module";
    public const string Process = "process";
    public const string Output = "output";
}

/// <summary>Why the program stopped, the stopped event's reason, by the protocol's names: what the engine reports, what the
/// adapter passes on and what the IDE tells apart, each written once.</summary>
public static class StopReasons
{
    public const string Step = "step";
    public const string Breakpoint = "breakpoint";
    public const string Exception = "exception";
    public const string Pause = "pause";
    public const string Entry = "entry";
    public const string Goto = "goto";
    public const string FunctionBreakpoint = "function breakpoint";
    public const string DataBreakpoint = "data breakpoint";
    public const string InstructionBreakpoint = "instruction breakpoint";

    /// <summary>A reason the protocol has no name for.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The rows Passenger's debug adapter adds among a value's members, by their names, which the IDE tells apart: each
/// written once for both ends.</summary>
public static class DapRows
{
    /// <summary>An EF Core query's SQL: a lazy row, read only as it is opened.</summary>
    public const string QuerySql = "SQL";

    /// <summary>What <see cref="QuerySql"/> says before it is opened.</summary>
    public const string QuerySqlUnread = "Opening it runs ToQueryString() in the program";
}
