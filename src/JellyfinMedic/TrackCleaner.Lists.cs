namespace JellyfinMedic.Services;

/// <summary>Track cleanup: paged lists from the last scan, and setting a language by hand.</summary>
public static partial class TrackCleaner
{
    // ---------- Paged lists from the last scan ----------

    public static PagedList<UntaggedTrack> UntaggedPage(int page, int size, string kind, string? search)
    {
        lock (ScanSync)
        {
            var all = (_lastScan?.Untagged ?? new List<UntaggedTrack>())
                .Where(u => kind is "audio" or "subtitle" ? u.Kind == kind : true)
                .Where(u => string.IsNullOrWhiteSpace(search) || u.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || u.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Page(all, page, size);
        }
    }

    public static PagedList<FilePlan> ChangesPage(int page, int size, string? search)
    {
        lock (ScanSync)
        {
            var all = (_lastScan?.Changing ?? new List<FilePlan>())
                .Where(p => string.IsNullOrWhiteSpace(search) || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Page(all, page, size);
        }
    }

    private static PagedList<T> Page<T>(List<T> all, int page, int size)
    {
        size = Math.Clamp(size, 5, 100);
        int pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)size));
        page = Math.Clamp(page, 1, pages);
        return new PagedList<T> { Total = all.Count, Page = page, Pages = pages, Items = all.Skip((page - 1) * size).Take(size).ToList() };
    }

    // ---------- Setting a language by hand ----------

    /// <summary>
    /// Sets (or clears, with an empty language) the language of a track inside a film. It's written into
    /// the file the next time tracks are stripped. Only tracks from the last scan can be changed.
    /// </summary>
    public static (bool Ok, string Message) SetLanguage(string path, int index, string language)
    {
        lock (ScanSync)
        {
            var row = _lastScan?.Untagged.FirstOrDefault(u => u.Path == path && u.Index == index && !u.External);
            if (row is null)
            {
                return (false, "That track isn't in the last scan. Scan again, then try.");
            }

            if (_lastScan!.Stale)
            {
                return (false, StaleMessage);
            }

            string code = string.Empty;
            if (!string.IsNullOrWhiteSpace(language))
            {
                if (LanguageCode(language) is not { } valid)
                {
                    return (false, "Choose a language first.");
                }

                code = valid;
            }

            TrackLanguages.Set(path, index, code.Length == 0 ? null : code);
            row.SetLanguage = code.Length == 0 ? null : code;
            return (true, code.Length == 0
                ? "Cleared."
                : $"Set to {LanguageName(code)}. It's written into the file the next time you press Strip tracks for real.");
        }
    }

    /// <summary>
    /// Sets the language of many tracks inside films at once, for everything matching the filter
    /// (page 0) or one page. By default only audio that is the film's only audio track, and never anime.
    /// </summary>
    public static (int Count, string Message) SetLanguageBulk(string language, string kind, string? search, bool onlyAudioTrack, bool skipAnime, int page, int size)
    {
        if (LanguageCode(language) is not { } code)
        {
            return (0, "Choose a language first.");
        }

        List<UntaggedTrack> rows;
        lock (ScanSync)
        {
            if (_lastScan?.Stale == true)
            {
                return (0, StaleMessage);
            }

            var all = (_lastScan?.Untagged ?? new List<UntaggedTrack>())
                .Where(u => kind is "audio" or "subtitle" ? u.Kind == kind : true)
                .Where(u => string.IsNullOrWhiteSpace(search) || u.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || u.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            rows = page > 0 ? Page(all, page, size).Items : all;
        }

        var chosen = rows
            .Where(u => !u.External)
            .Where(u => !onlyAudioTrack || u.Kind != "audio" || u.OnlyAudioTrack)
            .Where(u => !skipAnime || !u.Anime)
            .ToList();

        TrackLanguages.SetMany(chosen.Select(u => (u.Path, u.Index)), code);
        lock (ScanSync)
        {
            foreach (var u in chosen)
            {
                u.SetLanguage = code;
            }
        }

        int skipped = rows.Count - chosen.Count;
        return (chosen.Count, $"Set {chosen.Count:N0} track{(chosen.Count == 1 ? string.Empty : "s")} to {LanguageName(code)}."
            + (skipped > 0 ? $" Left {skipped:N0} alone (separate files, anime, or films with more than one audio track)." : string.Empty)
            + " They're written into the files the next time you press Strip tracks for real.");
    }

    /// <summary>
    /// Renames a separate subtitle file so Jellyfin knows its language, e.g. "Film (2009).srt" to
    /// "Film (2009).eng.srt". Only files from the last scan, and only text subtitle formats.
    /// </summary>
    public static (bool Ok, string Message, string? NewName) RenameSubtitle(string path, string language)
    {
        lock (ScanSync)
        {
            var row = _lastScan?.Untagged.FirstOrDefault(u => u.External && u.ExternalPath == path);
            if (row is null || string.IsNullOrEmpty(row.ExternalPath))
            {
                return (false, "That subtitle file isn't in the last scan. Scan again, then try.", null);
            }

            string ext = System.IO.Path.GetExtension(row.ExternalPath).ToLowerInvariant();
            if (ext is not (".srt" or ".ass" or ".ssa" or ".vtt"))
            {
                return (false, "Only .srt, .ass, .ssa and .vtt files can be renamed here.", null);
            }

            if (LanguageCode(language) is not { } code)
            {
                return (false, "Choose a language first.", null);
            }

            // Keep any flags Jellyfin reads from the name, so "Film.forced.srt" becomes "Film.eng.forced.srt".
            string flags = string.Concat(System.IO.Path.GetFileNameWithoutExtension(row.ExternalPath)
                .Split('.')
                .Skip(1)
                .Where(part => SubtitleFlags.Contains(part))
                .Select(part => "." + part.ToLowerInvariant()));
            string folder = System.IO.Path.GetDirectoryName(row.ExternalPath) ?? string.Empty;
            string target = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(row.Path) + "." + code + flags + ext);
            if (File.Exists(target))
            {
                return (false, $"There's already a file called {System.IO.Path.GetFileName(target)}.", null);
            }

            try
            {
                File.Move(row.ExternalPath, target);
            }
            catch (Exception ex)
            {
                return (false, "Couldn't rename it: " + ex.Message, null);
            }

            row.ExternalPath = target;
            row.SetLanguage = code;
            return (true, $"Renamed to {System.IO.Path.GetFileName(target)}. Jellyfin picks it up at the next library scan.", System.IO.Path.GetFileName(target));
        }
    }

    // Tracks were stripped since the scan, so its track numbers may no longer match the files.
    private const string StaleMessage = "Tracks have been stripped since the last scan, so its track numbers may be out of date. Scan again, then set the language.";

    // Words in a subtitle file's name that Jellyfin reads as flags rather than a language.
    private static readonly HashSet<string> SubtitleFlags = new(StringComparer.OrdinalIgnoreCase) { "forced", "foreign", "default", "sdh", "cc", "hi" };

    /// <summary>A 2 or 3 letter language code, as the 3 letter B form files use (e.g. "en" → "eng", "ell" → "gre"), or null if it isn't one.</summary>
    private static string? LanguageCode(string? language)
    {
        string code = (language ?? string.Empty).Trim().ToLowerInvariant();
        return code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z') ? LanguageCodes.FileCode(code) : null;
    }

    private static bool InLanguage(TrackInfo t, HashSet<string> keep) => keep.Contains(LanguageCodes.Canonical(t.Language));
}
