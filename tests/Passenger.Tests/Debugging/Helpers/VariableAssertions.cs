using Passenger.Dap;

namespace Passenger.Tests.Debugging.Helpers;

public static class VariableAssertions
{
    /// <summary>The variables, each whole on one line and in any order, so a failure lists the lines that differ. An empty
    /// presentation hint reads as none, which is how the protocol leaves it out.</summary>
    public static void ShouldBeEquivalentToDebuggerVariables(this List<DapVariable> variables, List<DapVariable> expectedVariables) =>
        Assert.Equal(Described(expectedVariables), Described(variables));

    /// <summary>Each variable described, its reference counted from the list's lowest: where the session's references start
    /// says only how many earlier stops gave them.</summary>
    private static List<string> Described(List<DapVariable> variables)
    {
        int lowest = variables.Where(v => v.VariablesReference > 0).Select(v => v.VariablesReference).DefaultIfEmpty(0).Min();
        return [.. variables.Select(v => Describe(v, v.VariablesReference > 0 ? $"#{v.VariablesReference - lowest}" : "none")).Order(StringComparer.Ordinal)];
    }

    private static string Describe(DapVariable variable, string reference)
    {
        string hint = variable.PresentationHint is { } h
            ? $"{h.Kind}/{string.Join('|', h.Attributes ?? [])}/{h.Visibility}"
            : "//";
        return $"{variable.Name} = {variable.Value} : {variable.Type} (evaluate {variable.EvaluateName}, reference {reference}, hint {hint})";
    }
}
