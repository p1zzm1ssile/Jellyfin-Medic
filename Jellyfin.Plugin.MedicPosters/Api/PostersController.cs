using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MedicPosters.Configuration;
using Jellyfin.Plugin.MedicPosters.Posters;
using Jellyfin.Plugin.MedicPosters.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MedicPosters.Api;

/// <summary>Everything under /MedicPosters is for admins only: it changes every film's poster.</summary>
[ApiController]
[Route("MedicPosters")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class PostersController : ControllerBase
{
    private readonly PosterService _service;
    private readonly PosterStore _store;
    private readonly ITaskManager _taskManager;

    public PostersController(PosterService service, PosterStore store, ITaskManager taskManager)
    {
        _service = service;
        _store = store;
        _taskManager = taskManager;
    }

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var titles = _service.Titles();
        var libraries = _service.ChosenLibraries();
        var (posters, failures, lastRun) = _store.Read(s => (
            s.Posters.Values.OrderByDescending(p => p.MadeUtc).ToList(),
            s.Failures.Values.OrderByDescending(f => f.Utc).Take(50).ToList(),
            s.LastRun));
        var ids = titles.Select(t => t.Id).Concat(libraries.Select(l => l.Id)).ToHashSet();
        var mine = posters.Where(p => ids.Contains(p.ItemId)).ToList();
        var minePosters = mine.Where(p => p.Kind != PosterService.CoverKind).ToList();
        var made = mine.Select(p => p.ItemId).ToHashSet();
        // Streams (IPTV, .strm) never get one, so they don't count towards what a run will make.
        var makeable = titles.Where(t => !t.IsShortcut).ToList();
        var progress = _service.Progress;
        bool running = progress is not null
            || _taskManager.ScheduledTasks.Any(t => t.ScheduledTask is MakePostersTask && t.State != TaskState.Idle);

        return Json(new
        {
            settings = new
            {
                libraryIds = config.LibraryIds,
                excludedLibraryIds = config.ExcludedLibraryIds,
                width = config.Width,
                moments = config.Moments,
                momentTenths = config.MomentTenths,
                framesPerSecond = config.FramesPerSecond,
                colours = config.Colours,
                showTitle = config.ShowTitle,
                showFrame = config.ShowFrame,
                makeLibraryCovers = config.MakeLibraryCovers,
                coverWidth = config.CoverWidth,
                coverMoments = config.CoverMoments,
                coverMomentTenths = config.CoverMomentTenths,
                coverFramesPerSecond = config.CoverFramesPerSecond,
                coverColours = config.CoverColours,
                coverShowTitle = config.CoverShowTitle,
                coverShowFrame = config.CoverShowFrame
            },
            libraries = _service.Libraries().Select(l => new { id = l.Id.ToString("N"), name = l.Name }),
            chosenLibraries = libraries.Select(l => new { id = l.Id.ToString("N"), name = l.Name, hasCover = made.Contains(l.Id) }),
            // "films" is every title that gets a poster (films, series, home and music videos); the name is kept for 1.0's page.
            films = titles.Count,
            made = minePosters.Count,
            averageBytes = minePosters.Count == 0 ? 0 : (long)minePosters.Average(p => p.Bytes),
            coverLibraries = config.MakeLibraryCovers ? libraries.Count : 0,
            coversMade = mine.Count(p => p.Kind == PosterService.CoverKind),
            // For the page's size estimate, which it works out from the settings on screen.
            estimate = new
            {
                posters = makeable.Count,
                postersToMake = makeable.Count(t => !made.Contains(t.Id)),
                covers = libraries.Count,
                coversToMake = libraries.Count(l => !made.Contains(l.Id)),
                bytesNow = mine.Sum(p => p.Bytes),
                posterRate = Rate(minePosters),
                coverRate = Rate(mine.Where(p => p.Kind == PosterService.CoverKind))
            },
            running,
            progress,
            lastRun,
            failures,
            recent = mine.Take(24).Select(p => new { itemId = p.ItemId.ToString("N"), title = p.Title, kind = p.Kind, bytes = p.Bytes, madeUtc = p.MadeUtc })
        });
    }

    /// <summary>Bytes per pixel per frame of the ones made so far, or a typical value before any are.</summary>
    internal static double Rate(System.Collections.Generic.IEnumerable<PosterRecord> records)
    {
        var known = records.Where(r => r.Frames > 0 && r.Width > 0 && r.Height > 0 && r.Bytes > 0).ToList();
        return known.Count == 0
            ? PosterService.DefaultBytesPerPixelFrame
            : known.Sum(r => (double)r.Bytes) / known.Sum(r => (double)r.Width * r.Height * r.Frames);
    }

    [HttpPost("Settings")]
    public ActionResult SaveSettings([FromBody] PosterSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest();
        }

        var plugin = Plugin.Instance!;
        Apply(plugin.Configuration, request, _service.Libraries().Select(l => l.Id).ToHashSet());
        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>Copies the settings sent onto a configuration, kept to the ranges the page offers.</summary>
    internal static void Apply(PluginConfiguration c, PosterSettingsRequest request, System.Collections.Generic.ISet<Guid> known)
    {
        string[] Ids(string[] ids) => ids
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(known.Contains)
            .Select(g => g.ToString("N"))
            .Distinct()
            .ToArray();

        if (request.LibraryIds is not null)
        {
            c.LibraryIds = Ids(request.LibraryIds);
        }

        if (request.ExcludedLibraryIds is not null)
        {
            c.ExcludedLibraryIds = Ids(request.ExcludedLibraryIds);
        }

        c.Width = Math.Clamp(request.Width ?? c.Width, 120, 600) / 2 * 2;
        c.Moments = Math.Clamp(request.Moments ?? c.Moments, 1, 6);
        c.MomentTenths = Math.Clamp(request.MomentTenths ?? c.MomentTenths, 5, 30);
        c.FramesPerSecond = Math.Clamp(request.FramesPerSecond ?? c.FramesPerSecond, 4, 15);
        c.Colours = Math.Clamp(request.Colours ?? c.Colours, 16, 256);
        c.ShowTitle = request.ShowTitle ?? c.ShowTitle;
        c.ShowFrame = request.ShowFrame ?? c.ShowFrame;
        c.MakeLibraryCovers = request.MakeLibraryCovers ?? c.MakeLibraryCovers;
        c.CoverWidth = Math.Clamp(request.CoverWidth ?? c.CoverWidth, 240, 960) / 2 * 2;
        c.CoverMoments = Math.Clamp(request.CoverMoments ?? c.CoverMoments, 1, 6);
        c.CoverMomentTenths = Math.Clamp(request.CoverMomentTenths ?? c.CoverMomentTenths, 5, 30);
        c.CoverFramesPerSecond = Math.Clamp(request.CoverFramesPerSecond ?? c.CoverFramesPerSecond, 4, 15);
        c.CoverColours = Math.Clamp(request.CoverColours ?? c.CoverColours, 16, 256);
        c.CoverShowTitle = request.CoverShowTitle ?? c.CoverShowTitle;
        c.CoverShowFrame = request.CoverShowFrame ?? c.CoverShowFrame;
    }

    /// <summary>Makes a poster for one title, or a cover for one library (a random one if none is given), without changing anything.</summary>
    [HttpPost("Preview")]
    public async Task<ActionResult> Preview([FromBody] PosterPreviewRequest? request, CancellationToken ct)
    {
        string kind = request?.Kind == PosterService.CoverKind ? PosterService.CoverKind : PosterService.PosterKind;
        PluginConfiguration? settings = null;
        if (request?.Settings is { } sent)
        {
            // A copy of the saved settings with the ones on screen on top; nothing is saved.
            settings = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(Plugin.Instance!.Configuration))!;
            Apply(settings, sent, _service.Libraries().Select(l => l.Id).ToHashSet());
        }

        var (film, error) = await _service.PreviewAsync(request?.ItemId, kind, settings, ct).ConfigureAwait(false);
        long bytes = error is null && System.IO.File.Exists(_store.PreviewPath) ? new FileInfo(_store.PreviewPath).Length : 0;
        return Json(new { ok = error is null, error, kind, itemId = film?.Id.ToString("N"), title = film?.Name, bytes });
    }

    [HttpGet("Preview/Image")]
    [Produces("image/gif")]
    public ActionResult PreviewImage()
    {
        if (!System.IO.File.Exists(_store.PreviewPath))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(_store.PreviewPath, "image/gif");
    }

    /// <summary>Uses the last preview as that film's, series' or library's picture.</summary>
    [HttpPost("Preview/Use")]
    public async Task<ActionResult> UsePreview(CancellationToken ct)
    {
        var (item, error) = await _service.UsePreviewAsync(ct).ConfigureAwait(false);
        return Json(new { ok = error is null, error, itemId = item?.Id.ToString("N"), title = item?.Name });
    }

    /// <summary>Films and series in the chosen libraries whose name contains the text, for choosing one.</summary>
    [HttpGet("Titles")]
    public ActionResult FindTitles([FromQuery] string? q)
    {
        var found = string.IsNullOrWhiteSpace(q) ? Array.Empty<MediaBrowser.Controller.Entities.BaseItem>() : _service.FindTitles(q.Trim(), 20);
        return Json(found.Select(t => new { id = t.Id.ToString("N"), name = t.Name, year = t.ProductionYear }));
    }

    /// <summary>
    /// Starts the scheduled task now. With redoAll, ones that already have a poster get a new one too; kind
    /// "poster" or "cover" makes only those.
    /// </summary>
    [HttpPost("Run")]
    public ActionResult Run([FromQuery] bool redoAll = false, [FromQuery] string? kind = null)
    {
        if (_service.Progress is not null)
        {
            return Conflict(new { error = "Posters are already being made." });
        }

        _service.RedoAllNextRun = redoAll;
        _service.NextRunKind = kind is PosterService.PosterKind or PosterService.CoverKind ? kind : null;
        _taskManager.QueueScheduledTask<MakePostersTask>();
        return Accepted();
    }

    /// <summary>Stops a run that's going.</summary>
    [HttpPost("Stop")]
    public ActionResult Stop()
    {
        _taskManager.CancelIfRunning<MakePostersTask>();
        return NoContent();
    }

    [HttpPost("Restore/{itemId}")]
    public async Task<ActionResult> Restore([FromRoute] Guid itemId, CancellationToken ct) =>
        await _service.RestoreAsync(itemId, ct).ConfigureAwait(false) ? NoContent() : NotFound();

    /// <summary>Puts every film's own poster back.</summary>
    [HttpPost("RestoreAll")]
    public async Task<ActionResult> RestoreAll(CancellationToken ct)
    {
        if (_service.Progress is not null)
        {
            return Conflict(new { error = "Stop making posters first." });
        }

        int count = await _service.RestoreAllAsync(ct).ConfigureAwait(false);
        return Json(new { restored = count });
    }

    // Jellyfin's own JSON is PascalCase; the page reads camelCase, like the other Medic plugins.
    private ContentResult Json(object body) =>
        Content(JsonSerializer.Serialize(body, PosterStore.JsonOptions), MediaTypeNames.Application.Json);
}

public class PosterSettingsRequest
{
    public string[]? LibraryIds { get; set; }

    public int? Width { get; set; }

    public int? Moments { get; set; }

    public int? MomentTenths { get; set; }

    public int? FramesPerSecond { get; set; }

    public int? Colours { get; set; }

    public bool? ShowTitle { get; set; }

    public bool? ShowFrame { get; set; }

    public bool? MakeLibraryCovers { get; set; }

    public int? CoverWidth { get; set; }

    public string[]? ExcludedLibraryIds { get; set; }

    public int? CoverMoments { get; set; }

    public int? CoverMomentTenths { get; set; }

    public int? CoverFramesPerSecond { get; set; }

    public int? CoverColours { get; set; }

    public bool? CoverShowTitle { get; set; }

    public bool? CoverShowFrame { get; set; }
}

public class PosterPreviewRequest
{
    public Guid? ItemId { get; set; }

    /// <summary>"cover" for a library cover; anything else is a poster.</summary>
    public string? Kind { get; set; }

    /// <summary>The settings on the page, saved or not; the saved ones when missing.</summary>
    public PosterSettingsRequest? Settings { get; set; }
}
