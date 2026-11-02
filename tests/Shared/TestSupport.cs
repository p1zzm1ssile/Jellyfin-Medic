using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;

// Shared by the plugins' test projects, which each compile it in (see their .csproj).
namespace Medic.TestSupport;

/// <summary>A test of Unix file permissions; skipped on Windows, which keeps its own folder permissions.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file permissions only";
        }
    }
}

/// <summary>Jellyfin's folders, all under one temporary folder.</summary>
internal sealed class TestPaths(string root) : IApplicationPaths
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

/// <summary>Reads every settings file as the defaults and writes nothing, so a plugin made in a test needs no files.</summary>
internal sealed class DefaultSettingsSerializer : IXmlSerializer
{
    public object? DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type);

    public void SerializeToStream(object obj, Stream stream)
    {
    }

    public void SerializeToFile(object obj, string file)
    {
    }

    public object? DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type);

    public object? DeserializeFromBytes(Type type, byte[] buffer) => Activator.CreateInstance(type);
}

/// <summary>Jellyfin's server host, for the details its own HTTP clients are named after; nothing else is asked for.</summary>
public class JellyfinHost : DispatchProxy
{
    public static IServerApplicationHost Create() => Create<IServerApplicationHost, JellyfinHost>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_Name" => "Jellyfin Server",
        "get_ApplicationVersionString" => "12.2.0",
        _ => throw new NotSupportedException(targetMethod?.Name)
    };
}

/// <summary>A real HTTP server on 127.0.0.1, to show what reaches an address and what never does.</summary>
internal sealed class LocalServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, (int Status, string? Location, string Body)> _answer;
    private readonly ConcurrentQueue<ReceivedRequest> _received = new();

    public LocalServer(Func<HttpListenerRequest, (int Status, string? Location, string Body)> answer)
    {
        _answer = answer;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)free.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        }

        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = ServeAsync();
    }

    /// <summary>The server's address, without a trailing slash.</summary>
    public string Url { get; }

    /// <summary>Every request it got, recorded before it answered.</summary>
    public IReadOnlyCollection<ReceivedRequest> Received => _received.ToArray();

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var request = context.Request;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                _received.Enqueue(new ReceivedRequest(request.HttpMethod, request.Url!.PathAndQuery, request.Headers["X-Api-Key"], request.UserAgent, request.Headers["Accept"], await reader.ReadToEndAsync()));
            }

            var (status, location, body) = _answer(request);
            context.Response.StatusCode = status;
            if (location is not null)
            {
                context.Response.RedirectLocation = location;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }
}

internal sealed record ReceivedRequest(string Method, string PathAndQuery, string? ApiKey, string? UserAgent, string? Accept, string Body);
