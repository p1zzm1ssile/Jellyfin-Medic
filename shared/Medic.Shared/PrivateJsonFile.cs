using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Medic.Shared;

/// <summary>
/// A JSON file of secrets, such as a plugin's keys file. On Unix only the account Jellyfin runs as can read it,
/// and a file saved by an older version that others could read is tightened the first time it's read. Each change
/// is read, made and written as one locked step, so two changes at once can't lose either, and a file that can't
/// be read is set aside rather than replaced by one holding only the newest change.
/// Medic Picks and Medic Profiles each compile this file in.
/// </summary>
internal sealed class PrivateJsonFile<T>
    where T : class, new()
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private readonly ILogger _logger;
    private readonly string _plugin;
    private readonly object _lock = new();
    private volatile bool _checkedPrivate;

    /// <param name="plugin">The plugin's name, which starts each line this writes to the log.</param>
    public PrivateJsonFile(string path, JsonSerializerOptions options, ILogger logger, string plugin)
    {
        _path = path;
        _options = options;
        _logger = logger;
        _plugin = plugin;
    }

    /// <summary>The file's contents, or a new <typeparamref name="T"/> when there's no file yet or it can't be read.</summary>
    public T Read()
    {
        try
        {
            return Parse() ?? new T();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Plugin}: could not read {Path}", _plugin, _path);
            return new T();
        }
    }

    /// <summary>Reads the file, changes it and writes it back as one step.</summary>
    public void Update(Action<T> change)
    {
        lock (_lock)
        {
            T contents;
            try
            {
                contents = Parse() ?? new T();
            }
            catch (JsonException ex)
            {
                string aside = AtomicFile.SetAside(_path);
                _logger.LogWarning(ex, "{Plugin}: {Path} couldn't be read, so it was kept as {Aside} and a new one started", _plugin, _path, aside);
                contents = new T();
            }

            change(contents);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.Write(_path, JsonSerializer.Serialize(contents, _options), ownerOnly: true);
        }
    }

    // The file's contents, or null when there isn't one yet. Throws when it can't be read.
    private T? Parse()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        if (!_checkedPrivate)
        {
            KeepPrivate(); // once: files written since are created private
            _checkedPrivate = true;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), _options);
    }

    private void KeepPrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (File.GetUnixFileMode(_path) != OwnerOnly)
            {
                File.SetUnixFileMode(_path, OwnerOnly);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Plugin}: could not make {Path} private", _plugin, _path);
        }
    }
}
