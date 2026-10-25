using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Passenger.Core;

namespace Passenger.Debugger.Decompilation;

/// <summary>Symbols decompiled for assemblies that ship none, kept in the folder the client names: a portable PDB per
/// assembly version at <c>NAME/MVID/NAME.STAMP.pdb</c>, STAMP the decompiler's version and
/// <see cref="PortablePdbWriter2"/>'s, either of which moves the lines. Written whole (<see cref="AtomicFile"/>), so an
/// adapter killed mid-write leaves no entry; one that does not load is deleted. Pruned as the adapter starts.</summary>
public sealed class SymbolCache
{
    /// <summary>An entry unused this long is deleted: .NET's patches come monthly, each runtime's a new CoreLib, so the
    /// version a machine has moved on from goes a month later.</summary>
    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    /// <summary>The most the entries may take, past which the least recently used go: CoreLib's, the largest a program
    /// loads, is about 5 MB, so a hundred of them, for libraries without symbols that are larger or many.</summary>
    public const long MostBytes = 512L << 20;

    /// <summary>An entry used this recently is never deleted: another session may be reading it.</summary>
    private static readonly TimeSpan InUse = TimeSpan.FromDays(1);

    /// <summary>A temporary file this old is a write that never finished, its adapter killed; a younger one may still be written.</summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromHours(1);

    private const string Temporary = AtomicFile.Temporary;

    /// <summary>The version of what Passenger's PDB writer makes, raised whenever a change to it moves what it maps.</summary>
    private const int Format = 1;

    /// <summary>What names each entry beside its assembly: another decompiler's lines, or another writer's, are not these.</summary>
    private static readonly string Stamp = $"{DecompilerVersionInfo.Version}-{Format}";

    private readonly Action<string>? log;

    /// <summary>The prune begun as the cache was opened, which every read and write waits for.</summary>
    private readonly Task pruned;

    public SymbolCache(string folder, Action<string>? log = null)
    {
        Folder = Path.GetFullPath(folder);
        this.log = log;
        pruned = Task.Run(() => Prune(DateTime.UtcNow));
    }

    public string Folder { get; }

    /// <summary>Where an assembly version's decompiled PDB is kept.</summary>
    public string PathOf(string name, Guid mvid) => Path.Combine(Folder, name, mvid.ToString("N"), $"{name}.{Stamp}.pdb");

    /// <summary>An assembly version's decompiled PDB, marked as used now, or null when the cache has none.</summary>
    public string? Find(string name, Guid mvid)
    {
        pruned.Wait();
        string pdb = PathOf(name, mvid);
        if (!File.Exists(pdb))
        {
            return null;
        }
        try
        {
            File.SetLastWriteTimeUtc(pdb, DateTime.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Pruned or held a moment ago: the load that follows says which.
        }
        return pdb;
    }

    /// <summary>Deletes a PDB that did not load, so the next session asking makes it again rather than refusing it for good.</summary>
    public void Discard(string pdb)
    {
        if (!Delete(pdb))
        {
            log?.Invoke($"Symbol cache: {pdb} did not load and could not be deleted");
        }
    }

    /// <summary>Decompiles an assembly into its entry, written whole as it is made (<see cref="AtomicFile"/>). False when it
    /// could not be decompiled or the session ended first (<paramref name="cancel"/>), its temporary file then deleted.</summary>
    public bool Write(string assemblyPath, string name, Guid mvid, IReadOnlyList<string> references, CancellationToken cancel)
    {
        pruned.Wait();
        string pdb = PathOf(name, mvid);
        try
        {
            using PEFile file = new(assemblyPath, PEStreamOptions.PrefetchEntireImage);
            DecompilerSettings settings = new();
            DecompilerTypeSystem types = new(file, new DebuggingAssemblyResolver([.. references]), settings);
            WriteWhole(pdb, stream => new PortablePdbWriter2 { NoLogo = true }.WritePdb(file, types, settings, stream, cancellationToken: cancel));
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            log?.Invoke($"Symbol cache: decompiling {name} was given up as the session ended");
        }
        catch (Exception error)
        {
            log?.Invoke($"Symbol cache: {name} ({assemblyPath}) could not be decompiled: {error}");
        }
        return false;
    }

    /// <summary>An entry written whole in its folder, made again once if another adapter's prune took the folder away between
    /// the two.</summary>
    private static void WriteWhole(string pdb, Action<Stream> fill)
    {
        for (int attempt = 0; ; attempt++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pdb)!);
            try
            {
                AtomicFile.Write(pdb, fill, null, Temporary, Replace);
                return;
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
            }
        }
    }

    /// <summary>Puts a finished PDB in place. Another adapter's, renamed there first and held open by a reader (Windows
    /// refuses the rename then), is as whole as this one, so it stays.</summary>
    private static void Replace(string temporary, string pdb)
    {
        try
        {
            File.Move(temporary, pdb, overwrite: true);
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && File.Exists(pdb))
        {
            Delete(temporary);
        }
    }

    /// <summary>Deletes what a session no longer needs: temporary files of writes that never finished, entries unused for
    /// <see cref="UnusedFor"/>, then the least recently used while the rest pass <see cref="MostBytes"/>, never one used in
    /// the last <see cref="InUse"/>; then the folders left empty.</summary>
    private void Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }
            List<FileInfo> entries = [];
            foreach (FileInfo file in new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Name.EndsWith(Temporary, StringComparison.Ordinal))
                {
                    if (now - file.LastWriteTimeUtc > Abandoned)
                    {
                        Delete(file.FullName);
                    }
                }
                else if (file.Extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(file);
                }
            }
            long total = entries.Sum(entry => entry.Length);
            int deleted = 0;
            foreach (FileInfo entry in entries.OrderBy(entry => entry.LastWriteTimeUtc))
            {
                TimeSpan unused = now - entry.LastWriteTimeUtc;
                if (unused < InUse)
                {
                    break;
                }
                if ((unused > UnusedFor || total > MostBytes) && Delete(entry.FullName))
                {
                    total -= entry.Length;
                    deleted++;
                }
            }
            // The deepest first, so a name's folder is empty once its versions' are gone.
            foreach (DirectoryInfo folder in new DirectoryInfo(Folder).EnumerateDirectories("*", SearchOption.AllDirectories)
                .OrderByDescending(folder => folder.FullName.Length).ToList())
            {
                if (!folder.EnumerateFileSystemInfos().Any())
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
            }
            if (deleted > 0)
            {
                log?.Invoke($"Symbol cache: {deleted} entries pruned, {total / (1024 * 1024)} MB kept in {Folder}");
            }
        }
        catch (Exception error)
        {
            // A chore: what it could not delete waits for the next adapter, and the cache is used all the same.
            log?.Invoke($"Symbol cache: {Folder} could not be pruned: {error.Message}");
        }
    }

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
