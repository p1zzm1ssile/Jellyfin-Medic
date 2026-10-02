using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using JellyfinMedic.Api;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

/// <summary>
/// Security findings from data already on the server: failed logins, logins from new or far-off
/// places, and accounts that can reach libraries they probably shouldn't. Read-only. IP addresses
/// are shown to the admin on the page but never saved into Medic's files or sent anywhere.
/// </summary>
public static class SecurityAuditor
{
    private const string Area = "Security";
    private const string WhereUsers = "Dashboard → Users";

    public static List<Finding> Audit(object? activityManager, IUserManager userManager, List<object> users)
    {
        var findings = new List<Finding>();
        Guard(() => LoginAttempts(activityManager, findings));
        Guard(() => LibraryAccess(userManager, users, findings));
        return findings;
    }

    // ---------- Failed logins and unusual places ----------

    private static void LoginAttempts(object? activityManager, List<Finding> findings)
    {
        var entries = ActivityEntries(activityManager, DateTime.UtcNow.AddDays(-7));
        if (entries.Count == 0)
        {
            return;
        }

        var failures = new List<(string User, string Ip)>();
        var success = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            string type = (SettingsReader.Text(entry, "Type") ?? string.Empty).ToLowerInvariant();
            string name = SettingsReader.Text(entry, "Name") ?? string.Empty;
            string overview = SettingsReader.Text(entry, "ShortOverview") ?? SettingsReader.Text(entry, "Overview") ?? string.Empty;
            string ip = ExtractIp(overview) ?? ExtractIp(name) ?? string.Empty;
            string user = SettingsReader.Text(entry, "UserId") ?? string.Empty;

            bool failed = type.Contains("authenticationfailed") || type.Contains("failedlogin") ||
                          (type.Contains("login") && (name + overview).Contains("failed", StringComparison.OrdinalIgnoreCase));
            bool loggedIn = !failed && (type.Contains("authenticationsucceeded") || type.Contains("sessionstarted") || type.Contains("userauthenticated"));

            if (failed)
            {
                failures.Add((NameOrUser(name, user), ip));
            }
            else if (loggedIn && ip.Length > 0)
            {
                string key = NameOrUser(name, user);
                (success.TryGetValue(key, out var set) ? set : success[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(ip);
            }
        }

        if (failures.Count >= 10)
        {
            var byIp = failures.Where(f => f.Ip.Length > 0).GroupBy(f => f.Ip).OrderByDescending(g => g.Count()).ToList();
            var worst = byIp.FirstOrDefault();
            string detail = worst is not null
                ? $"{failures.Count} failed sign-ins in the last 7 days; most from {worst.Key} ({worst.Count()})"
                : $"{failures.Count} failed sign-ins in the last 7 days";
            findings.Add(new Finding
            {
                Area = Area,
                Severity = failures.Count >= 50 ? Sev.Problem : Sev.Improve,
                Title = "A lot of failed sign-ins",
                Current = detail,
                Recommended = "If your server is reachable from the internet, put it behind a reverse proxy with fail2ban, or only allow access over a VPN",
                Why = "Many failed sign-ins usually mean someone is guessing passwords. A lockout after a few tries, or not exposing Jellyfin directly, stops this.",
                Where = "Dashboard → Networking, and your reverse proxy"
            });
        }

        var roaming = success.Where(s => s.Value.Count >= 3).ToList();
        if (roaming.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = roaming.Count == 1 ? "An account signs in from several places" : $"{roaming.Count} accounts sign in from several places",
                Current = string.Join("; ", roaming.Select(r => $"{r.Key}: {r.Value.Count} addresses")),
                Recommended = "Check these are all the right person. If not, change the password and review who has it",
                Why = "Signing in from several different addresses in a week can be normal (phone, work, travel), but it can also mean a password is being shared or has been stolen.",
                Where = WhereUsers
            });
        }
    }

    // ---------- Library access ----------

    private static void LibraryAccess(IUserManager userManager, List<object> users, List<Finding> findings)
    {
        var getDto = userManager.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetUserDto" && m.GetParameters().Length >= 1);
        var flagged = new List<string>();

        foreach (var user in users)
        {
            object? dto = TryDto(getDto, userManager, user);
            string name = SettingsReader.Text(dto, "Name") ?? SettingsReader.Text(user, "Username") ?? "Unknown";
            if (SettingsReader.Bool(dto, "Policy.IsAdministrator") ?? false)
            {
                continue; // Admins see everything by design.
            }

            bool allLibraries = SettingsReader.Bool(dto, "Policy.EnableAllFolders") ?? false;
            bool parentalLimited = (SettingsReader.Number(dto, "Policy.MaxParentalRating") ?? 0) > 0 ||
                                   (SettingsReader.List(dto, "Policy.BlockedTags")?.Count ?? 0) > 0;
            if (allLibraries && !parentalLimited)
            {
                flagged.Add(name);
            }
        }

        if (flagged.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = flagged.Count == 1 ? "An account can see every library" : $"{flagged.Count} accounts can see every library",
                Current = string.Join(", ", flagged),
                Recommended = "Give each account only the libraries it should see, and set parental limits for children",
                Why = "These accounts can open every library, including any adult or IPTV ones, and have no content rating limit. That may not be what you want for children's or guests' accounts.",
                Where = WhereUsers + " → (user) → Access and Parental Control"
            });
        }
    }

    // ---------- Reading the activity log ----------

    private static List<object> ActivityEntries(object? activityManager, DateTime sinceUtc)
    {
        if (activityManager is null)
        {
            return new List<object>();
        }

        try
        {
            foreach (var method in activityManager.GetType().GetMethods().Where(m => m.Name is "GetPagedResult" or "GetActivityLogEntries"))
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].ParameterType switch
                    {
                        { } t when t == typeof(int) => i == parameters.Length - 1 ? 500 : 0,
                        { } t when t == typeof(DateTime) => sinceUtc,
                        { } t when t == typeof(DateTime?) => sinceUtc,
                        _ => parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null
                    };
                }

                object? result = method.Invoke(activityManager, args);
                var items = SettingsReader.Get(result, "Items") ?? result;
                if (items is IEnumerable list && items is not string)
                {
                    return list.Cast<object>().Where(e => e is not null).ToList();
                }
            }
        }
        catch
        {
            // Activity log unavailable in this version: skip these checks.
        }

        return new List<object>();
    }

    private static string? ExtractIp(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var token in text.Split(new[] { ' ', '\t', ',', ';', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = token.Trim();
            int slash = candidate.IndexOf('/');
            if (slash > 0)
            {
                candidate = candidate[..slash];
            }

            if (IPAddress.TryParse(candidate, out var ip) && !IPAddress.IsLoopback(ip) && candidate.Any(char.IsDigit))
            {
                return ip.ToString();
            }
        }

        return null;
    }

    private static string NameOrUser(string name, string userId) =>
        !string.IsNullOrWhiteSpace(name) && name.Length < 40 ? name : (userId.Length > 0 ? "a user" : "unknown");

    private static object? TryDto(System.Reflection.MethodInfo? getDto, IUserManager manager, object user)
    {
        if (getDto is null)
        {
            return null;
        }

        try
        {
            var args = new object?[getDto.GetParameters().Length];
            args[0] = user;
            return getDto.Invoke(manager, args);
        }
        catch
        {
            return null;
        }
    }

    private static void Guard(Action check)
    {
        try
        {
            check();
        }
        catch
        {
            // One failing security check never stops the rest of the report.
        }
    }
}
