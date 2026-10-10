using System.Collections;
using System.Globalization;
using JellyfinMedic.Api;
using MediaBrowser.Controller.Library;

namespace JellyfinMedic.Services;

public class UserSummary
{
    public int Total { get; set; }

    public int Disabled { get; set; }

    public int Admins { get; set; }

    public List<string> Inactive { get; set; } = new();

    public List<string> NoPassword { get; set; } = new();

    public List<string> AdminNames { get; set; } = new();
}

/// <summary>
/// Accounts that look forgotten or unprotected. Reads each user through Jellyfin's own user
/// details (the same information the Users page shows) and never changes anything.
/// </summary>
/// <summary>
/// Gets every user account, whichever way this Jellyfin version offers it: a Users list,
/// a GetUsers method, or a list of user IDs to look up one by one.
/// </summary>
public static class UserList
{
    public static List<object> All(IUserManager userManager)
    {
        try
        {
            var types = new[] { typeof(IUserManager) }.Concat(typeof(IUserManager).GetInterfaces()).Append(userManager.GetType()).ToList();

            foreach (var type in types)
            {
                foreach (var name in new[] { "Users", "AllUsers" })
                {
                    if (type.GetProperty(name)?.GetValue(userManager) is IEnumerable list && list is not string)
                    {
                        return list.Cast<object>().Where(u => u is not null).ToList();
                    }
                }
            }

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods().Where(m => m.Name is "GetUsers" or "GetAllUsers" or "GetUsersAsync" && m.GetParameters().All(p => p.IsOptional)))
                {
                    object? result = method.Invoke(userManager, method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
                    if (result is Task task)
                    {
                        task.GetAwaiter().GetResult();
                        result = task.GetType().GetProperty("Result")?.GetValue(task);
                    }

                    if (result is IEnumerable list && result is not string)
                    {
                        return list.Cast<object>().Where(u => u is not null).ToList();
                    }
                }
            }

            foreach (var type in types)
            {
                var ids = type.GetProperty("UsersIds")?.GetValue(userManager) as IEnumerable;
                var byId = type.GetMethods().FirstOrDefault(m => m.Name == "GetUserById" && m.GetParameters().Length == 1);
                if (ids is not null && byId is not null)
                {
                    return ids.Cast<object>()
                        .Select(id => byId.Invoke(userManager, new[] { id }))
                        .Where(u => u is not null)
                        .Cast<object>()
                        .ToList();
                }
            }
        }
        catch
        {
            // Users unavailable: the checks that need them are skipped.
        }

        return new List<object>();
    }
}

public static class UserAuditor
{
    private const string Area = "Users and access";
    private const string Where = "Dashboard → Users";

    public static (UserSummary Summary, List<Finding> Findings) Audit(IUserManager userManager, int inactiveDays)
    {
        var summary = new UserSummary();
        var findings = new List<Finding>();

        var users = UserList.All(userManager);
        if (users.Count == 0)
        {
            return (summary, findings);
        }

        var getDto = userManager.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetUserDto" && m.GetParameters().Length >= 1);

        var noPasswordRemote = new List<string>();
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(7, inactiveDays));

        foreach (var user in users)
        {
            object? dto = null;
            try
            {
                if (getDto is not null)
                {
                    var args = new object?[getDto.GetParameters().Length];
                    args[0] = user;
                    dto = getDto.Invoke(userManager, args);
                }
            }
            catch
            {
                // Fall back to the raw user record below.
            }

            string name = SettingsReader.Text(dto, "Name") ?? SettingsReader.Text(user, "Username") ?? "Unknown";
            bool disabled = SettingsReader.Bool(dto, "Policy.IsDisabled") ?? false;
            bool admin = SettingsReader.Bool(dto, "Policy.IsAdministrator") ?? false;
            bool hasPassword = SettingsReader.Bool(dto, "HasPassword") ?? SettingsReader.Bool(dto, "HasConfiguredPassword") ?? true;
            bool remote = SettingsReader.Bool(dto, "Policy.EnableRemoteAccess") ?? true;
            DateTime? lastActive = AsDate(SettingsReader.Get(dto, "LastActivityDate")) ?? AsDate(SettingsReader.Get(user, "LastActivityDate"));

            summary.Total++;
            if (disabled)
            {
                summary.Disabled++;
                continue;
            }

            if (admin)
            {
                summary.Admins++;
                summary.AdminNames.Add(name);
            }

            if (!hasPassword)
            {
                summary.NoPassword.Add(name);
                if (remote)
                {
                    noPasswordRemote.Add(name);
                }
            }

            if (lastActive is null)
            {
                summary.Inactive.Add($"{name} (never signed in)");
            }
            else if (lastActive.Value.ToUniversalTime() < cutoff)
            {
                summary.Inactive.Add($"{name} (last seen {lastActive.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)})");
            }
        }

        if (summary.NoPassword.Count > 0)
        {
            bool risky = noPasswordRemote.Count > 0;
            findings.Add(new Finding
            {
                Area = Area,
                Severity = risky ? Sev.Problem : Sev.Improve,
                Title = summary.NoPassword.Count == 1 ? "An account has no password" : $"{summary.NoPassword.Count} accounts have no password",
                Current = string.Join(", ", summary.NoPassword),
                Recommended = "Set a password, or disable the account if nobody uses it",
                Why = risky
                    ? "These accounts are allowed to connect from outside your home, so anyone who can reach your server can sign in as them."
                    : "Anyone on your home network can sign in as them, including visitors' devices.",
                Where = Where
            });
        }

        if (summary.Inactive.Count > 0)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = summary.Inactive.Count == 1 ? $"An account hasn't been used in over {inactiveDays} days" : $"{summary.Inactive.Count} accounts haven't been used in over {inactiveDays} days",
                Current = string.Join(", ", summary.Inactive),
                Recommended = "Disable accounts nobody uses",
                Why = "Unused accounts are easy to forget about, and their old passwords still work. Disabling keeps their watch history in case they come back.",
                Where = Where
            });
        }

        if (summary.Admins > 2)
        {
            findings.Add(new Finding
            {
                Area = Area,
                Severity = Sev.Tip,
                Title = $"{summary.Admins} accounts are administrators",
                Current = string.Join(", ", summary.AdminNames),
                Recommended = "Keep admin rights to the people who manage the server",
                Why = "Administrators can change every setting and see every library. Everyday watching is safer from a normal account.",
                Where = Where
            });
        }

        return (summary, findings);
    }

    private static DateTime? AsDate(object? value) => value switch
    {
        DateTime d when d > DateTime.MinValue.AddYears(1) => d,
        DateTimeOffset o => o.UtcDateTime,
        _ => null
    };
}
