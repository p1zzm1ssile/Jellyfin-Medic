using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>A poster or library cover Medic Posters made, and the picture the item had before, so it can be put back.</summary>
public class PosterRecord
{
    public Guid ItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>"poster" for a film or series, "cover" for a library. Records from 1.0.0 have none, and were posters.</summary>
    public string Kind { get; set; } = "poster";

    public DateTime MadeUtc { get; set; }

    public long Bytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>How many frames it has, to estimate the size of the next ones; 0 for records from before 1.2.0.</summary>
    public int Frames { get; set; }

    /// <summary>The film's own poster before Medic Posters replaced it; null if it had none.</summary>
    public SavedImage? Original { get; set; }
}

/// <summary>Enough of a Jellyfin image entry to put it back exactly as it was.</summary>
public class SavedImage
{
    public string Path { get; set; } = string.Empty;

    public DateTime DateModified { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? BlurHash { get; set; }
}

/// <summary>A film a poster couldn't be made for, and why.</summary>
public class PosterFailure
{
    public Guid ItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTime Utc { get; set; }
}

/// <summary>What the last run did.</summary>
public class RunSummary
{
    public DateTime StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    public int Made { get; set; }

    public int Failed { get; set; }

    /// <summary>Films whose poster was changed outside Medic Posters since it made one, and were left alone.</summary>
    public int LeftAlone { get; set; }

    public bool Cancelled { get; set; }
}

/// <summary>Everything Medic Posters remembers, in one file.</summary>
public class PosterState
{
    public Dictionary<Guid, PosterRecord> Posters { get; set; } = new();

    public Dictionary<Guid, PosterFailure> Failures { get; set; } = new();

    public RunSummary? LastRun { get; set; }
}

/// <summary>
/// Keeps the posters (one GIF per film) and a record of what each film had before, under the server's
/// data folder, in medic-posters/. Nothing is written into your media folders.
/// </summary>
public class PosterStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly ILogger<PosterStore> _logger;
    private readonly object _lock = new();
    private PosterState? _state;

    public PosterStore(IApplicationPaths applicationPaths, ILogger<PosterStore> logger)
    {
        _root = Path.Combine(applicationPaths.DataPath, "medic-posters");
        _logger = logger;
    }

    private string StatePath => Path.Combine(_root, "posters.json");

    public string PosterPath(Guid itemId) => Path.Combine(_root, "posters", itemId.ToString("N") + ".gif");

    public string PreviewPath => Path.Combine(_root, "preview.gif");

    /// <summary>Whether a path is one of Medic Posters' own posters.</summary>
    public bool IsOurs(string? path) =>
        !string.IsNullOrEmpty(path)
        && string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty), Path.GetFullPath(Path.Combine(_root, "posters")), StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads, changes and saves the state as one step.</summary>
    public T Update<T>(Func<PosterState, T> change)
    {
        lock (_lock)
        {
            var state = Load();
            var result = change(state);
            Save(state);
            return result;
        }
    }

    /// <summary>Changes and saves the state as one step.</summary>
    public void Update(Action<PosterState> change) => Update<bool>(state =>
    {
        change(state);
        return true;
    });

    /// <summary>Reads the state; the result must not be changed.</summary>
    public T Read<T>(Func<PosterState, T> read)
    {
        lock (_lock)
        {
            return read(Load());
        }
    }

    private PosterState Load()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (File.Exists(StatePath))
            {
                _state = JsonSerializer.Deserialize<PosterState>(File.ReadAllText(StatePath), JsonOptions);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep the damaged file: it holds the only record of the films' own posters.
            _logger.LogError(ex, "Medic Posters: {Path} couldn't be read; it's kept as posters.json.bad", StatePath);
            try { File.Copy(StatePath, StatePath + ".bad", overwrite: true); } catch (IOException) { }
        }

        _state ??= new PosterState();
        return _state;
    }

    private void Save(PosterState state)
    {
        Directory.CreateDirectory(_root);
        string temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, StatePath, overwrite: true);
    }
}
