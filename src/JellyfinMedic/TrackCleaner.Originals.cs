using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

/// <summary>Track cleanup: deleting kept originals, and tidying copies left by Medic 1.0.4 to 1.0.7.</summary>
public static partial class TrackCleaner
{
    // ---------- Delete kept originals (only ones Medic kept) ----------

    /// <summary>
    /// Deletes kept originals whose cleaned file is still in place under the same name. With orphans, also those with
    /// no file of that name any more (Sonarr or Radarr renamed or replaced it). Never one whose swap was cut short.
    /// </summary>
    public static (int Deleted, long Freed) DeleteOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, bool orphans = false)
    {
        int deleted = 0;
        long freed = 0;
        try
        {
            foreach (var dir in LibraryFolders(library, libraries))
            {
                string folder = Path.Combine(dir, OriginalsFolder);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var kept in SafeFiles(folder).Where(IsKeptOriginal))
                {
                    // A swap cut short leaves the path empty too, and its original is the title's only untouched copy:
                    // the next run (or "Tidy copies") puts it back.
                    string twin = Path.Combine(dir, Path.GetFileName(kept));
                    if (File.Exists(TempPath(twin)) || (!orphans && !File.Exists(twin)))
                    {
                        continue;
                    }

                    try
                    {
                        long size = Size(kept);
                        File.Delete(kept);
                        freed += size;
                        deleted++;
                    }
                    catch
                    {
                        // Skip anything locked.
                    }
                }

                RemoveIfEmpty(folder);
            }
        }
        finally
        {
            KeptOriginalsWalk.Clear();
        }

        return (deleted, freed);
    }

    // ---------- Tidy up copies left by Medic 1.0.4–1.0.7 ----------

    /// <summary>
    /// Earlier versions left "Name.medic-stripped.mkv" beside the original, and could then strip that copy
    /// again ("Name.medic-stripped.medic-stripped.mkv"). This puts each title back to one file: the first
    /// stripped copy takes the original's name, the original moves to .medic-originals, and copies of
    /// copies are deleted. Nothing is deleted unless a good file remains under the original name, and a copy
    /// FFprobe doesn't read like the film leaves its title as it is.
    /// </summary>
    public static async Task<(int Titles, int CopiesDeleted, long Freed, List<TidyLeft> Left)> TidyEarlierCopies(ILibraryManager library, IEnumerable<LibraryFacts> libraries, string ffprobe, bool deleteOriginals = false)
    {
        lock (Sync)
        {
            _ownerNoted = false;
        }

        int titles = 0, deletedCopies = 0;
        var left = new List<TidyLeft>();
        long freed = 0;
        try
        {
            foreach (var dir in LibraryFolders(library, libraries))
            {
                // Only a copy's end is read: the original it's compared with is only read for its header.
                var (t, d, f, l) = await TidyFolder(dir, path => ProbeFile(ffprobe, path, readEnd: Path.GetFileName(path).Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase), CancellationToken.None), deleteOriginals, CancellationToken.None).ConfigureAwait(false);
                titles += t;
                deletedCopies += d;
                freed += f;
                left.AddRange(l);
            }
        }
        finally
        {
            // Tidying moves originals into .medic-originals, and can delete them.
            KeptOriginalsWalk.Clear();
        }

        return (titles, deletedCopies, freed, left);
    }

    /// <summary>Tidies one folder. probe reads a file with FFprobe; it's passed in so the tests can run without it.</summary>
    internal static async Task<(int Titles, int CopiesDeleted, long Freed, List<TidyLeft> Left)> TidyFolder(string dir, Func<string, Task<ProbedFile?>> probe, bool deleteOriginals, CancellationToken ct)
    {
        int titles = 0, deletedCopies = 0;
        var left = new List<TidyLeft>();
        long freed = 0;
        var copies = SafeFiles(dir).Where(f => Path.GetFileName(f).Contains(StrippedSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in copies.GroupBy(f => OriginalNameFor(f), StringComparer.Ordinal))
        {
            try
            {
                string original = group.Key;
                // Shallowest copy first: "Name.medic-stripped.mkv" before "Name.medic-stripped.medic-stripped.mkv".
                var ordered = group.OrderBy(f => CountSuffix(f)).ThenBy(f => f.Length).ToList();
                string best = ordered[0];

                string keptOriginal = OriginalPath(original);

                // Versions 1.0.4 to 1.0.7 made these copies with optional maps and checked nothing, so a copy
                // missing its video, audio or end must not take the place of a good file. A kept original is
                // the true one; without either, the copy only has to read.
                string? reference = File.Exists(keptOriginal) ? keptOriginal : File.Exists(original) ? original : null;
                string? problem = CheckLeftover(
                    reference is null ? null : await probe(reference).ConfigureAwait(false),
                    await probe(best).ConfigureAwait(false),
                    reference is null ? 0 : Size(reference),
                    Size(best));
                if (problem is not null)
                {
                    left.Add(new TidyLeft(Path.GetFileName(original), problem));
                    Note($"Left {Path.GetFileName(original)} and its earlier copy as they are: the copy didn't check out ({problem})");
                    continue;
                }

                if (File.Exists(original))
                {
                    if (deleteOriginals || File.Exists(keptOriginal))
                    {
                        // Either you asked to keep only the cleaned file, or a true original is already
                        // kept and the file in place is an older cleaned copy. The copy replaces it in one
                        // rename below, so the title is never left without a file if that fails.
                        freed += Size(original);
                        deletedCopies++;
                    }
                    else
                    {
                        await EnsureOriginalsFolder(original, ct).ConfigureAwait(false);
                        File.Move(original, keptOriginal);
                    }
                }

                File.Move(best, original, overwrite: true);
                titles++;

                // The copy in place is Medic's own, so a later release under this name can be told from it.
                if (!deleteOriginals && File.Exists(keptOriginal))
                {
                    RecordCleaned(original);
                }

                if (deleteOriginals && File.Exists(keptOriginal))
                {
                    freed += Size(keptOriginal);
                    File.Delete(keptOriginal);
                    RemoveIfEmpty(Path.GetDirectoryName(keptOriginal)!);
                }

                foreach (var extra in ordered.Skip(1))
                {
                    freed += Size(extra);
                    File.Delete(extra);
                    deletedCopies++;
                }
            }
            catch (Exception ex)
            {
                // Leave anything locked or odd as it is, but say so.
                Note($"Couldn't tidy {Path.GetFileName(group.Key)}: {ex.Message}");
            }
        }

        // Temp files from interrupted runs, once any original they belong to is back in place.
        RecoverInterruptedSwaps(new[] { dir });
        foreach (var tmp in SafeFiles(dir).Where(f => Path.GetFileName(f).Contains(".medic-tmp", StringComparison.OrdinalIgnoreCase)))
        {
            try { freed += Size(tmp); File.Delete(tmp); } catch { /* skip */ }
        }

        return (titles, deletedCopies, freed, left);
    }

    /// <summary>A file in .medic-originals that is a kept original, not Medic's own .ignore or record.</summary>
    private static bool IsKeptOriginal(string path) => !Path.GetFileName(path).StartsWith('.');

    /// <summary>
    /// A kept original with no file of its name in the library folder any more, which "Delete those with no file too"
    /// deletes. One whose swap was cut short (the temp file is still there) isn't: the next run puts it back.
    /// </summary>
    internal static bool IsOrphan(string dir, string kept)
    {
        string twin = Path.Combine(dir, Path.GetFileName(kept));
        return !File.Exists(twin) && !File.Exists(TempPath(twin));
    }

    // Cleared by whatever keeps, moves or deletes originals: a track run, tidying, and deleting them.
    internal static readonly LibraryWalkCache<(int Files, long Bytes, int Orphans, long OrphanBytes)> KeptOriginalsWalk = new(TimeProvider.System);

    /// <summary>How many originals Medic is keeping in .medic-originals folders and their total size, and how many of them are orphans.</summary>
    public static (int Files, long Bytes, int Orphans, long OrphanBytes) KeptOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct = default) =>
        KeptOriginalsWalk.Get(c => CountKeptOriginals(library, libraries, c), ct);

    private static (int Files, long Bytes, int Orphans, long OrphanBytes) CountKeptOriginals(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct)
    {
        int files = 0, orphans = 0;
        long bytes = 0, orphanBytes = 0;
        foreach (var dir in LibraryFolders(library, libraries, ct))
        {
            ct.ThrowIfCancellationRequested();
            string folder = Path.Combine(dir, OriginalsFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var f in SafeFiles(folder).Where(IsKeptOriginal))
            {
                long size = Size(f);
                files++;
                bytes += size;
                if (IsOrphan(dir, f))
                {
                    orphans++;
                    orphanBytes += size;
                }
            }
        }

        return (files, bytes, orphans, orphanBytes);
    }

    private static string OriginalNameFor(string copy)
    {
        string dir = Path.GetDirectoryName(copy) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(copy);
        while (name.EndsWith(StrippedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^StrippedSuffix.Length];
        }

        return Path.Combine(dir, name + Path.GetExtension(copy));
    }

    private static int CountSuffix(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int n = 0, i = 0;
        while ((i = name.IndexOf(StrippedSuffix, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++;
            i += StrippedSuffix.Length;
        }

        return n;
    }

    /// <summary>Every folder that holds a local file in a non-streamed library.</summary>
    private static HashSet<string> LibraryFolders(ILibraryManager library, IEnumerable<LibraryFacts> libraries, CancellationToken ct = default)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in libraries.Where(l => !l.IsStreamed))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(lib.Id, out var id))
            {
                continue;
            }

            try
            {
                foreach (var item in library.GetItemList(new InternalItemsQuery { ParentId = id, Recursive = true, IsFolder = false }))
                {
                    ct.ThrowIfCancellationRequested();
                    string path = item.Path ?? string.Empty;
                    if (path.Length > 0 && LooksLocal(path) && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                    {
                        string? dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !dir.EndsWith(OriginalsFolder, StringComparison.Ordinal))
                        {
                            folders.Add(dir);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip a library that can't be read.
            }
        }

        return folders;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }

    // What Medic itself writes in a .medic-originals folder; with only these left, no original is kept there.
    private static readonly HashSet<string> MedicsOwn = new(StringComparer.Ordinal) { ".ignore", CleanedRecord, CleanedRecord + ".tmp" };

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            var left = Directory.GetFileSystemEntries(folder);
            if (left.All(e => MedicsOwn.Contains(Path.GetFileName(e))))
            {
                foreach (var e in left) { File.Delete(e); }
                Directory.Delete(folder);
            }
        }
        catch
        {
            // Leave it.
        }
    }
}
