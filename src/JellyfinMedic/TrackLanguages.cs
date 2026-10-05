using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace JellyfinMedic.Services;

/// <summary>
/// Languages you've set by hand for tracks with no language tag, keyed by file and track number.
/// Kept in Medic's own settings folder; written into the file itself the next time tracks are stripped.
/// </summary>
public static class TrackLanguages
{
    private static readonly object Sync = new();
    private static Dictionary<string, Dictionary<int, string>>? _map;
    private static string? _file;

    /// <summary>Called once with Medic's settings folder.</summary>
    public static void Init(string pluginConfigurationsPath)
    {
        lock (Sync)
        {
            if (_file is not null)
            {
                return;
            }

            _file = Path.Combine(pluginConfigurationsPath, "JellyfinMedic", "track_languages.json");
            try
            {
                _map = File.Exists(_file)
                    ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, string>>>(File.ReadAllText(_file))
                    : null;
            }
            catch
            {
                _map = null;
            }

            _map ??= new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        }
    }

    public static string? Get(string path, int index)
    {
        lock (Sync)
        {
            return _map is not null && _map.TryGetValue(path, out var tracks) && tracks.TryGetValue(index, out var code) ? code : null;
        }
    }

    public static void SetMany(IEnumerable<(string Path, int Index)> tracks, string code)
    {
        lock (Sync)
        {
            _map ??= new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            foreach (var (path, index) in tracks)
            {
                if (!_map.TryGetValue(path, out var list))
                {
                    list = new Dictionary<int, string>();
                    _map[path] = list;
                }

                list[index] = code;
            }

            Write();
        }
    }

    /// <summary>
    /// Forgets every language set by hand for a file, once they've been written into it. Track numbers
    /// change when tracks are stripped, so an old entry would otherwise land on a different track.
    /// </summary>
    public static void ClearFile(string path)
    {
        lock (Sync)
        {
            if (_map is not null && _map.Remove(path))
            {
                Write();
            }
        }
    }

    // Written to a temporary file first, so a crash mid-write can't wipe the whole list.
    private static void Write()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // Kept in memory until the next restart.
        }
    }

    public static void Set(string path, int index, string? code)
    {
        lock (Sync)
        {
            _map ??= new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            if (!_map.TryGetValue(path, out var tracks))
            {
                tracks = new Dictionary<int, string>();
                _map[path] = tracks;
            }

            if (code is null)
            {
                tracks.Remove(index);
                if (tracks.Count == 0)
                {
                    _map.Remove(path);
                }
            }
            else
            {
                tracks[index] = code;
            }

            Write();
        }
    }
}
