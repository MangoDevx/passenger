using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;
using Passenger.Xaml.Metadata;

namespace Passenger.App.Xaml;

/// <summary>A library's type as code to read, for go to declaration from XAML: decompiled by ILSpy's decompiler from
/// the package's implementation assembly (its lib/ beside the ref/ a project compiles against; the runtime's own for
/// the framework's), written once per assembly version under the state folder's MetadataAsSource/, where the editor
/// shows it read-only. Each version's folder (an entry, Source Link's too) is kept while used, and pruned at each
/// process's first use.</summary>
public static partial class LibrarySources
{
    /// <summary>An entry unused this long is deleted: a package updated leaves its old version's behind.</summary>
    private static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    /// <summary>The most the entries may take, past which the least recently used go: a theme's PDB is a few MB.</summary>
    private const long MostBytes = 256L << 20;

    /// <summary>An entry used this recently is never deleted: another window or an agent's Passenger may be reading it.</summary>
    private static readonly TimeSpan InUse = TimeSpan.FromDays(1);

    /// <summary>A temporary file this old is a write that never finished; a younger one may still be written.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(1);

    private static readonly Lock Pruning = new();
    private static bool pruned;

    /// <summary>Where the decompiled files are kept.</summary>
    public static string Folder => Path.Combine(StateStore.DefaultRoot, "MetadataAsSource");

    /// <summary>A library type's decompiled file, and where in it a member (or the type itself) is declared; null when its
    /// assembly cannot be read.</summary>
    public static (string Path, TextPosition Position)? Decompile(ClrType type, ClrMember? member)
    {
        if (type.Origin.AssemblyPath is not { } reference)
        {
            return null;
        }
        string assembly = Implementation(reference);
        string outermost = type.FullName.Split('+')[0];
        if (Entry("Decompiled", type.Origin.AssemblyName, assembly) is not { } folder || Inside(folder, outermost.Replace('`', '_') + ".cs") is not { } path)
        {
            return null;
        }
        if (!File.Exists(path))
        {
            try
            {
                CSharpDecompiler decompiler = new(assembly, new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
                WriteWhole(path, System.Text.Encoding.UTF8.GetBytes(decompiler.DecompileTypeAsString(new FullTypeName(outermost))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException
                or ICSharpCode.Decompiler.Metadata.MetadataFileNotSupportedException)
            {
                return null;
            }
        }
        string[] lines = File.ReadAllLines(path);
        return (path, Declaration(lines, member?.Name ?? type.Name, enumMember: member is { Kind: MemberKind.Field } && type.Kind == ClrTypeKind.Enum));
    }

    /// <summary>The folder of one version of an assembly's files (<c>kind/Name/stamp</c>), marked as used now, the cache
    /// pruned first if this process has not yet; null for a name that is no plain folder name. Off the UI thread.</summary>
    public static string? Entry(string kind, string assemblyName, string assembly)
    {
        if (assemblyName.Length == 0 || assemblyName is "." or ".." || assemblyName.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return null;
        }
        lock (Pruning)
        {
            if (!pruned)
            {
                pruned = true;
                Prune(DateTime.UtcNow);
            }
        }
        string folder = Path.Combine(Folder, kind, assemblyName, Stamp(assembly));
        try
        {
            Directory.CreateDirectory(folder);
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Pruned by another Passenger a moment ago, or not writable: the write that follows says which.
        }
        return folder;
    }

    /// <summary>A path inside an entry, by its path from there; null when it would lead out of it (<c>../</c>, a rooted
    /// path, a link out), compared as canonical paths.</summary>
    public static string? Inside(string folder, string relative)
    {
        string root = PathNames.Canonical(folder);
        string path = PathNames.Canonical(Path.Combine(folder, relative.TrimStart('/', '\\')));
        return PathNames.IsUnder(path, root) ? path : null;
    }

    /// <summary>Writes a file whole or not at all, its folder made first (<see cref="AtomicFile"/>), so a write cut short
    /// never leaves a truncated file that a later session takes for the whole.</summary>
    public static void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, bytes);
    }

    /// <summary>Deletes what no session needs: temporary files of writes that never finished, entries unused for
    /// <see cref="UnusedFor"/>, then the least recently used while the rest pass <see cref="MostBytes"/>, never one used in
    /// the last <see cref="InUse"/>; then the assemblies' folders left empty.</summary>
    private static void Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }
            foreach (FileInfo file in new DirectoryInfo(Folder).EnumerateFiles("*" + AtomicFile.Temporary, SearchOption.AllDirectories))
            {
                if (now - file.LastWriteTimeUtc > Abandoned)
                {
                    Delete(file.FullName);
                }
            }
            List<(DirectoryInfo Folder, DateTime Used, long Bytes)> entries = [.. new DirectoryInfo(Folder).EnumerateDirectories()
                .SelectMany(kind => kind.EnumerateDirectories()).SelectMany(name => name.EnumerateDirectories())
                .Select(entry => (entry, entry.LastWriteTimeUtc, entry.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)))];
            long total = entries.Sum(e => e.Bytes);
            int deleted = 0;
            foreach ((DirectoryInfo entry, DateTime used, long bytes) in entries.OrderBy(e => e.Used))
            {
                TimeSpan unused = now - used;
                if (unused < InUse)
                {
                    break;
                }
                if (unused > UnusedFor || total > MostBytes)
                {
                    try
                    {
                        entry.Delete(recursive: true);
                        total -= bytes;
                        deleted++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // Held by a reader (Windows): the next session's prune takes it.
                    }
                }
            }
            foreach (DirectoryInfo name in new DirectoryInfo(Folder).EnumerateDirectories().SelectMany(kind => kind.EnumerateDirectories()).ToList())
            {
                if (!name.EnumerateFileSystemInfos().Any())
                {
                    Delete(name);
                }
            }
            if (deleted > 0)
            {
                IdeErrors.Write($"Library sources: {deleted} versions' files pruned, {total / (1024 * 1024)} MB kept in {Folder}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A chore: what it could not delete waits for the next session, and the cache is used all the same.
            IdeErrors.Write($"Library sources: {Folder} could not be pruned: {error.Message}");
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Held a moment: a later prune takes it.
        }
    }

    private static void Delete(DirectoryInfo folder)
    {
        try
        {
            folder.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Written to since it was looked at.
        }
    }

    /// <summary>The assembly with the code: a package's lib/ beside the ref/ compiled against, the running runtime's for
    /// the framework's reference pack, else the one given.</summary>
    public static string Implementation(string reference)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        string refFolder = $"{separator}ref{separator}";
        if (reference.Contains(refFolder, StringComparison.Ordinal))
        {
            string lib = reference.Replace(refFolder, $"{separator}lib{separator}", StringComparison.Ordinal);
            if (File.Exists(lib))
            {
                return lib;
            }
        }
        if (reference.Contains($"{separator}packs{separator}", StringComparison.Ordinal))
        {
            string runtime = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), Path.GetFileName(reference));
            if (File.Exists(runtime))
            {
                return runtime;
            }
        }
        return reference;
    }

    /// <summary>A folder name for one version of an assembly, so a package updated decompiles afresh: a hash of its path,
    /// time and length that every session works out alike (not HashCode's, seeded anew in each process).</summary>
    public static string Stamp(string assembly)
    {
        FileInfo file = new(assembly);
        byte[] said = System.Text.Encoding.UTF8.GetBytes($"{assembly}\n{file.LastWriteTimeUtc.Ticks}\n{file.Length}");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(said).AsSpan(0, 8));
    }

    /// <summary>Where a name is declared in decompiled code: the first line declaring it with an access modifier (an enum's
    /// member, the line it starts), else its first mention, else the top.</summary>
    private static TextPosition Declaration(string[] lines, string name, bool enumMember)
    {
        Regex word = new($@"\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        int mentioned = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            Match found = word.Match(lines[i]);
            if (!found.Success)
            {
                continue;
            }
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            if ((enumMember && trimmed.StartsWith(name, StringComparison.Ordinal)) || (!enumMember && Modifier().IsMatch(trimmed)))
            {
                return new TextPosition(i, found.Index);
            }
            if (mentioned < 0)
            {
                mentioned = i;
            }
        }
        return mentioned < 0 ? new TextPosition(0, 0) : new TextPosition(mentioned, Math.Max(0, word.Match(lines[mentioned]).Index));
    }

    [GeneratedRegex(@"^(public|protected|internal)\b")]
    private static partial Regex Modifier();
}
