using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.MedicPicks;
using Jellyfin.Plugin.MedicPicks.Api;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicPicks.Tests;

// Plugin.Instance is static and other test classes make a Plugin too, so these tests run on their own,
// apart from the other test classes instead of alongside them.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}

[Collection(PluginInstanceCollection.Name)]
public sealed class PicksControllerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("medic-picks-tests-").FullName;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly PicksStore _store;
    private readonly Plugin _plugin;
    private readonly SeerrCalls _seerrCalls = new();
    private readonly PicksController _controller;

    public PicksControllerTests()
    {
        var paths = new TestPaths(_root);
        _store = new PicksStore(paths, NullLogger<PicksStore>.Instance);
        _plugin = new Plugin(paths, new DefaultSettingsSerializer());
        var seerr = new SeerrClient(new OneClient(new HttpClient(_seerrCalls)), _store, NullLogger<SeerrClient>.Instance);

        // Only the store, Seerr, the logger and the language lookup are used by the routes tested here.
        _controller = new PicksController(_store, null!, null!, DispatchProxy.Create<ILocalizationManager, NoLanguages>(), null!, seerr, null!, NullLogger<PicksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", _userId.ToString()) }, "test"))
                }
            }
        };

        _store.Save(_userId, new UserPicks
        {
            GeneratedUtc = DateTime.UtcNow,
            Discover = { new DiscoverPick { TmdbId = 603, MediaType = "movie", Title = "The Matrix" } }
        });
    }

    [Fact]
    public async Task Stored_Discover_picks_are_served_while_Discover_is_on_for_the_person()
    {
        Save(new PluginConfiguration { EnableDiscover = true });

        Assert.Equal(1, (await MyPicks()).GetProperty("discover").GetArrayLength());
    }

    [Fact]
    public async Task Stored_Discover_picks_are_not_served_once_Discover_is_off_on_the_server()
    {
        // The picks were saved while Discover was on; the next build may be a day away.
        Save(new PluginConfiguration { EnableDiscover = false });

        Assert.Equal(0, (await MyPicks()).GetProperty("discover").GetArrayLength());
    }

    [Fact]
    public async Task Stored_Discover_picks_are_not_served_to_a_person_unticked_from_Discover()
    {
        Save(new PluginConfiguration { EnableDiscover = true, DiscoverDisabledUserIds = new[] { _userId.ToString("N") } });

        Assert.Equal(0, (await MyPicks()).GetProperty("discover").GetArrayLength());
    }

    [Fact]
    public async Task Show_different_ones_skips_only_the_Discover_picks_that_were_shown()
    {
        Save(new PluginConfiguration { EnableDiscover = false });
        await _controller.ShowDifferent();
        Assert.Empty(_store.LoadPreferences(_userId).SeenTmdb);

        Save(new PluginConfiguration { EnableDiscover = true });
        await _controller.ShowDifferent();
        Assert.Equal(new[] { PicksStore.TmdbKey("movie", 603) }, _store.LoadPreferences(_userId).SeenTmdb);
    }

    [Fact]
    public async Task Direct_requests_do_not_send_the_Seerr_address()
    {
        // The address the server uses (inside Docker, say) is no use to people when the page makes the request itself.
        Save(new PluginConfiguration { JellyseerrUrl = "http://seerr:5055", SeerrDirectRequests = true });
        _store.SetSeerrKey("seerr-key");

        var me = await MyPicks();

        Assert.Equal("direct", me.GetProperty("requestMode").GetString());
        Assert.False(me.TryGetProperty("seerrUrl", out _));
    }

    [Fact]
    public async Task Links_to_Seerr_send_the_Seerr_address()
    {
        Save(new PluginConfiguration { JellyseerrUrl = "http://seerr:5055", SeerrDirectRequests = false });

        var me = await MyPicks();

        Assert.Equal("link", me.GetProperty("requestMode").GetString());
        Assert.Equal("http://seerr:5055", me.GetProperty("seerrUrl").GetString());
    }

    [Fact]
    public async Task Requests_are_refused_while_Discover_is_off_on_the_server()
    {
        Save(new PluginConfiguration { EnableDiscover = false, JellyseerrUrl = "http://seerr:5055", SeerrDirectRequests = true });
        _store.SetSeerrKey("seerr-key");

        var result = await RequestTitle();

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, _seerrCalls.Count);
    }

    [Fact]
    public async Task Requests_reach_Seerr_while_Discover_is_on_for_the_person()
    {
        Save(new PluginConfiguration { EnableDiscover = true, JellyseerrUrl = "http://seerr:5055", SeerrDirectRequests = true });
        _store.SetSeerrKey("seerr-key");

        var result = await RequestTitle();

        Assert.IsType<ContentResult>(result);
        Assert.True(_seerrCalls.Count > 0);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Save(PluginConfiguration config) => _plugin.UpdateConfiguration(config);

    private async Task<JsonElement> MyPicks()
    {
        var result = Assert.IsType<ContentResult>(await _controller.GetMyPicks());
        return JsonDocument.Parse(result.Content!).RootElement.Clone();
    }

    private Task<ActionResult> RequestTitle() =>
        _controller.RequestTitle(new PicksController.TitleRequest { TmdbId = 603, MediaType = "movie" }, CancellationToken.None);

    private sealed class SeerrCalls : HttpMessageHandler
    {
        private int _count;

        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OneClient(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    // A localization manager that knows no languages, so the dubbed-audio language falls back to none.
    public class NoLanguages : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
