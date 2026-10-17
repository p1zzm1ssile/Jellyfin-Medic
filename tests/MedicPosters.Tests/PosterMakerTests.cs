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
        Assert.Contains("Style: Title," + PosterMaker.FontName, full);
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
}
