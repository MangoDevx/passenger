using System.Collections.Immutable;
using System.Reflection.Metadata;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The names the debugger knows metadata's types by: a type named alike from its definition and from a reference
/// to it in another module (its namespace, then each type it is nested in, joined by <c>nested</c>: '+' as reflection writes
/// it, '.' as C# does), and a primitive by its System type's name and its runtime element type.</summary>
internal static class MetadataTypeNames
{
    public static string Of(MetadataReader reader, TypeDefinitionHandle handle, char nested = '+')
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        return declaring.IsNil ? Joined(reader.GetString(type.Namespace), name) : $"{Of(reader, declaring, nested)}{nested}{name}";
    }

    public static string Of(MetadataReader reader, TypeReferenceHandle handle, char nested = '+')
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{Of(reader, (TypeReferenceHandle)type.ResolutionScope, nested)}{nested}{name}"
            : Joined(reader.GetString(type.Namespace), name);
    }

    public static string Of(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => $"System.{code}",
    };

    /// <summary>The runtime's element type of a primitive, <see cref="CorElementType.END"/> for none.</summary>
    public static CorElementType ElementTypeOf(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Void => CorElementType.VOID,
        PrimitiveTypeCode.Boolean => CorElementType.BOOLEAN,
        PrimitiveTypeCode.Char => CorElementType.CHAR,
        PrimitiveTypeCode.SByte => CorElementType.I1,
        PrimitiveTypeCode.Byte => CorElementType.U1,
        PrimitiveTypeCode.Int16 => CorElementType.I2,
        PrimitiveTypeCode.UInt16 => CorElementType.U2,
        PrimitiveTypeCode.Int32 => CorElementType.I4,
        PrimitiveTypeCode.UInt32 => CorElementType.U4,
        PrimitiveTypeCode.Int64 => CorElementType.I8,
        PrimitiveTypeCode.UInt64 => CorElementType.U8,
        PrimitiveTypeCode.Single => CorElementType.R4,
        PrimitiveTypeCode.Double => CorElementType.R8,
        PrimitiveTypeCode.String => CorElementType.STRING,
        PrimitiveTypeCode.TypedReference => CorElementType.TYPEDBYREF,
        PrimitiveTypeCode.IntPtr => CorElementType.I,
        PrimitiveTypeCode.UIntPtr => CorElementType.U,
        PrimitiveTypeCode.Object => CorElementType.OBJECT,
        _ => CorElementType.END,
    };

    private static string Joined(string @namespace, string name) => @namespace.Length == 0 ? name : $"{@namespace}.{name}";
}

/// <summary>A method signature's types as text that a member reference and the definition it names write alike, whichever
/// modules they are in (<see cref="MetadataTypeNames"/>): the key the debugger matches methods by, and with nested types
/// joined by '.' (<see cref="Dotted"/>) the names a person's function breakpoint is matched against.</summary>
internal sealed class SignatureKeyProvider(char nested) : ISignatureTypeProvider<string, object?>
{
    public static SignatureKeyProvider Instance { get; } = new('+');

    public static SignatureKeyProvider Dotted { get; } = new('.');

    /// <summary>A whole signature as one key: its generic arity, return type and parameters.</summary>
    public static string Of(MethodSignature<string> signature) =>
        $"{signature.GenericParameterCount}|{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => MetadataTypeNames.Of(typeCode);
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, nested);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, nested);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
