using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.MedicPicks.Configuration;
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

    public static Plugin? Instance { get; private set; }

    public override string Name => "Medic Picks";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Personal picks from each user's viewing habits: a private playlist in every Jellyfin app, "
        + "plus a My picks page with titles that aren't on the server yet.";

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
