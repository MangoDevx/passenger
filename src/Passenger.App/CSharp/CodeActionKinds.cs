namespace Passenger.App.CSharp;

/// <summary>What Show Context Actions says beside a language server's action: what it is, a quick-fix or a context
/// action, never the protocol's own kind ("quickfix", "refactor.inline"); a kind with no such name says
/// nothing.</summary>
internal static class CodeActionKinds
{
    // Every refactoring offered at the caret is a context action: the server's sub-kinds mark only some.
    public static string Shown(string? kind) => kind switch
    {
        null => string.Empty,
        _ when Is(kind, "quickfix") => "Quick-fix",
        _ when Is(kind, "refactor") => "Context action",
        _ => string.Empty,
    };

    /// <summary>Whether a kind is the one named or under it, as the protocol's kinds nest by their dots.</summary>
    private static bool Is(string kind, string name) =>
        kind == name || kind.StartsWith(name + ".", StringComparison.Ordinal);
}
