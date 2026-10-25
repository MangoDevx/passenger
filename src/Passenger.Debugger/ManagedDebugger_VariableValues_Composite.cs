using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private static bool IsTupleType(string typeName) => typeName is "System.ValueTuple"
                                                        || typeName.StartsWith("System.Tuple<", StringComparison.Ordinal)
                                                        || typeName.StartsWith("System.ValueTuple<", StringComparison.Ordinal);

    private async Task<(string Value, bool ResultIsError)> FormatCompositeValueAsync(ICorDebugObjectValue value, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValueFormatKind formatKind)
    {
        string typeName = GetCorDebugTypeFriendlyName(value.ExactType);
        IMetaDataImport metadata = value.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        List<string> members = [];
        bool resultIsError = false;
        if (formatKind is CorDebugValueFormatKind.Tuple)
        {
            string prefix = typeName.StartsWith("System.Tuple<", StringComparison.Ordinal) ? "m_" : "";
            int arity = value.ExactType.TypeParameters.Length;
            List<ICorDebugValue> tupleMemberValues = [];
            for (int i = 1; i <= Math.Min(arity, 7); i++)
            {
                mdFieldDef token = metadata.FindField(value.Class.Token, $"{prefix}Item{i}", 0, 0);
                tupleMemberValues.Add(value.GetFieldValue(value.Class, token));
            }

            ICorDebugValue? rest = null;
            if (arity == 8)
            {
                mdFieldDef token = metadata.FindField(value.Class.Token, $"{prefix}Rest", 0, 0);
                rest = value.GetFieldValue(value.Class, token);
            }

            // Every field read before any is formatted, which may evaluate and neuter the parent value.
            foreach (ICorDebugValue memberValue in tupleMemberValues)
            {
                (string Value, bool ResultIsError) result = await FormatMember(memberValue);
                members.Add(result.Value);
                resultIsError |= result.ResultIsError;
            }

            if (rest is not null)
            {
                (string Value, bool ResultIsError) result = await FormatMember(rest);
                string text = result.Value;
                resultIsError |= result.ResultIsError;
                // Only Rest is flattened; tuples stored in ItemN retain their parentheses.
                if (IsTupleType(GetCorDebugTypeFriendlyName(rest.ExactType)) && text.StartsWith('(') &&
                    text.EndsWith(')'))
                {
                    if (text.Length > 2)
                    {
                        members.Add(text[1..^1]);
                    }
                }
                else
                {
                    members.Add(text);
                }
            }

            return ($"({string.Join(", ", members)})", resultIsError);
        }

        List<(string Name, ICorDebugValue Value)> anonymousMemberValues = [];
        foreach (mdFieldDef token in metadata.EnumFields(value.Class.Token))
        {
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) field = metadata.GetFieldProps(token);
            string name = field.szField;
            if (!name.StartsWith('<') || !name.EndsWith(">i__Field", StringComparison.Ordinal))
            {
                continue;
            }

            name = name[1..name.IndexOf('>')];
            anonymousMemberValues.Add((name, value.GetFieldValue(value.Class, token)));
        }

        // As with tuples, capture all fields before formatting can resume the debuggee.
        foreach ((string? name, ICorDebugValue? memberValue) in anonymousMemberValues)
        {
            (string Value, bool ResultIsError) result = await FormatMember(memberValue);
            members.Add($"{name} = {result.Value}");
            resultIsError |= result.ResultIsError;
        }

        string summary = members.Count == 0 ? "{ }" : $"{{ {string.Join(", ", members)} }}";
        return (summary, resultIsError);

        async Task<(string Value, bool ResultIsError)> FormatMember(ICorDebugValue member)
        {
            FormattedValue result = await FormatCorDebugValueAsync(ResolveCorDebugValue(member), threadId, stackDepth, true);
            return (result.Value, result.ResultIsError);
        }
    }
}
