using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MedicPosters.Posters;

/// <summary>A title font that travels inside the plugin. Family is the name libass finds it by.</summary>
public sealed record PosterFont(string Key, string File, string Family, string Label);

/// <summary>The fonts the page offers, all under the SIL Open Font License (their licences sit beside them in Fonts).</summary>
public static class PosterFonts
{
    public static readonly IReadOnlyList<PosterFont> All = new[]
    {
        new PosterFont("BodoniModa", "BodoniModa-Bold.ttf", "Bodoni Moda", "Bodoni Moda"),
        new PosterFont("PlayfairDisplay", "PlayfairDisplay-ExtraBold.ttf", "Playfair Display ExtraBold", "Playfair Display"),
        new PosterFont("Cinzel", "Cinzel-Bold.ttf", "Cinzel", "Cinzel"),
        new PosterFont("CormorantGaramond", "CormorantGaramond-Bold.ttf", "Cormorant Garamond", "Cormorant Garamond"),
        new PosterFont("AbrilFatface", "AbrilFatface-Regular.ttf", "Abril Fatface", "Abril Fatface"),
        new PosterFont("BebasNeue", "BebasNeue-Regular.ttf", "Bebas Neue", "Bebas Neue"),
        new PosterFont("Anton", "Anton-Regular.ttf", "Anton", "Anton"),
        new PosterFont("Oswald", "Oswald-Bold.ttf", "Oswald", "Oswald"),
        new PosterFont("Montserrat", "Montserrat-ExtraBold.ttf", "Montserrat ExtraBold", "Montserrat"),
        new PosterFont("Lobster", "Lobster-Regular.ttf", "Lobster", "Lobster")
    };

    public static PosterFont Default => All[0];

    /// <summary>The font with this key, or Bodoni Moda (the only font before 1.3) for an unknown or missing one.</summary>
    public static PosterFont Find(string? key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static bool Known(string? key) => All.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}
