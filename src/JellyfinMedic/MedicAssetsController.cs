using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMedic.Api;

/// <summary>
/// Serves the files the Medic page loads by address, such as its stylesheet. A stylesheet link carries no Jellyfin
/// sign-in, so this is open to anyone; it only ever serves the files listed here, none of which say anything about
/// the server.
/// </summary>
[ApiController]
[Route("JellyfinMedic/Assets")]
[AllowAnonymous]
public class MedicAssetsController : ControllerBase
{
    private static readonly Dictionary<string, (string Resource, string ContentType)> Files = new(StringComparer.Ordinal)
    {
        ["medic.css"] = ("JellyfinMedic.medic.css", "text/css; charset=utf-8"),
    };

    [HttpGet("{name}")]
    public ActionResult GetAsset(string name)
    {
        if (!Files.TryGetValue(name, out var file)
            || typeof(MedicAssetsController).Assembly.GetManifestResourceStream(file.Resource) is not { } stream)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        // Checked again on every load, so a new version shows straight after an update instead of a cached copy.
        Response.Headers.CacheControl = "no-cache";
        return File(stream, file.ContentType);
    }
}
