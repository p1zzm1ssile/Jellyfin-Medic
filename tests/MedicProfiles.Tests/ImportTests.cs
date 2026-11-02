using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MedicProfiles;
using Jellyfin.Plugin.MedicProfiles.Arr;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicProfiles.Tests;

/// <summary>Import by hand: what a download's files are read as, and what is sent to Radarr.</summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class ImportTests : IDisposable
{
    private const string Files = """
        [
          { "path": "/downloads/Film/film.mkv", "relativePath": "film.mkv", "size": 1000,
            "quality": { "quality": { "name": "Bluray-1080p" } }, "languages": [ { "id": 1, "name": "English" } ],
            "movie": { "id": 7, "title": "Film", "year": 2020 },
            "rejections": [ { "reason": "Not an upgrade" }, "Not an upgrade", { "reason": "Sample" } ] },
          { "path": "", "relativePath": "no-path.nfo" }
        ]
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("medic-profiles-tests-").FullName;
    private readonly ArrStore _store;
    private readonly List<(HttpMethod Method, string Path, string? Body)> _sent = new();

    public ImportTests()
    {
        var paths = new TestPaths(_root);
        var plugin = new Plugin(paths, new DefaultSettingsSerializer());
        plugin.Configuration.RadarrUrl = "http://radarr:7878";
        _store = new ArrStore(paths, NullLogger<ArrStore>.Instance);
        _store.SetKey(ArrApp.Radarr, "radarr-key");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Each_rejection_is_listed_once_and_files_without_a_path_are_left_out()
    {
        var (files, error) = await Service("""{"id":1}""").ImportFilesAsync(ArrApp.Radarr, "abc", CancellationToken.None);

        Assert.Null(error);
        var file = Assert.Single(files!);
        Assert.Equal(new[] { "Not an upgrade", "Sample" }, file.Rejections);
        Assert.Equal(7, file.MediaId);
        Assert.Equal("Film (2020)", file.MediaTitle);
    }

    [Fact]
    public async Task Only_files_in_the_download_are_imported()
    {
        var (ok, message) = await Service("""{"id":1}""").ImportAsync(ArrApp.Radarr, "abc", "move",
            new List<ImportFileRequest> { new() { Path = "/etc/passwd", MediaId = 7 } }, "Film", null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("isn't part of this download", message, StringComparison.Ordinal);
        Assert.DoesNotContain(_sent, s => s.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task The_import_is_sent_as_Radarrs_own_manual_import()
    {
        var quality = JsonDocument.Parse("""{ "quality": { "name": "Bluray-1080p" } }""").RootElement;
        var (ok, _) = await Service("""{"id":42}""").ImportAsync(ArrApp.Radarr, "abc", "copy",
            new List<ImportFileRequest> { new() { Path = "/downloads/Film/film.mkv", MediaId = 7, QualityRaw = quality } }, "Film", null, CancellationToken.None);

        Assert.True(ok);
        var post = Assert.Single(_sent, s => s.Method == HttpMethod.Post);
        Assert.EndsWith("/api/v3/command", post.Path, StringComparison.Ordinal);
        using var command = JsonDocument.Parse(post.Body!);
        Assert.Equal("ManualImport", command.RootElement.GetProperty("name").GetString());
        Assert.Equal("copy", command.RootElement.GetProperty("importMode").GetString());
        var file = command.RootElement.GetProperty("files")[0];
        Assert.Equal(7, file.GetProperty("movieId").GetInt32());
        Assert.Equal("abc", file.GetProperty("downloadId").GetString());
        Assert.Equal("Bluray-1080p", file.GetProperty("quality").GetProperty("quality").GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_import_Radarr_didnt_confirm_isnt_reported_as_started()
    {
        var (ok, message) = await Service("[]").ImportAsync(ArrApp.Radarr, "abc", "auto",
            new List<ImportFileRequest> { new() { Path = "/downloads/Film/film.mkv", MediaId = 7 } }, "Film", null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("didn't confirm the import", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_without_its_film_isnt_sent()
    {
        var (ok, message) = await Service("""{"id":1}""").ImportAsync(ArrApp.Radarr, "abc", "auto",
            new List<ImportFileRequest> { new() { Path = "/downloads/Film/film.mkv" } }, "Film", null, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("Choose the film for every file you're importing.", message);
    }

    private DownloadsService Service(string commandAnswer)
    {
        var arr = new ArrClient(new Factory(new Answers(async request =>
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            _sent.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            string answer = request.Method == HttpMethod.Post ? commandAnswer : Files;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json"), RequestMessage = request };
        })), _store, NullLogger<ArrClient>.Instance);
        return new DownloadsService(arr, _store);
    }

    private sealed class Answers(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
