using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Web;

/// <summary>
/// Puts the middleware below at the front of Jellyfin's request pipeline.
/// </summary>
public sealed class MenuLinkStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<MenuLinkMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds a "My picks" link to the web client's menu for every user, with no files to edit.
/// The web client reads its menu links from /web/config.json; this adds Medic Picks' entry to that
/// file as it's served. The file on disk is never changed, so Jellyfin updates can't undo it.
/// Works in browsers, Jellyfin Desktop and the Android and iOS apps, which all run the server's web client.
/// With "Open inside Jellyfin" on, it also adds Web/inapp.js to the web client's own page (index.html), which opens
/// that link as a page inside the app; the web client draws every menu link to open in a new tab or window.
/// </summary>
public sealed class MenuLinkMiddleware
{
    private const string ConfigPath = "/web/config.json";
    private const string ScriptId = "medic-picks-inapp";

    private readonly RequestDelegate _next;
    private readonly ILogger<MenuLinkMiddleware> _logger;

    public MenuLinkMiddleware(RequestDelegate next, ILogger<MenuLinkMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        var config = Plugin.Instance?.Configuration;
        bool isConfig = path.EndsWith(ConfigPath, StringComparison.OrdinalIgnoreCase);
        bool isWebPage = path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
        if (config is null
            || !config.AddMenuLink
            || !HttpMethods.IsGet(context.Request.Method)
            || !(isConfig || (isWebPage && config.OpenInApp)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for the plain, uncached file so it can be edited.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        byte[] output = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK && output.Length > 0)
        {
            try
            {
                if (isConfig)
                {
                    // Keep any base URL (e.g. /jellyfin) so the link points at this server.
                    string basePath = path[..^ConfigPath.Length];
                    output = AddLink(output, config.MenuLinkName, basePath + "/MedicPicks/Page");
                }
                else if ((context.Response.ContentType ?? string.Empty).Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    // Relative to the page, which is in /web/, so a base URL (e.g. /jellyfin) is kept here too.
                    output = AddScript(output, "../MedicPicks/InApp.js");
                }

                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers["Cache-Control"] = "no-cache";
            }
            catch (Exception ex)
            {
                // Never break the web client: serve the original file if anything goes wrong.
                _logger.LogWarning(ex, "Medic Picks: couldn't add {What} to the web client", isConfig ? "the menu link" : "the in-app picks page");
                output = buffer.ToArray();
            }
        }

        context.Response.ContentLength = output.Length;
        await original.WriteAsync(output).ConfigureAwait(false);
    }

    /// <summary>Adds the script tag before the page's closing body tag, once.</summary>
    internal static byte[] AddScript(byte[] html, string src)
    {
        string text = Encoding.UTF8.GetString(html);
        int end = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end < 0 || text.Contains(ScriptId, StringComparison.Ordinal))
        {
            return html;
        }

        return Encoding.UTF8.GetBytes(text.Insert(end, "<script id=\"" + ScriptId + "\" src=\"" + src + "\" defer></script>"));
    }

    internal static byte[] AddLink(byte[] json, string? name, string url)
    {
        string text = Encoding.UTF8.GetString(json).TrimStart('\uFEFF');
        if (JsonNode.Parse(text) is not JsonObject root)
        {
            throw new InvalidDataException("config.json isn't a JSON object");
        }

        if (root["menuLinks"] is not JsonArray links)
        {
            links = new JsonArray();
            root["menuLinks"] = links;
        }

        bool present = links.OfType<JsonObject>()
            .Any(l => string.Equals(l["url"]?.GetValue<string>(), url, StringComparison.OrdinalIgnoreCase));
        if (!present)
        {
            links.Add(new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(name) ? "My picks" : name.Trim(),
                ["icon"] = "recommend",
                ["url"] = url
            });
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
