using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MedicPicks.Picks;

/// <summary>
/// Removes the saved Seerr key when the settings point the Seerr address at another server. A saved key is only
/// ever sent to the server it was entered for, so a changed address can't be used to collect it (Medic Profiles
/// does the same with its keys).
/// </summary>
public sealed class SeerrKeyGuard : IHostedService
{
    private readonly PicksStore _store;
    private readonly ILogger<SeerrKeyGuard> _logger;
    private Plugin? _plugin;

    public SeerrKeyGuard(PicksStore store, ILogger<SeerrKeyGuard> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether the address moved to another scheme, host or port. A first address (none saved before) isn't a
    /// change; removing the address is, or the next one saved would be taken for a first address.
    /// </summary>
    public static bool HostChanged(string? before, string? after) =>
        !string.IsNullOrWhiteSpace(before) && !string.Equals(Authority(before), Authority(after), StringComparison.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plugin = Plugin.Instance;
        if (_plugin is not null)
        {
            _plugin.SeerrServerChanging += OnSeerrServerChanging;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_plugin is not null)
        {
            _plugin.SeerrServerChanging -= OnSeerrServerChanging;
        }

        return Task.CompletedTask;
    }

    private static string Authority(string? url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}:{u.Port}" : trimmed;
    }

    private void OnSeerrServerChanging(object? sender, EventArgs e)
    {
        if (_store.GetSeerrKey() is null)
        {
            return;
        }

        _store.SetSeerrKey(null);
        _logger.LogInformation("Medic Picks: the Seerr address now points at another server, so the saved Seerr key was removed; enter it again for the new server");
    }
}
