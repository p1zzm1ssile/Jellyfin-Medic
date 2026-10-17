using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>
/// Jellyfin reads a film's own poster (poster.jpg and the like) from its folder every time the film is refreshed,
/// which would quietly swap the animated poster back out. This watches for that and puts the animated one back.
/// Started by PluginServiceRegistrator.
/// </summary>
public sealed class PosterKeeper : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly PosterService _service;
    private readonly PosterStore _store;
    private readonly ILogger<PosterKeeper> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly CancellationTokenSource _stopping = new();

    public PosterKeeper(ILibraryManager libraryManager, PosterService service, PosterStore store, ILogger<PosterKeeper> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not BaseItem film || !_store.Read(s => s.Posters.ContainsKey(film.Id)) || !_pending.TryAdd(film.Id, 0))
        {
            return;
        }

        // Out of Jellyfin's own update, and a moment later, so the refresh has finished saving the film first.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _stopping.Token).ConfigureAwait(false);
                _pending.TryRemove(film.Id, out _);
                await _service.KeepOursAsync(film.Id, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _pending.TryRemove(film.Id, out _);
                _logger.LogError(ex, "Medic Posters: couldn't put back the animated poster for {Title}", film.Name);
            }
        });
    }
}
