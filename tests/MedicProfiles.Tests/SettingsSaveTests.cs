using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Api;
using Jellyfin.Plugin.MedicProfiles.Arr;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>Saving the settings when a key or the addresses can't be written, as on a full disk.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class SettingsSaveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly Plugin _plugin;
    private readonly FailingStore _store;
    private readonly ProfilesController _controller;

    public SettingsSaveTests()
    {
        var paths = new TestPaths(_root);
        _plugin = new Plugin(paths, new FailingSettingsSerializer());
        _plugin.Configuration.SonarrUrl = "http://old-sonarr:8989";
        _plugin.Configuration.QbitUrl = "http://old-qbit:8080";
        _plugin.Configuration.QbitUser = "admin";
        _store = new FailingStore(paths);
        _store.SetKey(ArrApp.Sonarr, "old-key");
        _store.SetQbitPassword("old-password");

        var arr = new ArrClient(new NoHttp(), _store, NullLogger<ArrClient>.Instance);
        _controller = new ProfilesController(
            _store,
            arr,
            new DownloadsService(arr, _store),
            new ProfileAdvisor(arr, null!, paths, NullLogger<ProfileAdvisor>.Instance),
            new IndexersService(arr),
            new QbitFileFilter(_store, NullLogger<QbitFileFilter>.Instance),
            NullLogger<ProfilesController>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_new_server_s_key_is_not_left_paired_with_the_old_address()
    {
        var result = _controller.SaveSettings(new ProfilesController.SettingsRequest
        {
            SonarrUrl = "http://new-sonarr:8989",
            SonarrKey = "new-key",
            RadarrUrl = string.Empty,
            QbitUrl = "http://new-qbit:8080",
            QbitUser = "someone",
            QbitPassword = "new-password"
        });

        AssertNothingWasChanged(result);
        Assert.Equal("http://old-sonarr:8989", _plugin.Configuration.SonarrUrl);
        Assert.Equal("old-key", _store.GetKey(ArrApp.Sonarr));
        Assert.Equal(("http://old-qbit:8080", "admin"), (_plugin.Configuration.QbitUrl, _plugin.Configuration.QbitUser));
        Assert.Equal("old-password", _store.GetQbitPassword());
    }

    [Fact]
    public void A_new_key_for_the_same_server_is_put_back_too()
    {
        var result = _controller.SaveSettings(new ProfilesController.SettingsRequest
        {
            SonarrUrl = "http://old-sonarr:8989",
            SonarrKey = "new-key",
            RadarrUrl = string.Empty,
            StuckAfterHours = 24
        });

        AssertNothingWasChanged(result);
        Assert.Equal("old-key", _store.GetKey(ArrApp.Sonarr));
        Assert.Equal(6, _plugin.Configuration.StuckAfterHours);
    }

    [Fact]
    public void A_key_that_can_t_be_written_puts_back_the_password_written_before_it()
    {
        _plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store.SetKey(ArrApp.Radarr, "old-radarr-key");
        _store.FailOn = "new-radarr-key";

        var result = _controller.SaveSettings(new ProfilesController.SettingsRequest
        {
            SonarrUrl = "http://old-sonarr:8989",
            RadarrUrl = "http://radarr:7878",
            RadarrKey = "new-radarr-key",
            QbitUrl = "http://old-qbit:8080",
            QbitUser = "admin",
            QbitPassword = "new-password"
        });

        AssertNothingWasChanged(result);
        Assert.Equal("old-password", _store.GetQbitPassword());
        Assert.Equal("old-radarr-key", _store.GetKey(ArrApp.Radarr));
        Assert.Equal("old-key", _store.GetKey(ArrApp.Sonarr));
    }

    private static void AssertNothingWasChanged(ActionResult result)
    {
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(500, problem.Status);
        Assert.Contains("nothing was changed", problem.Detail, StringComparison.Ordinal);
    }

    // Reads the defaults like DefaultSettingsSerializer, but writing the settings fails.
    private sealed class FailingSettingsSerializer : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type);

        public void SerializeToStream(object obj, Stream stream) => throw new IOException("No space left on device");

        public void SerializeToFile(object obj, string file) => throw new IOException("No space left on device");

        public object? DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type);

        public object? DeserializeFromBytes(Type type, byte[] buffer) => Activator.CreateInstance(type);
    }

    // Writing the keys file fails on one value, as a full disk would on that write.
    private sealed class FailingStore(TestPaths paths) : ArrStore(paths, NullLogger<ArrStore>.Instance)
    {
        public string? FailOn { get; set; }

        public override void SetKey(ArrApp app, string? key)
        {
            ThrowIfFailing(key);
            base.SetKey(app, key);
        }

        public override void SetQbitPassword(string? password)
        {
            ThrowIfFailing(password);
            base.SetQbitPassword(password);
        }

        private void ThrowIfFailing(string? value)
        {
            if (value is not null && value == FailOn)
            {
                throw new IOException("No space left on device");
            }
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Saving the settings sends nothing");
    }
}
