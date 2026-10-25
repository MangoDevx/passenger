using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext(ICorDebugThread thread, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? rootValue = null)
{
    public ICorDebugThread Thread { get; set; } = thread;
    public FrameStackDepth StackDepth { get; set; } = stackDepth;
    public ThreadId ThreadId { get; set; } = threadId;
    /// <summary>The value names resolve against, when given: a DebuggerDisplay expression sees only the current
    /// object.</summary>
    public ICorDebugValue? RootValue { get; set; } = rootValue;
}

public class RuntimeAssemblyPrimitiveTypeClasses(Dictionary<CorElementType, ICorDebugClass> corElementToValueClassMap, ICorDebugClass? corVoidClass, ICorDebugClass? corDecimalClass)
{
    public Dictionary<CorElementType, ICorDebugClass> CorElementToValueClassMap { get; } = corElementToValueClassMap;
    public ICorDebugClass? CorVoidClass { get; } = corVoidClass;
    public ICorDebugClass? CorDecimalClass { get; } = corDecimalClass;
}
