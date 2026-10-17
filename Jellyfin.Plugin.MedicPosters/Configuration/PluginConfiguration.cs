using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MedicPosters.Configuration;

/// <summary>Medic Posters' settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Libraries to make posters (and covers) for. Empty: every film, TV, home video and music video library.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Libraries left alone, such as IPTV ones; new libraries are included until they're added here.</summary>
    public string[] ExcludedLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Poster width in pixels; the height is 1.5 times this. Jellyfin sends GIF posters at full size, so smaller loads faster.</summary>
    public int Width { get; set; } = 240;

    /// <summary>How many moments of the film each poster cuts between.</summary>
    public int Moments { get; set; } = 3;

    /// <summary>How long each moment plays, in tenths of a second.</summary>
    public int MomentTenths { get; set; } = 10;

    /// <summary>Frames per second. Fewer frames make a smaller file.</summary>
    public int FramesPerSecond { get; set; } = 8;

    /// <summary>Colours in the GIF's palette (16 to 256). Fewer colours make a smaller file.</summary>
    public int Colours { get; set; } = 96;

    /// <summary>The film's title on the poster.</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>The rounded white frame.</summary>
    public bool ShowFrame { get; set; } = true;

    /// <summary>The title font, by its key in PosterFonts; Bodoni Moda before 1.3.</summary>
    public string Font { get; set; } = "BodoniModa";

    /// <summary>Title size as a percentage of the size that fits the title (50 to 200).</summary>
    public int TextSize { get; set; } = 100;

    /// <summary>Where the title sits: "top", "middle" or "bottom".</summary>
    public string TextPosition { get; set; } = "bottom";

    /// <summary>A soft shadow under the title's letters.</summary>
    public bool TextShadow { get; set; } = true;

    /// <summary>The picture darkened behind the title so it reads on bright scenes.</summary>
    public bool DarkBand { get; set; } = true;

    /// <summary>Fade through black from one moment to the next.</summary>
    public bool FadeBetween { get; set; }

    /// <summary>Fade out at the end (and in at the start), so the loop starts again from black.</summary>
    public bool FadeEnd { get; set; }

    /// <summary>How long each fade takes, in tenths of a second.</summary>
    public int FadeTenths { get; set; } = 3;

    /// <summary>Also give each library an animated cover, made from moments of what's in it, with the library's name.</summary>
    public bool MakeLibraryCovers { get; set; } = true;

    /// <summary>Library cover width in pixels; covers are 16:9, like Jellyfin's library cards.</summary>
    public int CoverWidth { get; set; } = 480;

    /// <summary>How many moments each library cover cuts between; covers have their own settings from 1.2.0.</summary>
    public int CoverMoments { get; set; } = 3;

    /// <summary>How long each moment of a cover plays, in tenths of a second.</summary>
    public int CoverMomentTenths { get; set; } = 10;

    /// <summary>Frames per second for covers.</summary>
    public int CoverFramesPerSecond { get; set; } = 8;

    /// <summary>Colours in a cover's palette (16 to 256).</summary>
    public int CoverColours { get; set; } = 96;

    /// <summary>The library's name on its cover.</summary>
    public bool CoverShowTitle { get; set; } = true;

    /// <summary>The rounded white frame on covers.</summary>
    public bool CoverShowFrame { get; set; } = true;

    public string CoverFont { get; set; } = "BodoniModa";

    public int CoverTextSize { get; set; } = 100;

    public string CoverTextPosition { get; set; } = "bottom";

    public bool CoverTextShadow { get; set; } = true;

    public bool CoverDarkBand { get; set; } = true;

    public bool CoverFadeBetween { get; set; }

    public bool CoverFadeEnd { get; set; }

    public int CoverFadeTenths { get; set; } = 3;

    /// <summary>
    /// Library covers and posters with their own text or their own moments, such as an IPTV library's cover
    /// cut from the films library. One entry per library or title.
    /// </summary>
    public PosterCustom[] Custom { get; set; } = Array.Empty<PosterCustom>();

    /// <summary>The custom text and moments for a library or title, if it has any.</summary>
    public PosterCustom? CustomFor(Guid itemId) =>
        Array.Find(Custom ?? Array.Empty<PosterCustom>(), c => Guid.TryParse(c.ItemId, out var id) && id == itemId);
}

/// <summary>A library's cover or a title's poster with its own text, and the moments to cut it from.</summary>
public class PosterCustom
{
    /// <summary>The library or title.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>The text to show instead of its name; empty for its name.</summary>
    public string? Title { get; set; }

    /// <summary>For a library cover: the library to take random moments from instead of its own, such as Movies for an IPTV library.</summary>
    public string? SourceLibraryId { get; set; }

    /// <summary>The moments to use, in order. Empty: random ones.</summary>
    public PosterCustomMoment[] Moments { get; set; } = Array.Empty<PosterCustomMoment>();
}

/// <summary>One chosen moment: a film, episode or video (or a series, for a random episode), and where to start.</summary>
public class PosterCustomMoment
{
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Seconds into the video; below 0 for a random moment.</summary>
    public double Start { get; set; } = -1;
}
