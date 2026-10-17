using Jellyfin.Plugin.MedicPosters.Configuration;
using Jellyfin.Plugin.MedicPosters.Posters;

namespace MedicPosters.Tests;

public class PosterMakerTests
{
    private static readonly PosterSpec Spec = PosterSpec.From(new PluginConfiguration());

    [Fact]
    public void Moments_are_spread_between_the_opening_and_the_credits()
    {
        var starts = PosterMaker.MomentStarts(6000, 3, 1);
        Assert.Equal(new[] { 900.0, 2700, 4500 }, starts);
    }

    [Fact]
    public void Short_films_still_fit_every_moment_and_never_play_past_the_end()
    {
        Assert.Equal(new[] { 0.6, 1.8, 3 }, PosterMaker.MomentStarts(4, 3, 1));
        Assert.Equal(new[] { 0.0, 1, 2 }, PosterMaker.MomentStarts(3, 3, 1));
        Assert.Equal(new[] { 0.0, 1, 1 }, PosterMaker.MomentStarts(2, 3, 1));
    }

    [Fact]
    public void A_film_of_unknown_length_plays_from_the_start()
    {
        Assert.Equal(new[] { 0.0, 1.5 }, PosterMaker.MomentStarts(null, 2, 1.5));
    }

    [Fact]
    public void Titles_cannot_inject_drawing_commands()
    {
        Assert.Equal("ALIEN (/P1) /N", PosterMaker.AssText("Alien {\\p1} \\N"));
        Assert.Equal("LINE BREAK", PosterMaker.AssText("Line\n  Break\t"));
    }

    [Fact]
    public void Long_titles_get_a_smaller_font()
    {
        Assert.True(PosterMaker.TitleSize(240, 4) > PosterMaker.TitleSize(240, 25));
        Assert.True(PosterMaker.TitleSize(240, 25) > PosterMaker.TitleSize(240, 80));
    }

    [Fact]
    public void The_drawing_has_the_title_frame_and_shade_only_when_asked_for()
    {
        var full = PosterMaker.BuildAss(Spec, "Big Buck Bunny");
        Assert.Contains("PlayResX: 240", full);
        Assert.Contains("PlayResY: 360", full);
        Assert.Contains(",BIG BUCK BUNNY", full);
        Assert.Contains("Style: Title,Bodoni Moda,", full);
        Assert.Equal(4, full.Split("Dialogue:").Length);

        var bare = PosterMaker.BuildAss(Spec with { ShowTitle = false, ShowFrame = false }, "Big Buck Bunny");
        Assert.DoesNotContain("Dialogue:", bare);
    }

    [Fact]
    public void FFmpeg_seeks_once_per_moment_and_writes_a_looping_gif()
    {
        var args = PosterMaker.BuildArgs("/films/a b.mkv", new[] { 10.0, 20.5 }, Spec, hdr: false, "/tmp/out.gif");
        Assert.Equal(2, args.Count(a => a == "-ss"));
        Assert.Equal(new[] { "-ss", "20.5", "-t", "1", "-i", "/films/a b.mkv" }, args.Skip(args.ToList().LastIndexOf("-ss")).Take(6));
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("concat=n=2:v=1:a=0", graph);
        Assert.Contains("ass=filename=poster.ass:fontsdir=fonts", graph);
        Assert.Contains("palettegen=max_colors=96", graph);
        Assert.DoesNotContain("zscale", graph);
        Assert.Equal("/tmp/out.gif", args[^1]);
        Assert.Contains("gif", args);

        var hdr = PosterMaker.BuildArgs("/films/a.mkv", new[] { 10.0 }, Spec, hdr: true, "/tmp/out.gif");
        Assert.Contains("tonemap", hdr[hdr.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void Settings_out_of_range_are_kept_to_sensible_values()
    {
        var spec = PosterSpec.From(new PluginConfiguration { Width = 5001, Moments = 0, MomentTenths = 1, FramesPerSecond = 100, Colours = 2 });
        Assert.Equal((600, 900, 1, 0.5, 15, 16), (spec.Width, spec.Height, spec.Moments, spec.MomentSeconds, spec.FramesPerSecond, spec.Colours));
    }

    [Fact]
    public void Library_covers_are_16_by_9_and_kept_to_sensible_sizes()
    {
        var cover = PosterSpec.CoverFrom(new PluginConfiguration());
        Assert.Equal((480, 270), (cover.Width, cover.Height));
        Assert.Equal((960, 540), (PosterSpec.CoverFrom(new PluginConfiguration { CoverWidth = 4000 }) is var big ? (big.Width, big.Height) : default));
        Assert.Equal(0, PosterSpec.CoverFrom(new PluginConfiguration { CoverWidth = 401 }).Height % 2);
    }

    [Fact]
    public void A_cover_title_is_sized_by_its_height_so_it_fits()
    {
        var cover = PosterSpec.CoverFrom(new PluginConfiguration());
        var ass = PosterMaker.BuildAss(cover, "Movies");
        Assert.Contains("PlayResX: 480\nPlayResY: 270", ass);
        Assert.Contains("Style: Title,Bodoni Moda," + PosterMaker.TitleSize(270, "MOVIES".Length) + ",", ass);
    }

    [Fact]
    public void Moments_from_several_videos_each_get_their_own_input_and_only_hdr_ones_are_tone_mapped()
    {
        var clips = new[] { new Clip("/tv/a.mkv", 100, false), new Clip("/tv/b.mkv", 200.5, true), new Clip("/tv/c.mkv", 0, false) };
        var args = PosterMaker.BuildArgs(clips, Spec, "/tmp/out.gif").ToList();
        Assert.Equal(new[] { "/tv/a.mkv", "/tv/b.mkv", "/tv/c.mkv" }, args.Select((a, i) => (a, i)).Where(x => x.i > 0 && args[x.i - 1] == "-i").Select(x => x.a));
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("concat=n=3:v=1:a=0", graph);
        Assert.DoesNotContain("[0:v:0]zscale", graph);
        Assert.Contains("[1:v:0]zscale", graph);
        Assert.DoesNotContain("[2:v:0]zscale", graph);
    }

    [Fact]
    public void A_random_moment_is_past_the_opening_and_before_the_credits()
    {
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            double at = PosterMaker.RandomStart(1000, 1, random);
            Assert.InRange(at, 150, 750);
        }

        Assert.Equal(0, PosterMaker.RandomStart(null, 1, random));
        Assert.Equal(0, PosterMaker.RandomStart(2, 1, random));
    }

    [Fact]
    public void Library_covers_follow_their_own_settings()
    {
        var c = new PluginConfiguration { Moments = 5, FramesPerSecond = 12, ShowTitle = true, CoverMoments = 2, CoverFramesPerSecond = 6, CoverColours = 48, CoverShowTitle = false, CoverShowFrame = false };
        var cover = PosterSpec.CoverFrom(c);
        Assert.Equal((2, 6, 48, false, false), (cover.Moments, cover.FramesPerSecond, cover.Colours, cover.ShowTitle, cover.ShowFrame));
        var poster = PosterSpec.From(c);
        Assert.Equal((5, 12, true), (poster.Moments, poster.FramesPerSecond, poster.ShowTitle));
    }

    [Fact]
    public void The_size_estimate_scales_with_pixels_and_frames()
    {
        Assert.Equal(24, Spec.Frames);
        Assert.Equal(240 * 360 * 24 / 20, Spec.EstimateBytes(0.05));
        var longer = Spec with { Moments = 6 };
        Assert.Equal(2 * Spec.EstimateBytes(0.05), longer.EstimateBytes(0.05));
    }

    [Fact]
    public void Every_font_is_inside_the_plugin_with_its_licence_beside_it()
    {
        var names = typeof(PosterMaker).Assembly.GetManifestResourceNames();
        foreach (var font in PosterFonts.All)
        {
            Assert.Contains("Jellyfin.Plugin.MedicPosters.Fonts." + font.File, names);
        }

        Assert.Equal(PosterFonts.All.Count, PosterFonts.All.Select(f => f.Key).Distinct().Count());
        Assert.Equal("BodoniModa", PosterFonts.Find(null).Key);
        Assert.Equal("BodoniModa", PosterFonts.Find("Comic Sans").Key);
        Assert.Equal("Anton", PosterFonts.Find("anton").Key);
    }

    [Fact]
    public void The_title_takes_the_chosen_font_size_and_position()
    {
        var c = new PluginConfiguration { Font = "Lobster", TextSize = 150, TextPosition = "top" };
        var ass = PosterMaker.BuildAss(PosterSpec.From(c), "Up");
        int size = (int)Math.Round(PosterMaker.TitleSize(240, 2) * 1.5);
        // Alignment 8 is top centre.
        Assert.Contains($"Style: Title,Lobster,{size},", ass);
        Assert.Matches(@"Style: Title,[^\n]*,8,\d+,\d+,\d+,1\n", ass);
        Assert.Matches(@"Style: Title,[^\n]*,5,\d+,\d+,\d+,1\n", PosterMaker.BuildAss(PosterSpec.From(new PluginConfiguration { TextPosition = "middle" }), "Up"));
        Assert.Matches(@"Style: Title,[^\n]*,2,\d+,\d+,\d+,1\n", PosterMaker.BuildAss(PosterSpec.From(new PluginConfiguration { TextPosition = "sideways" }), "Up"));
    }

    [Fact]
    public void The_shadow_and_the_dark_band_can_each_be_left_off()
    {
        var plain = PosterMaker.BuildAss(PosterSpec.From(new PluginConfiguration { TextShadow = false, DarkBand = false, ShowFrame = false }), "Up");
        Assert.Matches(@"Style: Title,[^\n]*,100,1,0,1,0,0,2,", plain);
        Assert.Single(plain.Split("Dialogue:").Skip(1));

        var shaded = PosterMaker.BuildAss(PosterSpec.From(new PluginConfiguration { ShowFrame = false }), "Up");
        Assert.Equal(2, shaded.Split("Dialogue:").Length - 1);
        Assert.Matches(@"Style: Title,[^\n]*,100,1,0,1,0,1.5,2,", shaded);
    }

    [Fact]
    public void Fades_go_between_moments_and_out_at_the_end_only_when_asked_for()
    {
        var none = PosterSpec.From(new PluginConfiguration());
        Assert.Equal(string.Empty, PosterMaker.Fades(none, 0, 3));

        var between = PosterSpec.From(new PluginConfiguration { FadeBetween = true, FadeTenths = 3 });
        Assert.Equal(",fade=t=out:st=0.7:d=0.3", PosterMaker.Fades(between, 0, 3));
        Assert.Equal(",fade=t=in:st=0:d=0.3,fade=t=out:st=0.7:d=0.3", PosterMaker.Fades(between, 1, 3));
        Assert.Equal(",fade=t=in:st=0:d=0.3", PosterMaker.Fades(between, 2, 3));

        var end = PosterSpec.From(new PluginConfiguration { FadeEnd = true, FadeTenths = 5 });
        Assert.Equal(",fade=t=in:st=0:d=0.5", PosterMaker.Fades(end, 0, 3));
        Assert.Equal(string.Empty, PosterMaker.Fades(end, 1, 3));
        Assert.Equal(",fade=t=out:st=0.5:d=0.5", PosterMaker.Fades(end, 2, 3));

        // A fade never takes more than half of a short moment.
        var shortMoments = PosterSpec.From(new PluginConfiguration { FadeEnd = true, FadeTenths = 10, MomentTenths = 5 });
        Assert.Equal(0.25, shortMoments.FadeSeconds);

        var graph = PosterMaker.BuildArgs("/films/a.mkv", new[] { 10.0, 20 }, between, hdr: false, "/tmp/out.gif");
        Assert.Contains("format=yuv420p,fade=t=out:st=0.7:d=0.3[m0]", graph[graph.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void Covers_and_posters_have_their_own_text_style()
    {
        var c = new PluginConfiguration { Font = "Anton", TextSize = 80, CoverFont = "Cinzel", CoverTextSize = 200, CoverTextPosition = "middle", CoverFadeBetween = true };
        var cover = PosterSpec.CoverFrom(c);
        var poster = PosterSpec.From(c);
        Assert.Equal(("Cinzel", 2.0, "middle", true), (cover.Font.Key, cover.TextScale, cover.TextPosition, cover.FadeBetween));
        Assert.Equal(("Anton", 0.8, "bottom", false), (poster.Font.Key, poster.TextScale, poster.TextPosition, poster.FadeBetween));
        Assert.Equal(0.5, PosterSpec.From(new PluginConfiguration { TextSize = 5 }).TextScale);
    }

    [Fact]
    public void A_still_frame_is_one_jpeg_at_the_chosen_moment()
    {
        var args = PosterMaker.FrameArgs(new Clip("/films/a.mkv", 61.5, false), 320, "frame.jpg");
        Assert.Equal(new[] { "-ss", "61.5", "-i", "/films/a.mkv" }, args.SkipWhile(a => a != "-ss").Take(4));
        Assert.Contains("scale=320:-2", args);
        Assert.Equal("frame.jpg", args[^1]);
        Assert.Contains("tonemap", string.Join(' ', PosterMaker.FrameArgs(new Clip("/films/a.mkv", 0, true), 320, "frame.jpg")));
    }

    [Fact]
    public void A_picked_moment_stays_inside_the_video()
    {
        Assert.Equal(100, PosterService.ChosenStart(100, 6000, 1));
        Assert.Equal(5999, PosterService.ChosenStart(9000, 6000, 1));
        Assert.Equal(42, PosterService.ChosenStart(42, null, 1));
        Assert.InRange(PosterService.ChosenStart(-1, 1000, 1), 150, 750);
    }

    [Fact]
    public void Custom_text_and_moments_are_tidied_when_saved()
    {
        var movies = Guid.NewGuid();
        var iptv = Guid.NewGuid();
        var film = Guid.NewGuid();
        var sent = new[]
        {
            new PosterCustom { ItemId = iptv.ToString(), Title = "  Live TV  ", SourceLibraryId = movies.ToString("N") },
            new PosterCustom { ItemId = film.ToString(), Title = new string('x', 120), Moments = Enumerable.Range(0, 9).Select(i => new PosterCustomMoment { ItemId = film.ToString(), Start = i * 10 }).ToArray() },
            new PosterCustom { ItemId = movies.ToString(), SourceLibraryId = movies.ToString() },
            new PosterCustom { ItemId = "not a guid", Title = "x" },
            new PosterCustom { ItemId = Guid.NewGuid().ToString(), SourceLibraryId = Guid.NewGuid().ToString() },
            new PosterCustom { ItemId = film.ToString("N"), Title = "Kept last", Moments = new[] { new PosterCustomMoment { ItemId = film.ToString(), Start = double.NaN } } }
        };
        var tidy = Jellyfin.Plugin.MedicPosters.Api.PostersController.Custom(sent, new HashSet<Guid> { movies, iptv });

        Assert.Equal(2, tidy.Length);
        var cover = tidy.Single(c => c.ItemId == iptv.ToString("N"));
        Assert.Equal(("Live TV", movies.ToString("N")), (cover.Title, cover.SourceLibraryId));
        // The last entry for the same title wins, and a moment with no time is a random one.
        var poster = tidy.Single(c => c.ItemId == film.ToString("N"));
        Assert.Equal("Kept last", poster.Title);
        Assert.Equal(-1, Assert.Single(poster.Moments).Start);

        var many = Jellyfin.Plugin.MedicPosters.Api.PostersController.Custom(new[] { sent[1] }, new HashSet<Guid>()).Single();
        Assert.Equal(80, many.Title!.Length);
        Assert.Equal(PosterService.MaxMoments, many.Moments.Length);
    }

    [Fact]
    public void A_library_or_title_shows_its_own_text_when_it_has_some()
    {
        var id = Guid.NewGuid();
        var c = new PluginConfiguration { Custom = new[] { new PosterCustom { ItemId = id.ToString("N"), Title = "IPTV Films" } } };
        Assert.Equal("IPTV Films", c.CustomFor(id)?.Title);
        Assert.Null(c.CustomFor(Guid.NewGuid()));
    }
}
