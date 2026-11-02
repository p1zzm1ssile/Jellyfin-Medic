using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;

namespace JellyfinMedic.Services;

/// <summary>
/// Builds a text copy of every plugin's settings for asking for help, with passwords, keys,
/// tokens, usernames and credentials inside links replaced by ****. Nothing is sent anywhere:
/// the page shows it first and the admin chooses whether to download it.
/// </summary>
public static class SettingsExporter
{
    private static readonly Regex TelegramToken = new(@"\d{6,}:[A-Za-z0-9_-]{30,}", RegexOptions.Compiled);

    // Scripts (JavaScript Injector's, say) quote values with ' and ` as well as ", and a masked value keeps its quotes.
    // A value without quotes is code there, such as token: getToken(), so it's left alone.
    private static readonly Regex QuotedSecret = new(@"(?i)([""'`]?[A-Za-z_]*?(?:api[_-]?key|apitoken|token|password|passwd|secret|username|user_?name|login)[""'`]?\s*[:=]\s*)([""'`])(?:(?!\2)[\s\S])*\2", RegexOptions.Compiled);

    private static readonly Regex LongToken = new(@"^(?=.*[A-Za-z])(?=.*\d)[A-Za-z0-9_\-]{24,}$", RegexOptions.Compiled);

    // Ids written as dashed GUIDs are kept. 32 hex characters without dashes is also how Jellyfin, Sonarr and
    // Radarr API keys look, so those go, even though some plugins store harmless ids that way too.
    private static readonly Regex GuidLike = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    // Words in a name/value pair's label that mean its value is a secret (header names such as Authorization,
    // X-Api-Key or Cookie). Labels are names, not values, so matching parts of words is fine here.
    private static readonly string[] SecretLabelWords =
    {
        "auth", "token", "key", "cookie", "secret", "pass", "session", "signature", "credential", "bearer"
    };

    public static string Build(IPluginManager plugins, IApplicationPaths paths, string jellyfinVersion, IReadOnlyList<string>? systemSummary = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Jellyfin Medic: plugin settings for support");
        text.AppendLine($"Created {DateTime.Now:d MMM yyyy HH:mm}. Jellyfin {jellyfinVersion}.");
        text.AppendLine();
        text.AppendLine("What's in this file: a list of your installed plugins, each plugin's settings, and");
        text.AppendLine("(only if you ticked the box) a short summary of your hardware and libraries.");
        text.AppendLine("Passwords, API keys, tokens, usernames and credentials inside web addresses are");
        text.AppendLine("replaced with ****. No serial numbers are included. This masking covers the common");
        text.AppendLine("cases but can't be guaranteed to catch everything, so it is your responsibility to");
        text.AppendLine("read this file before you send it. If any personal data does reach the maintainer, it");
        text.AppendLine("is not wanted, is removed, and is not used, as a matter of integrity.");
        text.AppendLine("Nothing is sent automatically; emailing this file is your choice.");
        text.AppendLine();

        if (systemSummary is { Count: > 0 })
        {
            text.AppendLine("===== System summary (you ticked to include this)");
            foreach (var line in systemSummary)
            {
                text.AppendLine("  " + line);
            }

            text.AppendLine();
        }

        text.AppendLine("Installed plugins:");
        foreach (var plugin in plugins.Plugins.Cast<object>().OrderBy(p => SettingsReader.Text(p, "Name"), StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine($"  {SettingsReader.Text(plugin, "Name")} {SettingsReader.Text(plugin, "Version")} ({SettingsReader.Text(plugin, "Manifest.Status")})");
        }

        foreach (var file in Directory.Exists(paths.PluginConfigurationsPath)
                     ? Directory.GetFiles(paths.PluginConfigurationsPath, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                     : Enumerable.Empty<string>())
        {
            text.AppendLine();
            text.AppendLine("===== " + Path.GetFileName(file));
            try
            {
                var doc = XDocument.Load(file);

                // Decide on the original text first: a pair's value is judged by its label, so labels mustn't change before.
                var leaves = doc.Descendants().Where(e => !e.HasElements).ToList();
                var secretValues = leaves.Where(IsSecretPairValue).ToHashSet();
                var labels = leaves.Where(IsPairLabel).ToHashSet();
                foreach (var element in leaves)
                {
                    element.Value = secretValues.Contains(element) ? "****"
                        : labels.Contains(element) ? MaskValue(element.Value)
                        : Mask(element.Name.LocalName, element.Value);
                }

                // The same pairs written as attributes: <Header name="Cookie" value="sid=..." />.
                var attributes = doc.Descendants().SelectMany(e => e.Attributes()).ToList();
                var secretAttributes = attributes.Where(IsSecretPairAttribute).ToHashSet();
                var labelAttributes = attributes.Where(IsPairLabelAttribute).ToHashSet();
                foreach (var attribute in attributes)
                {
                    attribute.Value = secretAttributes.Contains(attribute) ? "****"
                        : labelAttributes.Contains(attribute) ? MaskValue(attribute.Value)
                        : Mask(attribute.Name.LocalName, attribute.Value);
                }

                text.AppendLine(doc.ToString());
            }
            catch (Exception ex)
            {
                text.AppendLine($"(Couldn't read this file: {ex.Message})");
            }
        }

        return text.ToString();
    }

    /// <summary>Masks a setting's value, writing <paramref name="mask"/> in place of what's hidden.</summary>
    public static string Mask(string name, string value, string mask = "****")
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return SettingsReader.IsSecretName(name) ? mask : MaskValue(value, mask);
    }

    /// <summary>Masks what looks like a credential inside a value, whatever the setting is called.</summary>
    private static string MaskValue(string value, string mask = "****")
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string masked = SettingsReader.MaskSecrets(value);
        masked = TelegramToken.Replace(masked, mask);
        masked = QuotedSecret.Replace(masked, m => m.Groups[1].Value + m.Groups[2].Value + mask + m.Groups[2].Value);
        if (LongToken.IsMatch(masked.Trim()) && !GuidLike.IsMatch(masked.Trim()))
        {
            masked = mask;
        }

        return masked;
    }

    /// <summary>
    /// The Value of a name/value pair (custom HTTP headers, extra fields) whose label looks secret. There the
    /// secret sits under a harmless element name: &lt;Key&gt;Authorization&lt;/Key&gt;&lt;Value&gt;Bearer ...&lt;/Value&gt;.
    /// </summary>
    internal static bool IsSecretPairValue(XElement element) =>
        IsNamed(element.Name, "Value")
        && element.Parent?.Elements().Any(e => IsLabelElement(e) && LooksSecretLabel(e.Value)) == true;

    /// <summary>The Key or Name beside a Value in such a pair: a label, shown as it is unless the text itself looks like a credential.</summary>
    private static bool IsPairLabel(XElement element) =>
        IsLabelElement(element) && element.Parent?.Elements().Any(e => !e.HasElements && IsNamed(e.Name, "Value")) == true;

    private static bool IsSecretPairAttribute(XAttribute attribute) =>
        IsNamed(attribute.Name, "Value")
        && attribute.Parent?.Attributes().Any(a => IsLabelName(a.Name) && LooksSecretLabel(a.Value)) == true;

    private static bool IsPairLabelAttribute(XAttribute attribute) =>
        IsLabelName(attribute.Name) && attribute.Parent?.Attributes().Any(a => IsNamed(a.Name, "Value")) == true;

    private static bool IsLabelElement(XElement element) => !element.HasElements && IsLabelName(element.Name);

    private static bool IsLabelName(XName name) => IsNamed(name, "Key") || IsNamed(name, "Name");

    private static bool LooksSecretLabel(string label) =>
        SecretLabelWords.Any(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsNamed(XName name, string expected) =>
        string.Equals(name.LocalName, expected, StringComparison.OrdinalIgnoreCase);
}
