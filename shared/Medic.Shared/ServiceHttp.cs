using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Medic.Shared;

/// <summary>
/// What calls to Seerr, Sonarr and Radarr have in common. Medic Picks and Medic Profiles each compile this file in.
/// </summary>
internal static class ServiceHttp
{
    /// <summary>
    /// The named client for calls that carry an API key. It doesn't follow redirects: .NET drops Authorization
    /// on a redirect but resends X-Api-Key, and a 307 or 308 resends the body too, so the key would reach
    /// whatever address the saved one points at.
    /// </summary>
    public const string KeyClient = "Medic.KeyClient";

    /// <summary>
    /// Registers <see cref="KeyClient"/>, with the User-Agent and Accept headers of Jellyfin's default client, which
    /// these calls went through before. Medic Picks and Medic Profiles each call this; registering it twice is harmless.
    /// </summary>
    public static void AddKeyClient(IServiceCollection services, IServerApplicationHost host) =>
        services.AddHttpClient(KeyClient, client =>
            {
                // Set, not added to: with both plugins installed this runs twice on each client.
                var headers = client.DefaultRequestHeaders;
                headers.UserAgent.Clear();
                headers.Accept.Clear();
                headers.UserAgent.Add(new ProductInfoHeaderValue(host.Name.Replace(' ', '-'), host.ApplicationVersionString));
                headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json, 1.0));
                headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9));
                headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
            })
            .ConfigurePrimaryHttpMessageHandler(() => NoRedirects());

    /// <summary>A handler as Jellyfin's default one, but that hands redirects back instead of following them.</summary>
    public static HttpClientHandler NoRedirects() => new() { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };

    /// <summary>
    /// Where an answer sends the request on to, or null when it doesn't. That's a 3xx with a Location, made absolute
    /// against the address asked, or an answer from another address than the one asked (a client that followed it).
    /// </summary>
    public static Uri? RedirectTarget(HttpResponseMessage response, Uri asked)
    {
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400 && status != 304 && response.Headers.Location is { } location)
        {
            return location.IsAbsoluteUri ? location : new Uri(asked, location);
        }

        Uri? reached = response.RequestMessage?.RequestUri;
        return reached is not null && reached != asked ? reached : null;
    }

    /// <summary>
    /// Sends a request that gives up after <paramref name="limit"/>. Running out of time fails like a service that
    /// can't be reached (<see cref="HttpRequestException"/>), so a service that hangs can't hold a page or a task;
    /// a cancelled caller still comes through as cancelled. An answer that sends the request on elsewhere throws
    /// <see cref="RedirectedException"/>: the request isn't sent again, so its key stays with the saved address.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan limit, string service, CancellationToken ct)
    {
        // Following a redirect changes the request in place, so keep what was asked.
        Uri asked = request.RequestUri!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{service} didn't answer within {limit.TotalSeconds:0} seconds", ex);
        }

        if (RedirectTarget(response, asked) is { } target)
        {
            response.Dispose();
            throw new RedirectedException(service, target);
        }

        return response;
    }

    /// <summary>
    /// The service's own words in an error answer: <c>{"message": "..."}</c>, or the first of a list of validation
    /// errors (<c>[{"errorMessage": "..."}]</c>) as Sonarr and Radarr send. Null for an answer of any other shape.
    /// </summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Array
                ? root.GetArrayLength() > 0 ? Json.Text(root[0], "errorMessage") : null
                : Json.Text(root, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The saved address sent a call on to <see cref="Target"/>, which wasn't followed.</summary>
internal sealed class RedirectedException : HttpRequestException
{
    public RedirectedException(string service, Uri target)
        : base($"{service}'s address sent the request on to {target}, which wasn't followed")
    {
        Target = target;
    }

    public Uri Target { get; }
}
