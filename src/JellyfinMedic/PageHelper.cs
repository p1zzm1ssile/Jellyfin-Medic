using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyfinMedic.Services;

/// <summary>Puts the middleware below at the front of Jellyfin's request pipeline.</summary>
public sealed class PageHelperStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<PageHelperMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Adds Medic's small helper scripts to the web client's page as it's served. assist.js: when you follow
/// a Medic link to a Jellyfin settings page, the issue you're fixing is shown in a box you can move (it
/// does nothing unless you've just clicked a Medic link in this browser). banner.js: on the home page,
/// admins see a banner for serious problems and for a restart that's waiting. The file on disk is never changed.
/// </summary>
public sealed class PageHelperMiddleware
{
    private static string? _tag;

    private readonly RequestDelegate _next;
    private readonly ILogger<PageHelperMiddleware> _logger;

    public PageHelperMiddleware(RequestDelegate next, ILogger<PageHelperMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        bool isWebPage = HttpMethods.IsGet(context.Request.Method)
            && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
        string? tag = isWebPage ? ScriptTag() : null;
        if (tag is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask for the plain, uncached page so it can be added to.
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
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType ?? string.Empty).Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string html = Encoding.UTF8.GetString(output);
                int end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (end > 0 && !html.Contains("jellyfin-medic-assist", StringComparison.Ordinal))
                {
                    output = Encoding.UTF8.GetBytes(html.Insert(end, tag));
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    context.Response.Headers["Cache-Control"] = "no-cache";
                }
            }
            catch (Exception ex)
            {
                // Never break the web client: serve the page untouched.
                _logger.LogWarning(ex, "Jellyfin Medic: couldn't add the page helper to the web client");
                output = buffer.ToArray();
            }
        }

        context.Response.ContentLength = output.Length;
        await original.WriteAsync(output).ConfigureAwait(false);
    }

    private static string? ScriptTag()
    {
        if (_tag is not null)
        {
            return _tag;
        }

        try
        {
            var scripts = new StringBuilder();
            foreach (string name in new[] { "JellyfinMedic.assist.js", "JellyfinMedic.banner.js" })
            {
                using var stream = typeof(PageHelperMiddleware).Assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    return null;
                }

                using var reader = new StreamReader(stream);
                scripts.Append(reader.ReadToEnd()).Append('\n');
            }

            _tag = "<script id=\"jellyfin-medic-assist-script\">" + scripts + "</script>";
            return _tag;
        }
        catch
        {
            return null;
        }
    }
}
