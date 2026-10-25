using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Passenger.Core;

/// <summary>What a built assembly's metadata says of it, read from its file without loading it, so no process holds a
/// build's output: its <c>AssemblyMetadata</c> pairs, its informational version, whether its <c>Debuggable</c> leaves the
/// JIT's optimizations on (none does), the assemblies it references and whether it has an entry point. An attribute is
/// known by its type's full name, whichever assembly declares it.</summary>
public sealed record AssemblyFacts(
    IReadOnlyList<(string Key, string? Value)> Metadata,
    string? InformationalVersion,
    bool Optimized,
    IReadOnlyList<(string Name, Version Version)> References,
    bool HasEntryPoint)
{
    /// <summary>An assembly's facts; null for a file that is not there, holds no .NET metadata or does not read.</summary>
    public static AssemblyFacts? Read(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using PEReader image = new(file);
            if (!image.HasMetadata)
            {
                return null;
            }
            MetadataReader metadata = image.GetMetadataReader();
            List<(string, string?)> pairs = [];
            string? informational = null;
            bool optimized = true;
            foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute attribute = metadata.GetCustomAttribute(handle);
                BlobReader value = metadata.GetBlobReader(attribute.Value);
                // Every attribute's value starts with its prolog, 1.
                if (value.Length < 2 || value.ReadUInt16() != 1)
                {
                    continue;
                }
                switch (TypeOf(metadata, attribute))
                {
                    case "System.Reflection.AssemblyMetadataAttribute":
                        pairs.Add((value.ReadSerializedString() ?? string.Empty, value.ReadSerializedString()));
                        break;
                    case "System.Reflection.AssemblyInformationalVersionAttribute":
                        informational = value.ReadSerializedString();
                        break;
                    case "System.Diagnostics.DebuggableAttribute":
                        optimized = (Modes(ref value) & DebuggableAttribute.DebuggingModes.DisableOptimizations) == 0;
                        break;
                }
            }
            List<(string, Version)> references = [.. metadata.AssemblyReferences.Select(r => metadata.GetAssemblyReference(r))
                .Select(r => (metadata.GetString(r.Name), r.Version))];
            return new AssemblyFacts(pairs, informational, optimized, references, image.PEHeaders.CorHeader is { EntryPointTokenOrRelativeVirtualAddress: not 0 });
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A Debuggable attribute's modes: one DebuggingModes (six bytes with the named arguments' count), or the older
    /// two bools (four), the second whether the JIT's optimizations are off.</summary>
    private static DebuggableAttribute.DebuggingModes Modes(ref BlobReader value)
    {
        if (value.RemainingBytes >= 6)
        {
            return (DebuggableAttribute.DebuggingModes)value.ReadInt32();
        }
        value.ReadBoolean();
        return value.ReadBoolean() ? DebuggableAttribute.DebuggingModes.DisableOptimizations : DebuggableAttribute.DebuggingModes.None;
    }

    /// <summary>The full name of an attribute's type, declared in another assembly or in this one.</summary>
    private static string? TypeOf(MetadataReader metadata, CustomAttribute attribute)
    {
        EntityHandle owner = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return owner.Kind switch
        {
            HandleKind.TypeReference when metadata.GetTypeReference((TypeReferenceHandle)owner) is var type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}",
            HandleKind.TypeDefinition when metadata.GetTypeDefinition((TypeDefinitionHandle)owner) is var type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}",
            _ => null,
        };
    }
}
