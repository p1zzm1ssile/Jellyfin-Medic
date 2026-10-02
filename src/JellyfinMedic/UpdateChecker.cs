using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Updates;

namespace JellyfinMedic.Services;

/// <summary>
/// Plugin updates waiting in your repositories, using Jellyfin's own update check.
/// Results are kept for an hour, so the report doesn't contact repositories every time.
/// </summary>
public static class UpdateChecker
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime At, List<string> Updates)? _cache;

    public static async Task<List<string>> AvailableAsync(IInstallationManager installs, CancellationToken ct)
    {
        if (_cache is { } hit && DateTime.UtcNow - hit.At < TimeSpan.FromHours(1))
        {
            return hit.Updates;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.At < TimeSpan.FromHours(1))
            {
                return again.Updates;
            }

            var updates = new List<string>();
            var method = installs.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetAvailablePluginUpdates" && m.GetParameters().Length <= 1);
            if (method is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                object?[] args = method.GetParameters().Length == 1 ? new object?[] { timeout.Token } : Array.Empty<object?>();
                if (method.Invoke(installs, args) is Task task)
                {
                    await task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    var result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result is IEnumerable items)
                    {
                        foreach (var item in items)
                        {
                            string name = SettingsReader.Text(item, "Name") ?? "A plugin";
                            string version = SettingsReader.Text(item, "Version") ?? string.Empty;
                            updates.Add(string.IsNullOrEmpty(version) ? name : $"{name} {version}");
                        }
                    }
                }
            }

            updates = updates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
            _cache = (DateTime.UtcNow, updates);
            return updates;
        }
        catch
        {
            return _cache?.Updates ?? new List<string>();
        }
        finally
        {
            Gate.Release();
        }
    }
}
