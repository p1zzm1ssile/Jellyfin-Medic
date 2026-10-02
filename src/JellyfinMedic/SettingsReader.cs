using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using JellyfinMedic.Api;

namespace JellyfinMedic.Services;

/// <summary>
/// Reads settings by name rather than through compiled property references. If a setting
/// doesn't exist in your Jellyfin version, the check that uses it is simply skipped,
/// instead of the plugin failing to build or load.
/// </summary>
public static class SettingsReader
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    private static readonly string[] SecretWords =
    {
        "password", "passwd", "secret", "token", "apikey", "api_key", "accesskey",
        "privatekey", "credential", "username", "login"
    };

    private static readonly Regex QuerySecrets =
        new(@"(?i)((?:password|passwd|pass|pwd|username|user|token|apikey|api_key|key)=)[^&\s]+", RegexOptions.Compiled);

    private static readonly Regex UrlUserInfo =
        new(@"://[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled);

    // Telegram bot tokens inside addresses, e.g. https://api.telegram.org/bot123456:ABC.../sendMessage
    private static readonly Regex BotToken =
        new(@"bot\d{6,}:[A-Za-z0-9_-]{20,}", RegexOptions.Compiled);

    /// <summary>Follows a dotted path such as "TrickplayOptions.EnableHwAcceleration".</summary>
    public static bool TryGet(object? root, string path, out object? value)
    {
        value = root;
        if (root is null)
        {
            return false;
        }

        foreach (var part in path.Split('.'))
        {
            if (value is null)
            {
                return false;
            }

            PropertyInfo? prop;
            try
            {
                prop = value.GetType().GetProperty(part, Flags);
            }
            catch (AmbiguousMatchException)
            {
                prop = value.GetType().GetProperties(Flags)
                    .FirstOrDefault(p => string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase));
            }

            if (prop is null || prop.GetIndexParameters().Length > 0)
            {
                value = null;
                return false;
            }

            try
            {
                value = prop.GetValue(value);
            }
            catch
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    public static object? Get(object? root, string path) => TryGet(root, path, out var value) ? value : null;

    public static bool? Bool(object? root, string path) => Get(root, path) switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var parsed) => parsed,
        _ => null
    };

    public static long? Number(object? root, string path) => ToNumber(Get(root, path));

    public static string? Text(object? root, string path) => ToText(Get(root, path));

    /// <summary>A list setting, or null if the setting doesn't exist.</summary>
    public static List<string>? List(object? root, string path)
    {
        if (!TryGet(root, path, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return new List<string>();
        }

        if (value is IEnumerable items && value is not string)
        {
            return items.Cast<object?>().Where(i => i is not null).Select(i => ToText(i) ?? string.Empty).ToList();
        }

        return new List<string> { ToText(value) ?? string.Empty };
    }

    public static long? ToNumber(object? value)
    {
        switch (value)
        {
            case null:
            case bool:
            case Enum:
                return null;
            case string s:
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            case IConvertible convertible:
                try
                {
                    return Convert.ToInt64(convertible, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }

            default:
                return null;
        }
    }

    public static string? ToText(object? value) => value switch
    {
        null => null,
        string s => s,
        Enum e => e.ToString(),
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(i => i?.ToString() ?? string.Empty)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    // ---------- Listing every setting ----------

    /// <summary>Turns a settings object into a flat list of name/value rows, with secrets hidden.</summary>
    public static List<SettingRow> Flatten(object? root, string section)
    {
        var rows = new List<SettingRow>();
        if (root is not null)
        {
            Walk(root, string.Empty, section, rows, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        return rows;
    }

    private static void Walk(object obj, string prefix, string section, List<SettingRow> rows, int depth, HashSet<object> seen)
    {
        if (depth > 3 || rows.Count > 600 || !seen.Add(obj))
        {
            return;
        }

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }

            string name = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;

            if (IsComplex(value))
            {
                Walk(value!, name, section, rows, depth + 1, seen);
                continue;
            }

            if (value is IEnumerable items && value is not string)
            {
                var list = items.Cast<object?>().ToList();
                if (list.Any(IsComplex))
                {
                    for (int i = 0; i < Math.Min(list.Count, 15); i++)
                    {
                        if (IsComplex(list[i]))
                        {
                            Walk(list[i]!, $"{name}[{i}]", section, rows, depth + 1, seen);
                        }
                    }

                    continue;
                }
            }

            rows.Add(new SettingRow { Section = section, Name = name, Value = Display(name, value) });
        }
    }

    private static bool IsComplex(object? value)
    {
        if (value is null || value is string || value is IEnumerable)
        {
            return false;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is Guid || value is TimeSpan)
        {
            return false;
        }

        string ns = type.Namespace ?? string.Empty;
        return ns.StartsWith("MediaBrowser", StringComparison.Ordinal) || ns.StartsWith("Jellyfin", StringComparison.Ordinal);
    }

    // ---------- Display and privacy ----------

    public static string Display(string name, object? value)
    {
        if (value is null)
        {
            return "(not set)";
        }

        if (value is bool b)
        {
            return b ? "On" : "Off";
        }

        string text = ToText(value) ?? string.Empty;
        if (text.Length == 0)
        {
            return "(empty)";
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "On";
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return "Off";
        }

        if (IsSecretName(name))
        {
            return "•••• (hidden)";
        }

        return MaskSecrets(text);
    }

    public static bool IsSecretName(string name)
    {
        string leaf = LeafName(name).ToLowerInvariant();
        return leaf is "user" or "pass" or "pwd" or "key" || SecretWords.Any(word => leaf.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>Hides credentials inside URLs, e.g. an IPTV playlist link with ?username=...&amp;password=...</summary>
    public static string MaskSecrets(string text)
    {
        string masked = QuerySecrets.Replace(text, "$1••••");
        masked = BotToken.Replace(masked, "bot••••");
        return UrlUserInfo.Replace(masked, "://••••:••••@");
    }

    /// <summary>"TunerHosts[0].Url" becomes "Url".</summary>
    public static string LeafName(string name)
    {
        string leaf = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        int bracket = leaf.IndexOf('[');
        return bracket >= 0 ? leaf[..bracket] : leaf;
    }
}
