using Jellyfin.Plugin.MedicPosters.Posters;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPosters.Tests;

public sealed class PosterStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-posters-tests-").FullName;

    private PosterStore NewStore() => new(new Paths(_root), NullLogger<PosterStore>.Instance);

    [Fact]
    public void The_films_own_posters_are_remembered_across_restarts()
    {
        var id = Guid.NewGuid();
        NewStore().Update(s => s.Posters[id] = new PosterRecord { ItemId = id, Title = "Alien", Original = new SavedImage { Path = "/films/Alien/poster.jpg", Width = 680, Height = 1000 } });

        var record = NewStore().Read(s => s.Posters[id]);
        Assert.Equal("/films/Alien/poster.jpg", record.Original!.Path);
        Assert.Equal(1000, record.Original.Height);
    }

    [Fact]
    public void Only_its_own_posters_count_as_ours()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        Assert.True(store.IsOurs(store.PosterPath(id)));
        Assert.False(store.IsOurs("/films/Alien/poster.jpg"));
        Assert.False(store.IsOurs(store.PreviewPath));
        Assert.False(store.IsOurs(null));
        Assert.False(store.IsOurs("https://image.tmdb.org/t/p/original/a.jpg"));
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_instead_of_lost()
    {
        var dir = Path.Combine(_root, "data", "medic-posters");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "posters.json"), "{ not json");

        var store = NewStore();
        Assert.Empty(store.Read(s => s.Posters));
        Assert.True(File.Exists(Path.Combine(dir, "posters.json.bad")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Paths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;

        public string WebPath => Path.Combine(root, "web");

        public string ProgramSystemPath => Path.Combine(root, "system");

        public string DataPath => Path.Combine(root, "data");

        public string ImageCachePath => Path.Combine(root, "cache", "images");

        public string PluginsPath => Path.Combine(root, "plugins");

        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");

        public string LogDirectoryPath => Path.Combine(root, "log");

        public string ConfigurationDirectoryPath => Path.Combine(root, "config");

        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");

        public string CachePath => Path.Combine(root, "cache");

        public string TempDirectory => Path.Combine(root, "temp");

        public string VirtualDataPath => Path.Combine(root, "data");

        public string TrickplayPath => Path.Combine(root, "data", "trickplay");

        public string BackupPath => Path.Combine(root, "backups");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
