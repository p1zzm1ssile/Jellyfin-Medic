using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MedicPosters.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MedicPosters;

/// <summary>
/// Medic Posters: animated posters for your films, cut from moments of each film, with its title
/// in a big serif font and a rounded white frame.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin GUID. Must match the guid in manifest.json and the release workflow.</summary>
    public const string PluginGuid = "f8a6666f-5278-45ae-8e5d-abcec99de310";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Medic Posters";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Animated posters for your films and series, and animated covers for your libraries, made from moments of your videos with the title on top. "
        + "Your own pictures are kept and can be put back at any time.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MedicPosters",
                DisplayName = "Medic Posters",
                EmbeddedResourcePath = GetType().Namespace + ".Web.posters.html",

                // Adds "Medic Posters" to the Plugins section of the dashboard sidebar.
                EnableInMainMenu = true,
                MenuIcon = "movie"
            }
        };
    }
}
