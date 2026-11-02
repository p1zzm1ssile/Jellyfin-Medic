using System.Collections;
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

    // Signing in from this many places in a week is worth a look: each address is a place (an IPv6 one by its /64
    // network), and the whole home network is one.
    private const int RoamingAddresses = 3;

    // The Report and the Dashboard read the activity log through this one reader, so they can share a reading.
    private static readonly ActivityLogReader SharedReader = new(TimeProvider.System);

    /// <summary>
    /// The Report's security findings. The activity log is read afresh, as "Run checks" asks for, unless a reading
    /// is already under way: that one started moments ago.
    /// </summary>
    public static async Task<List<Finding>> AuditAsync(object? activityManager, IUserManager userManager, List<object> users, CancellationToken cancellationToken)
    {
        var log = await SharedReader.ReadAsync(activityManager, reuse: false, cancellationToken).ConfigureAwait(false);
        var findings = new List<Finding>();
        Guard(() => LoginAttempts(log, UserNames(users), findings));
        Guard(() => LibraryAccess(userManager, users, findings));
        return findings;
    }

    // ---------- Failed logins and unusual places ----------

    /// <summary>Each account's name by its id, written both with and without dashes as the activity log may use either.</summary>
    private static Dictionary<string, string> UserNames(List<object> users)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            string? name = SettingsReader.Text(user, "Username") ?? SettingsReader.Text(user, "Name");
            if (name is not null && Guid.TryParse(SettingsReader.Text(user, "Id"), out var id))
            {
                names[id.ToString("N")] = name;
                names[id.ToString("D")] = name;
            }
        }

        return names;
    }

    private static void LoginAttempts(ActivityReading log, Dictionary<string, string> userNames, List<Finding> findings)
    {
        if (log.Entries is null)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = "Sign-ins couldn't be checked this time",
                Current = log.Reason,
                Recommended = "Open Medic again later; if this keeps happening, the database may be busy or need optimising, or Medic may need an update for this version of Jellyfin",
                Why = "Failed sign-ins and sign-ins from new places are found in the activity log. Without it, this check can't say whether there were any.",
                Where = "Dashboard → Activity"
            });
            return;
        }

        if (log.Entries.Count == 0)
        {
            return;
        }

        var (failures, networks) = SignIns(log.Entries, userNames);
        int foundBefore = findings.Count;

        // The oldest part of a week past the page limit went unread, so there may be more than these.
        string newest = $"the newest {PageSize * MaxPages:N0} entries of the activity log";
        string partly = log.Capped ? $"; only {newest} were read" : string.Empty;

        if (failures.Count >= 10)
        {
            var byIp = failures.Where(f => f.Ip.Length > 0).GroupBy(f => f.Ip).OrderByDescending(g => g.Count()).ToList();
            var worst = byIp.FirstOrDefault();
            string detail = worst is not null
                ? $"{failures.Count} failed sign-ins in the last 7 days; most from {worst.Key} ({worst.Count()})"
                : $"{failures.Count} failed sign-ins in the last 7 days";

            // Most failures from a private address usually means a reverse proxy Jellyfin doesn't know about,
            // so every failure looks like it came from the proxy and the real address is lost.
            bool fromProxy = worst is not null && IPAddress.TryParse(worst.Key, out var source) && IsPrivate(source);
            findings.Add(new Finding
            {
                Area = Area,
                Severity = failures.Count >= 50 ? Sev.Problem : Sev.Improve,
                Title = "A lot of failed sign-ins",
                Current = detail + partly,
                Recommended = (fromProxy
                        ? $"If {worst!.Key} is your reverse proxy, add it under Known proxies so Jellyfin sees the real addresses. "
                        : string.Empty)
                    + "If your server is reachable from the internet, put it behind a reverse proxy with fail2ban, or only allow access over a VPN. Jellyfin can also lock an account after a few wrong passwords (each user's \"Maximum failed login attempts\")",
                Why = "Many failed sign-ins usually mean someone is guessing passwords. A lockout after a few tries, or not exposing Jellyfin directly, stops this.",
                Where = fromProxy ? "Dashboard → Networking → Known proxies, and Dashboard → Users" : "Dashboard → Networking, and your reverse proxy"
            });
        }

        var roaming = networks.Where(s => s.Value.Count >= RoamingAddresses).ToList();
        if (roaming.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = roaming.Count == 1 ? "An account signs in from several places" : $"{roaming.Count} accounts sign in from several places",
                Id = "security.roaming-accounts",
                Current = string.Join("; ", roaming.Select(r => $"{r.Key}: {r.Value.Count} addresses")) + partly,
                Recommended = "Check these are all the right person. If not, change the password and review who has it",
                Why = "Signing in from several different addresses in a week can be normal (phone, work, travel), but it can also mean a password is being shared or has been stolen.",
                Where = WhereUsers
            });
        }

        // With no sign-in finding to carry that note, a week cut short would pass for one checked in full.
        if (log.Capped && findings.Count == foundBefore)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = "Sign-ins were only partly checked",
                Current = $"Only {newest} were read, so older sign-ins in the week weren't checked",
                Recommended = "Look through the rest of the week in Dashboard → Activity for failed sign-ins and sign-ins from new places",
                Why = "Failed sign-ins and sign-ins from new places are found in the activity log. This week's holds more entries than Medic reads, so any in its oldest part weren't counted.",
                Where = "Dashboard → Activity"
            });
        }
    }

    /// <summary>
    /// From activity log entries: each failed sign-in (account and address), and the networks each account
    /// signed in from.
    /// </summary>
    internal static (List<(string User, string Ip)> Failures, Dictionary<string, HashSet<string>> Networks) SignIns(List<object> entries, Dictionary<string, string> userNames)
    {
        var failures = new List<(string User, string Ip)>();
        var networks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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
                // By account: the entry's text includes the device and is translated, and long ones used to all
                // become "a user", adding up different people's addresses. Phones on IPv6 change address often,
                // so an IPv6 address counts by its /64 network.
                string key = user.Length > 0 ? (userNames.TryGetValue(user, out var known) ? known : "an account (" + user[..Math.Min(8, user.Length)] + ")") : NameOrUser(name, user);
                (networks.TryGetValue(key, out var set) ? set : networks[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(Network(ip));
            }
        }

        return (failures, networks);
    }

    /// <summary>
    /// Accounts that signed in from 3 or more places in the last 7 days, with how many, most first; on a week too busy
    /// to read in full, from its newest entries only. Never the addresses. Null when the activity log couldn't be read.
    /// For the Dashboard, a good reading under a minute old will do, such as the one the Report just made.
    /// </summary>
    internal static async Task<List<RoamingAccount>?> RoamingAccountsAsync(object? activityManager, List<object> users, CancellationToken cancellationToken)
    {
        var entries = (await SharedReader.ReadAsync(activityManager, reuse: true, cancellationToken).ConfigureAwait(false)).Entries;
        if (entries is null)
        {
            return null;
        }

        var (_, networks) = SignIns(entries, UserNames(users));
        return networks.Where(n => n.Value.Count >= RoamingAddresses)
            .OrderByDescending(n => n.Value.Count)
            .ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
            .Select(n => new RoamingAccount { Name = n.Key, Addresses = n.Value.Count })
            .ToList();
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
                Id = "security.all-libraries",
                Current = string.Join(", ", flagged),
                Recommended = "Give each account only the libraries it should see, and set parental limits for children",
                Why = "These accounts can open every library, including any adult or IPTV ones, and have no content rating limit. That may not be what you want for children's or guests' accounts.",
                Where = WhereUsers + " → (user) → Access and Parental Control"
            });
        }
    }

    // ---------- Finding the activity log ----------

    // The activity manager's interface, under its name in this Jellyfin version. Looked up across the loaded
    // assemblies once: controllers are created for every request, the Dashboard's frequent polls included.
    private static readonly Lazy<Type[]> ActivityManagerTypes = new(() =>
        new[] { "MediaBrowser.Model.Activity.IActivityManager", "Jellyfin.Data.IActivityManager" }
            .Select(name => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t is not null))
            .OfType<Type>()
            .ToArray());

    /// <summary>Jellyfin's activity manager, or null when this version has none Medic can find.</summary>
    internal static object? ResolveActivityManager(IServiceProvider services)
    {
        foreach (var type in ActivityManagerTypes.Value)
        {
            try
            {
                var service = services.GetService(type);
                if (service is not null)
                {
                    return service;
                }
            }
            catch
            {
                // Not registered in this version; try the next name.
            }
        }

        return null;
    }

    // ---------- Reading the activity log ----------

    // The log is read newest first, PageSize entries at a time. Reading stops after MaxPages pages so that a very busy
    // week can't hold the report up; the findings then say only the newest entries were read.
    private const int PageSize = 500;
    private const int MaxPages = 20;

    // For all the pages together. A log too slow to answer is reported as such, rather than as a week with no
    // failed sign-ins.
    private static readonly TimeSpan ReadingTime = TimeSpan.FromSeconds(15);

    private const string NotAvailable = "Jellyfin's activity log isn't available to Medic";

    /// <summary>
    /// The activity log as read: its entries, newest first, and whether reading stopped at <see cref="MaxPages"/> full
    /// pages, so older ones may be left. Entries is null when the log couldn't be read, and Reason then says why.
    /// </summary>
    internal sealed record ActivityReading(List<object>? Entries, bool Capped, string Reason)
    {
        public static ActivityReading Failed(string reason) => new(null, false, reason);
    }

    /// <summary>
    /// Reads the last 7 days of the activity log without holding a thread while Jellyfin answers, and lets callers
    /// share a reading: one under way is shared by everyone, and a good one is reused for a minute by callers that
    /// don't need it fresh. A failed one is shared only by the callers already waiting for it.
    /// </summary>
    internal sealed class ActivityLogReader
    {
        // Long enough for the Dashboard to use the reading of the Report it was opened with.
        private static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(60);

        private readonly TimeProvider _clock;
        private readonly object _gate = new();
        private Shared? _last;

        internal ActivityLogReader(TimeProvider clock) => _clock = clock;

        /// <summary>
        /// The last 7 days of the log. A reading under way is always shared; with <paramref name="reuse"/>, so is a
        /// good one under a minute old.
        /// </summary>
        internal Task<ActivityReading> ReadAsync(object? activityManager, bool reuse, CancellationToken cancellationToken)
        {
            Lazy<Task<ActivityReading>> reading;
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                if (_last is null || !ReferenceEquals(_last.Manager, activityManager) || !Usable(_last, now, reuse))
                {
                    // Started by the first caller to ask for its value, outside this lock, as it calls into Jellyfin.
                    _last = new Shared(activityManager, now, new Lazy<Task<ActivityReading>>(() => ReadWeekAsync(activityManager, now.UtcDateTime.AddDays(-7))));
                }

                reading = _last.Reading;
            }

            // A caller that gives up stops waiting, but the reading goes on for the others sharing it.
            return reading.Value.WaitAsync(cancellationToken);
        }

        private static bool Usable(Shared last, DateTimeOffset now, bool reuse)
        {
            // Under way: it started moments ago, so it's fresh enough for anyone.
            if (!last.Reading.IsValueCreated || !last.Reading.Value.IsCompleted)
            {
                return true;
            }

            // "Open Medic again later" has to mean a new reading, so a failed one is never kept.
            var done = last.Reading.Value;
            return reuse && now - last.At < KeepFor && done.IsCompletedSuccessfully && done.Result.Entries is not null;
        }

        private async Task<ActivityReading> ReadWeekAsync(object? activityManager, DateTime sinceUtc)
        {
            try
            {
                // Jellyfin 10.9 and later: GetPagedResultAsync(ActivityLogQuery), newest first.
                var getPage = activityManager?.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetPagedResultAsync" && m.GetParameters().Length == 1);
                if (getPage is null)
                {
                    return ActivityReading.Failed(NotAvailable);
                }

                var entries = new List<object>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                long started = _clock.GetTimestamp();
                for (int page = 0; page < MaxPages; page++)
                {
                    object? query = Activator.CreateInstance(getPage.GetParameters()[0].ParameterType);
                    SetIfPresent(query, "MinDate", sinceUtc);
                    SetIfPresent(query, "Skip", page * PageSize);
                    SetIfPresent(query, "Limit", PageSize);
                    if (getPage.Invoke(activityManager, new[] { query }) is not Task task)
                    {
                        return ActivityReading.Failed(NotAvailable);
                    }

                    TimeSpan left = ReadingTime - _clock.GetElapsedTime(started);
                    try
                    {
                        await task.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, _clock).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Jellyfin's call takes no token, so this page is left to finish on its own, and nothing waits for it.
                        return ActivityReading.Failed($"The activity log didn't answer within {ReadingTime.TotalSeconds:0} seconds");
                    }

                    object? result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (SettingsReader.Get(result, "Items") is not IEnumerable items || items is string)
                    {
                        return ActivityReading.Failed(NotAvailable);
                    }

                    int onPage = 0;
                    foreach (var entry in items)
                    {
                        onPage++;

                        // Entries logged while the pages are read push the older ones down, so one can come on two pages.
                        string? id = SettingsReader.Text(entry, "Id");
                        if (entry is not null && (id is null || ids.Add(id)))
                        {
                            entries.Add(entry);
                        }
                    }

                    if (onPage < PageSize)
                    {
                        return new ActivityReading(entries, false, string.Empty);
                    }
                }

                return new ActivityReading(entries, true, string.Empty);
            }
            catch (Exception ex)
            {
                return ActivityReading.Failed($"Reading the activity log failed ({ex.GetBaseException().GetType().Name})");
            }
        }

        private sealed record Shared(object? Manager, DateTimeOffset At, Lazy<Task<ActivityReading>> Reading);
    }

    private static void SetIfPresent(object? target, string name, object value)
    {
        var property = target?.GetType().GetProperty(name);
        if (property is { CanWrite: true })
        {
            property.SetValue(target, value);
        }
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

    // The place an address counts as. Each device at home has its own address, so every address only seen from inside
    // the home network is one place: the private ones, and 100.64.0.0/10. An IPv6 address counts by its /64 network
    // (one home or phone connection); any other IPv4 address as it is.
    private static string Network(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return ip;
        }

        if (IsPrivate(address) || IsSharedAddressSpace(address))
        {
            return "home network";
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6)
        {
            return ip;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    // 100.64.0.0/10: Tailscale gives each device an address here, and carrier-grade NAT uses it inside an internet
    // provider's network. Neither is reachable from the internet.
    private static bool IsSharedAddressSpace(IPAddress ip)
    {
        var b = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && (b[1] & 0xc0) == 0x40;
    }

    // Addresses only seen inside a home or a Docker network: 10/8, 172.16/12, 192.168/16, IPv6 unique local and link-local.
    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
            : (b[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal;
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
