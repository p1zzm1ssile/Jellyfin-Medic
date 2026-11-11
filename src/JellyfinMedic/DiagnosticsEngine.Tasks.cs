using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>Checks: scheduled tasks and usage.</summary>
public sealed partial class DiagnosticsEngine
{
    // ---------- Scheduled tasks ----------

    private void CheckTasks(UsageSummary usage)
    {
        var workers = _tasks.ScheduledTasks.ToList();

        // By key: the names are translated into the server's language.
        var scan = workers.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshLibrary");
        var fastScan = scan?.Triggers?
            .Where(t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks.HasValue)
            .Select(t => TimeSpan.FromTicks(t.IntervalTicks.GetValueOrDefault()))
            .Where(i => i > TimeSpan.Zero && i < TimeSpan.FromHours(6))
            .OrderBy(i => i)
            .FirstOrDefault();
        if (fastScan is { } interval && interval > TimeSpan.Zero)
        {
            Add(AreaTasks, Sev.Improve, "The full library scan runs very often", $"Every {interval.TotalHours:0.#} hours",
                "Once a night, with Sonarr/Radarr notifications for new downloads",
                "Each full scan walks every folder. On a big library this keeps disks busy and slows the server for much of the day.",
                WhereTasks);
        }

        var optimise = workers.FirstOrDefault(t => t.ScheduledTask.Key == "OptimizeDatabaseTask");
        if (optimise is not null)
        {
            if (optimise.Triggers is null || !optimise.Triggers.Any())
            {
                Add(AreaTasks, Sev.Improve, "Database optimisation never runs", "Manual only", "Weekly, overnight",
                    "Without it the database slowly fragments, and the web UI and searches get slower.", WhereTasks);
            }
            else if (optimise.LastExecutionResult is { } last && last.EndTimeUtc < DateTime.UtcNow.AddDays(-14))
            {
                Add(AreaTasks, Sev.Tip, "Database optimisation hasn't run for a while",
                    $"Last ran {last.EndTimeUtc.ToLocalTime():d MMM}", "Weekly",
                    "It has a schedule but hasn't completed in over two weeks; check its schedule and errors.", WhereTasks);
            }
        }

        var failed = workers
            .Where(t => ScheduleStorage.IsVisible(t) && t.LastExecutionResult?.Status == TaskCompletionStatus.Failed)
            .Take(10)
            .ToList();
        foreach (var task in failed)
        {
            string error = (task.LastExecutionResult?.ErrorMessage ?? string.Empty).Trim().TrimEnd('.');
            string? owner = PluginNameFor(task);
            bool incompatible = error.Contains("Method not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("MissingMethod", StringComparison.OrdinalIgnoreCase)
                || error.Contains("TypeLoad", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Could not load type", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase);

            if (incompatible)
            {
                string who = owner is null ? "The plugin that provides it" : $"The {owner} plugin";
                Add(AreaTasks, Sev.Problem, $"\"{task.Name}\" can't work on this Jellyfin version",
                    "Fails every time it runs",
                    owner is null ? "Update the plugin it belongs to, or remove that plugin until an update is out"
                                  : $"Update {owner}, or remove it until a version for your Jellyfin is out",
                    $"{who} was built for an older Jellyfin and calls something that no longer exists, so this task can never succeed. Error: {Shorten(error)}.",
                    "Dashboard → Plugins");
            }
            else
            {
                Add(AreaTasks, Sev.Improve, $"\"{task.Name}\" failed last time it ran" + (owner is null ? string.Empty : $" ({owner} plugin)"),
                    error.Length == 0 ? "Failed, no error message" : Shorten(error),
                    "Check the full error in Medic's History tab",
                    "One-off failures are often temporary (a file in use, a network drop). If it keeps failing, the error shows the cause.",
                    WhereTasks);
            }
        }

        // Without a viewing pattern yet, fall back to a gentle daytime heuristic.
        if (!usage.Ready)
        {
            CheckDaytimeTasks(workers);
        }

        bool taskGrid = _plugins.Plugins.Any(p => string.Equals(SettingsReader.Text(p, "Name"), "Task Grid", StringComparison.OrdinalIgnoreCase));
        if (taskGrid)
        {
            Add(AreaTasks, Sev.Tip, "Task Grid and Medic can both change schedules", "Both installed",
                "Change schedules in one of them only",
                "A schedule edited in Task Grid is replaced the next time you press Apply recommended in Medic, and the other way round.",
                "Dashboard → Plugins");
        }

        if (!usage.Ready)
        {
            return;
        }

        var clashes = new List<string>();
        foreach (var worker in workers.Where(ScheduleStorage.IsVisible))
        {
            foreach (var trigger in worker.Triggers ?? Array.Empty<TaskTriggerInfo>())
            {
                if (!trigger.TimeOfDayTicks.HasValue)
                {
                    continue;
                }

                var at = TimeSpan.FromTicks(trigger.TimeOfDayTicks.GetValueOrDefault());
                int hour = at.Hours;
                IEnumerable<int> days = trigger.Type == TaskTriggerInfoType.WeeklyTrigger && trigger.DayOfWeek.HasValue
                    ? new[] { ((int)trigger.DayOfWeek.GetValueOrDefault() + 6) % 7 }
                    : Enumerable.Range(0, 7);

                if (days.Any(d => usage.AverageStreams.ElementAtOrDefault(d * 24 + hour) >= 1.0))
                {
                    clashes.Add($"{worker.Name} ({ScheduleStorage.Hhmm(at)})");
                    break;
                }
            }
        }

        if (clashes.Count > 0)
        {
            Add(AreaTasks, Sev.Improve, "Tasks run while people are usually watching", string.Join(", ", clashes),
                $"Move them into your quiet hours ({usage.QuietWindow})",
                "Heavy maintenance during viewing competes for disk and CPU and can cause buffering.",
                "Medic → Schedule → Apply recommended");
        }
    }

    /// <summary>
    /// In "suggest" mode, flags when Medic could schedule tasks noticeably better than they run now
    /// (for example tasks sitting in busy hours). It never changes anything itself; it points you at
    /// Preview → Apply. "Off" mode skips this entirely.
    /// </summary>
    private void SuggestSchedule(UsageSummary usage)
    {
        if ((Plugin.Instance?.Configuration.ScheduleMode ?? "suggest") == "off")
        {
            return;
        }

        List<PlannedTask> plan;
        try
        {
            var busy = BusyProfile.Create(usage, Plugin.Instance?.Configuration, HostLoadAnalyzer.Summarise(HostLoadStore.Load(_paths)));
            plan = SchedulePlanner.Build(_tasks.ScheduledTasks.Where(ScheduleStorage.IsVisible).ToList(),
                ScheduleStorage.LoadProfile(_paths), ScheduleStorage.LoadManaged(_paths), busy, ScheduleStorage.LoadChoices(_paths));
        }
        catch (Exception ex)
        {
            // The schedule suggestion is left out of the report; the reason goes to the log.
            _logger.LogWarning(ex, "Jellyfin Medic: couldn't work out the recommended schedule for the report");
            return;
        }

        var changing = plan.Where(p => p.Changes).ToList();
        // Only raise it when the change is worth making: a task currently in a busy hour, or several moves.
        bool worthwhile = changing.Count >= 3 || changing.Any(p => !string.IsNullOrEmpty(p.Warning));
        if (changing.Count > 0 && worthwhile)
        {
            Add(AreaTasks, Sev.Improve, "A better schedule is available",
                $"{changing.Count} task(s) could move to quieter times",
                "Open Schedule → Preview changes, then Apply if you're happy",
                usage.Ready
                    ? "Based on when people actually watch, Medic can space these tasks into quieter hours so they don't compete with playback. It won't change anything until you Apply."
                    : "Medic is still learning when people watch, but it can already tidy how these tasks are spread. It won't change anything until you Apply.",
                "Medic → Schedule");
        }
    }

    // ---------- Usage ----------

    private void CheckUsage(UsageSummary usage, object? enc, HardwareInfo hw)
    {
        if (!usage.Ready)
        {
            Add(AreaUsage, Sev.Tip, "Still learning when your server is busy",
                $"{usage.HoursCollected:0} hours recorded so far", "Leave it running for at least 3 days (a week is better)",
                "Medic checks who's watching every 5 minutes. Once it has enough, it can spot tasks running at busy times and suggest your best quiet hours.",
                "Nothing to do; it records automatically");
            return;
        }

        if (usage.MaxStreams == 0)
        {
            Add(AreaUsage, Sev.Good, "No playback recorded yet", "0 streams", string.Empty,
                "Nobody has watched anything since recording started, so there's no peak time to plan around.", string.Empty);
            return;
        }

        Add(AreaUsage, Sev.Good, "Your viewing pattern",
            $"Busiest: {usage.BusiestHours} (most on {usage.BusiestDay}); up to {usage.MaxStreams} at once",
            $"Quietest 5 hours: {usage.QuietWindow}",
            "Heavy tasks should run in the quiet hours. The Scheduled tasks section above flags any that don't.",
            "Medic → Schedule (shading shows busy hours)");


        string hwType = (SettingsReader.Text(enc, "HardwareAccelerationType") ?? "none").ToLowerInvariant();
        bool hwOn = hwType is not ("" or "none");
        if (usage.TranscodeShare is { } share && share >= 0.3)
        {
            Add(AreaUsage, hwOn ? Sev.Tip : Sev.Problem, $"{share:P0} of playback is transcoded",
                hwOn ? "Hardware acceleration on" : "CPU only",
                hwOn ? "Check app quality settings and the remote bitrate limit" : "Turn on hardware acceleration (see Hardware & transcoding)",
                hwOn ? "Lots of transcoding usually comes from apps set to a lower quality, subtitles that need burning in, or a low bitrate cap."
                     : $"Transcoding this much on {hw.CpuThreads} CPU threads is the most likely cause of buffering at busy times.",
                WhereTranscoding, id: "usage.transcode-share");
        }
    }
}
