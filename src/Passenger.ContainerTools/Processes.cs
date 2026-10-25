using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;

namespace Passenger.ContainerTools;

/// <summary>A .NET program of the container: what Attach to Process shows of it, whether it was started held for Hot
/// Reload (DOTNET_MODIFIABLE_ASSEMBLIES=debug), whether its code is a Release build's, and why the debugger cannot
/// attach to it, when it cannot.</summary>
public sealed record ContainerProcess(int Id, string Name, string CommandLine, string User, bool HotReload, bool Optimized, string? WhyNot);

/// <summary>The <c>processes</c> mode: the container's .NET programs, those <see cref="DotnetProcesses"/> finds and
/// those whose runtime opened no pipe for a debugger, found by the runtime's library in their memory, each saying
/// why.</summary>
public static class Processes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(TextWriter output) => output.WriteLine(JsonSerializer.Serialize(List(), Json));

    public static IReadOnlyList<ContainerProcess> List()
    {
        List<ContainerProcess> found = [];
        HashSet<int> listed = [];
        foreach (DotnetProcess process in DotnetProcesses.List())
        {
            listed.Add(process.Id);
            found.Add(new ContainerProcess(process.Id, process.Name, process.CommandLine, process.User,
                Variable(process.Id, RuntimeVariables.ModifiableAssemblies) is { } modifiable && modifiable.Equals("debug", StringComparison.OrdinalIgnoreCase),
                Optimized(process.Id, process.CommandLine), null));
        }
        foreach (int id in Running())
        {
            if (id == Environment.ProcessId || listed.Contains(id) || !MapsRuntime(id) || DotnetProcesses.Describe(id) is not { } process)
            {
                continue;
            }
            // Held at its start for a debugger, so no pipe yet but attachable; with diagnostics off, nothing holds it.
            bool held = Variable(id, RuntimeVariables.DefaultDiagnosticPortSuspend) == "1" && !DiagnosticsOff(id);
            found.Add(new ContainerProcess(process.Id, process.Name, process.CommandLine, process.User,
                held && Variable(id, RuntimeVariables.ModifiableAssemblies) is { } modifiable && modifiable.Equals("debug", StringComparison.OrdinalIgnoreCase),
                Optimized(process.Id, process.CommandLine), held ? null : WhyUnreachable(id)));
        }
        return found;
    }

    /// <summary>Why a .NET program opened no pipe for a debugger: diagnostics turned off, or its temporary folder elsewhere.</summary>
    private static string WhyUnreachable(int id) =>
        DiagnosticsOff(id)
            ? "Its runtime was started with DOTNET_EnableDiagnostics=0, which turns its debugger off: nothing can attach to it"
            : Variable(id, "TMPDIR") is { Length: > 0 } temporary && !SameFolder(temporary, Path.GetTempPath())
                ? $"Its runtime keeps the debugger's pipes in TMPDIR {temporary}, which the container's other programs do not share"
                : "Its runtime opened no pipe for a debugger";

    private static bool DiagnosticsOff(int id) => Variable(id, "DOTNET_EnableDiagnostics") == "0" || Variable(id, "DOTNET_EnableDiagnostics_Debugger") == "0";

    private static bool SameFolder(string one, string other) => string.Equals(one.TrimEnd('/'), other.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Every process of the container by its id.</summary>
    private static IEnumerable<int> Running()
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories("/proc");
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string folder in folders)
        {
            if (int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                yield return id;
            }
        }
    }

    /// <summary>Whether a process has CoreCLR loaded, read from its memory map (another user's is not readable, and passed over).</summary>
    private static bool MapsRuntime(int id)
    {
        try
        {
            return File.ReadLines($"/proc/{id}/maps").Any(line => line.EndsWith("/libcoreclr.so", StringComparison.Ordinal));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A variable of a process's environment as it started, or null.</summary>
    private static string? Variable(int id, string name)
    {
        try
        {
            string prefix = name + "=";
            return File.ReadAllText($"/proc/{id}/environ").Split('\0').FirstOrDefault(v => v.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the program's own assembly was built optimized (a Release build): its DebuggableAttribute leaves the
    /// JIT's optimizations on. The .dll the dotnet host runs, else the one beside its apphost; false when unreadable.</summary>
    private static bool Optimized(int id, string commandLine)
    {
        try
        {
            string[] words = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return false;
            }
            string? assembly = Path.GetFileName(words[0]) == "dotnet"
                ? words.Skip(1).FirstOrDefault(w => w.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                : words[0] + ".dll";
            if (assembly is null)
            {
                return false;
            }
            string path = Path.IsPathRooted(assembly) ? assembly : Path.Combine(new DirectoryInfo($"/proc/{id}/cwd").ResolveLinkTarget(true)?.FullName ?? "/", assembly);
            if (!File.Exists(path))
            {
                return false;
            }
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind == HandleKind.MemberReference
                    && metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                    && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "DebuggableAttribute")
                {
                    BlobReader value = metadata.GetBlobReader(attribute.Value);
                    value.ReadUInt16();
                    // DebuggingModes as one int (6 bytes with the named arguments' count), or the older two bools (4).
                    int modes;
                    if (value.RemainingBytes >= 6)
                    {
                        modes = value.ReadInt32();
                    }
                    else
                    {
                        value.ReadBoolean();
                        modes = value.ReadBoolean() ? (int)System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations : 0;
                    }
                    return (modes & (int)System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations) == 0;
                }
            }
            return true;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
