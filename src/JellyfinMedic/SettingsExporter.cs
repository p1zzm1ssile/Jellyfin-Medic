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
    private static readonly Regex JsonSecret = new(@"(?i)(""?[A-Za-z_]*?(?:api_?key|apitoken|token|password|passwd|secret|username|user_?name|login)""?\s*[:=]\s*)""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"^(?=.*[A-Za-z])(?=.*\d)[A-Za-z0-9_\-]{24,}$", RegexOptions.Compiled);
    private static readonly Regex GuidLike = new(@"^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$", RegexOptions.Compiled);

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
                foreach (var element in doc.Descendants().Where(e => !e.HasElements))
                {
                    element.Value = Mask(element.Name.LocalName, element.Value);
                }

                foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()))
                {
                    attribute.Value = Mask(attribute.Name.LocalName, attribute.Value);
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

    public static string Mask(string name, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (SettingsReader.IsSecretName(name))
        {
            return "****";
        }

        string masked = SettingsReader.MaskSecrets(value);
        masked = TelegramToken.Replace(masked, "****");
        masked = JsonSecret.Replace(masked, "$1\"****\"");
        if (LongToken.IsMatch(masked.Trim()) && !GuidLike.IsMatch(masked.Trim()))
        {
            masked = "****";
        }

        return masked;
    }
}
