using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;
using EntityHandle = System.Reflection.Metadata.EntityHandle;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private static string GetEnumDisplayValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
    {
        bool hasFlagsAttribute = metaDataImport.TryGetCustomAttributeByName(enumTypeDef, "System.FlagsAttribute", out _, out _) is Cor.S_OK;

        string? exact = GetEnumNameForValue(metaDataImport, enumTypeDef, valueAsString);
        if (exact is not null)
        {
            return exact;
        }

        if (hasFlagsAttribute is false)
        {
            return valueAsString;
        }

        return GetFlagsEnumValue(metaDataImport, enumTypeDef, valueAsString);
    }

    private static string? GetEnumNameForValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
    {
        IEnumerable<mdFieldDef> fields = metaDataImport.EnumFields(enumTypeDef);
        foreach (mdFieldDef field in fields)
        {
            const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metaDataImport.GetFieldProps(field);
            if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption)
            {
                continue;
            }

            object? fieldValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);
            if (fieldValue?.ToString() == valueAsString)
            {
                return fieldProps.szField;
            }
        }
        return null;
    }

    private static string GetFlagsEnumValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
    {
        if (!ulong.TryParse(valueAsString, out ulong enumValue))
        {
            return valueAsString;
        }

        ulong remaining = enumValue;

        // value -> name, ordered by value (ascending)
        SortedDictionary<ulong, string> flags = new SortedDictionary<ulong, string>();

        foreach (mdFieldDef field in metaDataImport.EnumFields(enumTypeDef))
        {
            const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;

            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metaDataImport.GetFieldProps(field);
            if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption)
            {
                continue;
            }

            object? fieldValueObj = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);

            ulong fieldValue = Convert.ToUInt64(fieldValueObj);

            // Zero flag is excluded from OR expressions
            if (fieldValue is 0)
            {
                continue;
            }

            // Exact match already handled earlier
            if ((fieldValue & remaining) == fieldValue)
            {
                flags[fieldValue] = fieldProps.szField;
                remaining &= ~fieldValue;
            }
        }

        if (flags.Count > 0 && remaining == 0)
        {
            return string.Join(" | ", flags.Values);
        }

        return enumValue.ToString();
    }

    // All of the below is for const fields
    private static bool TryGetEnumDisplayValue((string FriendlyName, ModuleInfo Module, EntityHandle Handle) fieldType, string value, out string displayValue)
    {
        displayValue = value;
        if (fieldType.Handle.IsNil)
        {
            return false;
        }

        IMetaDataImport metadataImport = fieldType.Module.Module.GetMetaDataInterface<IMetaDataImport>();
        IMetaDataImport? enumMetadataImport;
        mdTypeDef enumTypeDef;
        nint resolvedScope = 0;
        if (fieldType.Handle.Kind is HandleKind.TypeDefinition)
        {
            enumMetadataImport = metadataImport;
            enumTypeDef = (mdTypeDef)MetadataTokens.GetToken(fieldType.Handle);
        }
        else if (fieldType.Handle.Kind is HandleKind.TypeReference)
        {
            Guid iid = Cor.IID_IMetaDataImport;
            int hr = metadataImport.TryResolveTypeRef((mdTypeRef)MetadataTokens.GetToken(fieldType.Handle), ref iid, out resolvedScope, out enumTypeDef);
            if (hr < 0 || resolvedScope is 0)
            {
                return false;
            }

            unsafe
            {
                enumMetadataImport = ComInterfaceMarshaller<IMetaDataImport>.ConvertToManaged((void*)resolvedScope);
            }
            if (enumMetadataImport is null)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        try
        {
            if (IsEnumType(enumMetadataImport, enumTypeDef) is false)
            {
                return false;
            }

            displayValue = GetEnumDisplayValue(enumMetadataImport, enumTypeDef, value);
            return true;
        }
        finally
        {
            unsafe
            {
                if (resolvedScope != 0)
                {
                    ComInterfaceMarshaller<IMetaDataImport>.Free((void*)resolvedScope);
                }
            }
        }
    }

    private static bool IsEnumType(IMetaDataImport metadataImport, mdTypeDef typeDef)
    {
        mdToken baseType = metadataImport.GetTypeDefProps(typeDef).ptkExtends;
        string? baseTypeName = MetadataTokens.Handle(baseType).Kind switch
        {
            HandleKind.TypeDefinition => metadataImport.GetTypeDefProps((mdTypeDef)(int)baseType).szTypeDef,
            HandleKind.TypeReference => metadataImport.GetTypeRefProps((mdTypeRef)(int)baseType).szName,
            _ => null
        };
        return baseTypeName is "System.Enum";
    }

    private sealed class FieldTypeHandleProvider : ISignatureTypeProvider<EntityHandle, object?>
    {
        public static FieldTypeHandleProvider Instance { get; } = new();
        public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
        public EntityHandle GetByReferenceType(EntityHandle elementType) => elementType;
        public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
        public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> typeArguments) => genericType;
        public EntityHandle GetGenericMethodParameter(object? genericContext, int index) => default;
        public EntityHandle GetGenericTypeParameter(object? genericContext, int index) => default;
        public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;
        public EntityHandle GetPinnedType(EntityHandle elementType) => elementType;
        public EntityHandle GetPointerType(EntityHandle elementType) => default;
        public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
        public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
        public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;
        public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;
        public EntityHandle GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
