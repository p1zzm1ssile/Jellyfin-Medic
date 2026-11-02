using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MedicPicks.Configuration;
using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MedicPicks;

/// <summary>
/// Medic Picks: personal suggestions built from each user's viewing habits.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin GUID. Must match the guid in manifest.json and the release workflow.</summary>
    public const string PluginGuid = "b34ec5d1-b70f-435f-b77d-9b76979b03c7";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Raised when saved settings are about to point the Seerr address at another server, before they take effect.
    /// </summary>
    public event EventHandler? SeerrServerChanging;

    public static Plugin? Instance { get; private set; }

    public override string Name => "Medic Picks";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Personal picks from each user's viewing habits: a private playlist in every Jellyfin app, "
        + "plus a My picks page with titles that aren't on the server yet.";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration next)
        {
            next.JellyseerrUrl = WithScheme(next.JellyseerrUrl);
            next.SeerrLinkUrl = WithScheme(next.SeerrLinkUrl);

            // Raised before the new address is in use, so no request can pair it with the old server's key. The old
            // address gets http:// too, or adding it to one saved without it would look like another server.
            if (SeerrKeyGuard.HostChanged(WithScheme(Configuration.JellyseerrUrl), next.JellyseerrUrl))
            {
                SeerrServerChanging?.Invoke(this, EventArgs.Empty);
            }
        }

        base.UpdateConfiguration(configuration);
    }

    // "seerr:5055" reads as an address with the scheme "seerr", which no call to Seerr can use; the generic settings
    // endpoint can't explain a refusal, so http:// is added instead. Anything that still isn't an address is left as typed.
    private static string WithScheme(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Contains("://", StringComparison.Ordinal))
        {
            return address;
        }

        string withScheme = "http://" + address.Trim();
        return Uri.TryCreate(withScheme, UriKind.Absolute, out _) ? withScheme : address;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
