using System.Net;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

public sealed class ProfileAdvisorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ProfileAdvisor.AppData Data(params ProfileSummary[] profiles) => new()
    {
        Profiles = profiles.ToList(),
        CustomFormats = new List<string> { "x265 (HD)", "DTS" }
    };

    private static ProfileSummary Profile(int id, string name, params string[] allowed) =>
        new() { Id = id, Name = name, Allowed = allowed.ToList(), InUse = 1 };

    private static PlaybackEvidence Evidence(int transcoded, params (string Key, int Count)[] signals)
    {
        var e = new PlaybackEvidence { FromMedic = true, TranscodedFiles = transcoded };
        foreach (var (key, count) in signals)
        {
            e.Signals[key] = count;
        }

        return e;
    }

    private static ProfileAdvisor.AppData WithHevcPenalised()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p"), Profile(2, "UHD", "Bluray-2160p"));
        data.Scores[1] = new Dictionary<string, int> { ["x265 (HD)"] = -10000 };
        data.Scores[2] = new Dictionary<string, int> { ["x265 (HD)"] = -10000 };
        return data;
    }

    [Fact]
    public void An_empty_transcode_log_is_no_proof_that_hevc_plays_fine()
    {
        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, WithHevcPenalised(), Evidence(0, ("libFiles", 200), ("libHevc", 100)));

        Assert.DoesNotContain(advice, s => s.Title.StartsWith("HEVC plays fine", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hevc_tip_needs_transcodes_and_leaves_4K_profiles_out()
    {
        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, WithHevcPenalised(),
            Evidence(ProfileAdvisor.MinTranscodesForTip, ("libFiles", 200), ("libHevc", 100)));

        var tip = Assert.Single(advice, s => s.Title.StartsWith("HEVC plays fine", StringComparison.Ordinal));
        Assert.Equal(new[] { "HD" }, tip.Profiles);
    }

    [Fact]
    public void Dts_advice_skips_profiles_that_only_allow_remux()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p", "Remux-1080p"), Profile(2, "Remux 4K", "Remux-2160p"));

        var advice = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("dts", 5)));

        var dts = Assert.Single(advice, s => s.Title.Contains("DTS", StringComparison.Ordinal));
        Assert.Equal(new[] { "HD" }, dts.Profiles);
    }

    [Fact]
    public void A_bitrate_cap_on_small_files_doesnt_say_to_untick_remux()
    {
        var data = Data(Profile(1, "HD", "Bluray-1080p", "Remux-1080p"));

        var capped = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("bitrate", 8)));
        var remux = ProfileAdvisor.Advise(ArrApp.Radarr, data, Evidence(10, ("bitrate", 8), ("remux", 4)));

        Assert.DoesNotContain(capped, s => s.Title.StartsWith("Remux", StringComparison.Ordinal));
        Assert.Contains(remux, s => s.Title.StartsWith("Remux", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_remux_sized_file_counts_as_a_remux_transcode()
    {
        var signals = new Dictionary<string, int>();
        var reasons = new HashSet<string> { "ContainerBitrateExceedsLimit" };

        ProfileAdvisor.Count(signals, reasons, new ProfileAdvisor.FileFacts { Bitrate = 8_000_000 });
        ProfileAdvisor.Count(signals, reasons, new ProfileAdvisor.FileFacts { Bitrate = 40_000_000 });
        ProfileAdvisor.Count(signals, reasons, null);

        Assert.Equal(3, signals["bitrate"]);
        Assert.Equal(1, signals["remux"]);
    }

    [Fact]
    public void A_remux_without_stream_bitrates_is_sized_by_the_whole_file()
    {
        // MKV video often has no BPS tag, so only the container's bitrate tells a Remux from an encode.
        var film = new Film { Id = Guid.NewGuid(), TotalBitrate = 40_000_000 };
        var log = Path.Combine(_root, "plugins", "configurations", "JellyfinMedic", "transcode_log.json");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, $$$"""{"Items":[{"ItemId":"{{{film.Id}}}","Reasons":{"ContainerBitrateExceedsLimit":1}}]}""");

        var evidence = Advisor(FakeLibrary.With(film)).GatherEvidence();

        Assert.Equal(1, evidence.Signals["bitrate"]);
        Assert.Equal(1, evidence.Signals.GetValueOrDefault("remux"));
    }

    [Fact]
    public async Task A_report_started_before_the_settings_changed_isnt_kept()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int sampled = 0;
        var library = FakeLibrary.With(sampling: () =>
        {
            if (Interlocked.Increment(ref sampled) == 1)
            {
                entered.SetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
            }
        });
        var advisor = Advisor(library);

        var running = Task.Run(() => advisor.GetAsync(refresh: true, CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        advisor.Forget();
        release.SetResult();
        var stale = await running;
        var next = await advisor.GetAsync(refresh: false, CancellationToken.None);

        Assert.NotSame(stale, next);
        Assert.Equal(2, sampled);
    }

    private ProfileAdvisor Advisor(ILibraryManager library)
    {
        // No keys saved, so Sonarr and Radarr count as not set up and only the server's own evidence is read.
        var paths = new TestPaths(_root);
        var arr = new ArrClient(null!, new ArrStore(paths, NullLogger<ArrStore>.Instance), NullLogger<ArrClient>.Instance);
        return new ProfileAdvisor(arr, library, paths, NullLogger<ProfileAdvisor>.Instance);
    }

    /// <summary>A 4K HEVC film whose streams carry no bitrate, as Jellyfin lists an MKV without BPS tags.</summary>
    private sealed class Film : Movie
    {
        public override IReadOnlyList<MediaStream> GetMediaStreams() => new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Height = 2160 },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd" }
        };
    }
}

// Sonarr and Radarr answers that are JSON of the wrong shape, or no JSON at all, are named in the error.
[Collection(PluginInstanceCollection.Name)]
public sealed class UnexpectedAnswerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly TestPaths _paths;
    private readonly ArrStore _store;

    public UnexpectedAnswerTests()
    {
        _paths = new TestPaths(_root);
        var plugin = new Plugin(_paths, new DefaultSettingsSerializer());
        plugin.Configuration.SonarrUrl = "http://sonarr:8989";
        _store = new ArrStore(_paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Sonarr, "sonarr-key");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Sign in</body></html>", "text/html", "Sonarr sent something unexpected: an empty answer, or one that isn't JSON (a web page, say).")]
    [InlineData("", "text/plain", "Sonarr sent something unexpected: an empty answer, or one that isn't JSON (a web page, say).")]
    [InlineData("[]", "application/json", "Sonarr sent something unexpected: a list where an object was expected.")]
    [InlineData("\"ok\"", "application/json", "Sonarr sent something unexpected: a piece of text where an object was expected.")]
    public async Task The_queue_error_says_what_came_back(string body, string type, string message)
    {
        var (_, errors, _) = await new DownloadsService(Client(body, type), _store).QueueAsync(CancellationToken.None);

        Assert.Equal(message, errors["sonarr"]);
    }

    [Fact]
    public async Task The_blocked_list_error_says_what_came_back()
    {
        var (_, errors, _) = await new DownloadsService(Client("[]"), _store).BlockedAsync(CancellationToken.None);

        Assert.Equal("Sonarr sent something unexpected: a list where an object was expected.", errors["sonarr"]);
    }

    [Fact]
    public async Task The_library_search_error_says_what_came_back()
    {
        var (_, error) = await new DownloadsService(Client("{}"), _store).SearchLibraryAsync(ArrApp.Sonarr, "Show", CancellationToken.None);

        Assert.Equal("Sonarr sent something unexpected: an object where a list was expected.", error);
    }

    [Fact]
    public async Task The_episode_list_error_says_what_came_back()
    {
        var (_, error) = await new DownloadsService(Client("{}"), _store).EpisodesAsync(7, CancellationToken.None);

        Assert.Equal("Sonarr sent something unexpected: an object where a list was expected.", error);
    }

    [Fact]
    public async Task The_indexer_error_says_what_came_back()
    {
        var (_, _, errors) = await new IndexersService(Client("{}")).StatusAsync(CancellationToken.None);

        Assert.Equal("Sonarr sent something unexpected: an object where a list was expected.", errors["sonarr"]);
    }

    [Fact]
    public async Task The_advice_error_says_what_came_back()
    {
        var advisor = new ProfileAdvisor(Client("{}"), FakeLibrary.With(), _paths, NullLogger<ProfileAdvisor>.Instance);

        var report = await advisor.GetAsync(refresh: true, CancellationToken.None);

        Assert.Equal("Sonarr sent something unexpected: an object where a list was expected.", report.Errors["sonarr"]);
    }

    private ArrClient Client(string body, string type = "application/json") =>
        new(new Factory(new Answers(body, type)), _store, NullLogger<ArrClient>.Instance);

    private sealed class Answers(string body, string type) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

/// <summary>A library manager that finds the given items and samples nothing, which is all the advisor asks of it.</summary>
internal class FakeLibrary : DispatchProxy
{
    private Dictionary<Guid, BaseItem> _items = new();
    private Action _sampling = () => { };

    public static ILibraryManager With(params BaseItem[] items) => With(() => { }, items);

    public static ILibraryManager With(Action sampling, params BaseItem[] items)
    {
        var library = Create<ILibraryManager, FakeLibrary>();
        var fake = (FakeLibrary)(object)library;
        fake._items = items.ToDictionary(i => i.Id);
        fake._sampling = sampling;
        return library;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(ILibraryManager.GetItemById) when !targetMethod.IsGenericMethod:
                return _items.GetValueOrDefault((Guid)args![0]!);
            case nameof(ILibraryManager.GetItemIds):
                _sampling();
                return new List<Guid>();
            default:
                throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
