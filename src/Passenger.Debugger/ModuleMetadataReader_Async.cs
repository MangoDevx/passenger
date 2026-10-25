using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Passenger.Debugger;

public partial class ModuleMetadataReader
{
    private (int MethodToken, int ILOffset)? TryResolveAsyncMainEntryPoint(TypeDefinitionHandle declaringTypeHandle)
    {
        TypeDefinition declaringType = _peMetadataReader.GetTypeDefinition(declaringTypeHandle);
        foreach (MethodDefinitionHandle kickoffMethodHandle in declaringType.GetMethods())
        {
            MethodDefinition kickoffMethod = _peMetadataReader.GetMethodDefinition(kickoffMethodHandle);
            if (_peMetadataReader.GetString(kickoffMethod.Name) is not ("Main" or "<Main>$"))
            {
                continue;
            }

            foreach (TypeDefinitionHandle stateMachineHandle in declaringType.GetNestedTypes())
            {
                if (!kickoffMethod.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
                {
                    continue;
                }

                TypeDefinition stateMachine = _peMetadataReader.GetTypeDefinition(stateMachineHandle);
                foreach (MethodDefinitionHandle methodHandle in stateMachine.GetMethods())
                {
                    MethodDefinition method = _peMetadataReader.GetMethodDefinition(methodHandle);
                    if (_peMetadataReader.GetString(method.Name) is not "MoveNext")
                    {
                        continue;
                    }

                    int methodToken = MetadataTokens.GetToken(methodHandle);
                    return (methodToken, GetNextUserCodeIlOffset(methodToken, 0) ?? 0);
                }
            }
        }

        return null;
    }

    public int? GetStateMachineKickoffMethodToken(int moveNextMethodToken)
    {
        MethodDefinitionHandle moveNextHandle = MetadataTokens.MethodDefinitionHandle(moveNextMethodToken);
        if (_pdbMetadataReader is not null)
        {
            MethodDefinitionHandle kickoffHandle = _pdbMetadataReader.GetMethodDebugInformation(moveNextHandle).GetStateMachineKickoffMethod();
            if (kickoffHandle.IsNil is false)
            {
                return MetadataTokens.GetToken(kickoffHandle);
            }
        }

        // From metadata alone: the type the kickoff method's state machine attribute names, so overloads stay apart.
        MethodDefinition moveNext = _peMetadataReader.GetMethodDefinition(moveNextHandle);
        TypeDefinitionHandle stateMachineHandle = moveNext.GetDeclaringType();
        TypeDefinition stateMachineType = _peMetadataReader.GetTypeDefinition(stateMachineHandle);
        TypeDefinitionHandle declaringTypeHandle = stateMachineType.GetDeclaringType();
        if (declaringTypeHandle.IsNil)
        {
            return null;
        }

        foreach (MethodDefinitionHandle candidateHandle in _peMetadataReader.GetTypeDefinition(declaringTypeHandle).GetMethods())
        {
            MethodDefinition candidate = _peMetadataReader.GetMethodDefinition(candidateHandle);
            if (candidate.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
            {
                return MetadataTokens.GetToken(candidateHandle);
            }
        }

        return null;
    }

    private bool StateMachineAttributeMatches(CustomAttributeHandle attributeHandle,
        TypeDefinitionHandle stateMachineHandle)
    {
        CustomAttribute attribute = _peMetadataReader.GetCustomAttribute(attributeHandle);
        EntityHandle attributeType = attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition => _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            HandleKind.MemberReference => _peMetadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            _ => default
        };
        if (attributeType.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference))
        {
            return false;
        }

        (string? attributeNamespace, string? attributeName) = attributeType.Kind is HandleKind.TypeDefinition
            ? GetTypeName((TypeDefinitionHandle)attributeType)
            : GetTypeName((TypeReferenceHandle)attributeType);
        if (attributeNamespace is not "System.Runtime.CompilerServices" || attributeName is not
                ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute"))
        {
            return false;
        }

        BlobReader value = _peMetadataReader.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1)
        {
            return false;
        }

        string? serializedTypeName = value.ReadSerializedString();
        string stateMachineName = GetSerializedTypeName(stateMachineHandle);
        return serializedTypeName == stateMachineName || serializedTypeName?.StartsWith(stateMachineName + ",", StringComparison.Ordinal) is true;
    }

    private (string Namespace, string Name) GetTypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _peMetadataReader.GetTypeDefinition(handle);
        return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
    }

    private (string Namespace, string Name) GetTypeName(TypeReferenceHandle handle)
    {
        TypeReference type = _peMetadataReader.GetTypeReference(handle);
        return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
    }

    private string GetSerializedTypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = _peMetadataReader.GetTypeDefinition(handle);
        string name = _peMetadataReader.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (declaringType.IsNil is false)
        {
            return $"{GetSerializedTypeName(declaringType)}+{name}";
        }

        string @namespace = _peMetadataReader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }
}
