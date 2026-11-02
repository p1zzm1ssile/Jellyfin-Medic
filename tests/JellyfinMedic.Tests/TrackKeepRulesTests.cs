using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class TrackKeepRulesTests
{
    private static TrackInfo Audio(int index, string language, string title = "", bool isDefault = false) =>
        new() { Index = index, Kind = "audio", Language = language, FileLanguage = language, Title = title, Default = isDefault };

    private static TrackInfo Subtitle(int index, string language) =>
        new() { Index = index, Kind = "subtitle", Language = language, FileLanguage = language };

    private static int[] Kept(params TrackInfo[] tracks) => KeptWith("eng", tracks);

    private static int[] KeptWith(string languages, params TrackInfo[] tracks)
    {
        var settings = new PluginConfiguration { TracksKeepLanguages = languages };
        var plan = new FilePlan { Tracks = tracks.ToList() };
        TrackCleaner.ApplyKeepRules(plan, TrackCleaner.KeepLanguages(settings), settings);
        return plan.Tracks.Where(t => t.Keep).Select(t => t.Index).ToArray();
    }

    [Fact]
    public void A_commentary_in_your_language_does_not_replace_the_films_own_audio()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", "Commentary"), Subtitle(3, "eng"));

        Assert.Equal(new[] { 1, 2, 3 }, kept);
    }

    [Fact]
    public void A_foreign_film_keeps_its_subtitles_when_your_language_only_has_a_commentary()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", "Commentary"), Subtitle(3, "jpn"), Subtitle(4, "chi"));

        Assert.Equal(new[] { 1, 2, 3, 4 }, kept);
    }

    [Theory]
    [InlineData("Director's Commentary")]
    [InlineData("AUDIO DESCRIPTION")]
    [InlineData("Descriptive Video Service")]
    [InlineData("English (Audio Described)")]
    [InlineData("Isolated Score")]
    [InlineData("Karaoke")]
    public void Extra_titles_are_recognised_in_any_case(string title)
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "eng", title));

        Assert.Contains(1, kept);
    }

    [Fact]
    public void A_main_track_in_your_language_still_lets_other_languages_go()
    {
        var kept = Kept(Audio(1, "eng", "English 5.1", isDefault: true), Audio(2, "eng", "Commentary"), Audio(3, "fre"));

        Assert.Equal(new[] { 1, 2 }, kept);
    }

    [Fact]
    public void Every_main_track_stays_when_your_language_only_has_an_extra()
    {
        var kept = Kept(Audio(1, "jpn", isDefault: true), Audio(2, "fre"), Audio(3, "eng", "Commentary"));

        Assert.Equal(new[] { 1, 2, 3 }, kept);
    }

    // Jellyfin lists a track tagged "gre" as "ell", so each form has to match the other.
    [Theory]
    [InlineData("gre", "ell")]
    [InlineData("tr", "tur")]
    [InlineData("cs", "ces")]
    [InlineData("he", "heb")]
    public void Your_language_matches_in_either_3_letter_form_or_as_its_2_letter_code(string keep, string track)
    {
        var kept = KeptWith(keep, Audio(1, track), Audio(2, "eng"));

        Assert.Equal(new[] { 1 }, kept);
    }

    [Theory]
    [InlineData("nor", "nob")]
    [InlineData("nob", "nno")]
    [InlineData("heb", "iw")]
    [InlineData("ind", "in")]
    [InlineData("yid", "ji")]
    public void Norwegian_and_retired_2_letter_codes_match_their_language(string keep, string track)
    {
        var kept = KeptWith(keep, Audio(1, track), Audio(2, "eng"));

        Assert.Equal(new[] { 1 }, kept);
    }

    [Theory]
    [InlineData("pt-BR", "por")]
    [InlineData("por", "pt_br")]
    public void A_region_after_the_language_is_ignored(string keep, string track)
    {
        var kept = KeptWith(keep, Audio(1, track), Audio(2, "eng"));

        Assert.Equal(new[] { 1 }, kept);
    }

    [Fact]
    public void An_unknown_code_matches_only_itself()
    {
        var kept = KeptWith("xx", Audio(1, "xx"), Audio(2, "xxx"), Audio(3, "eng"));

        Assert.Equal(new[] { 1 }, kept);
    }

    [Theory]
    [InlineData("ell", "gre")]
    [InlineData("ces", "cze")]
    [InlineData("und", "")]
    public void Jellyfin_and_the_file_name_the_same_language_in_different_forms(string jellyfin, string file)
    {
        Assert.True(TrackCleaner.SameLanguage(jellyfin, file));
    }

    [Fact]
    public void A_different_language_in_the_file_is_still_a_mismatch()
    {
        Assert.False(TrackCleaner.SameLanguage("ell", "eng"));
    }

    [Theory]
    [InlineData("el", "gre")]
    [InlineData("ell", "gre")]
    [InlineData("fre", "fre")]
    [InlineData("fra", "fre")]
    [InlineData("tr", "tur")]
    [InlineData("nb", "nob")]
    [InlineData("iw", "heb")]
    [InlineData("xx", "xx")]
    public void Languages_are_written_into_files_in_the_B_form(string code, string written)
    {
        Assert.Equal(written, LanguageCodes.FileCode(code));
    }

    [Theory]
    [InlineData("gre", true)]
    [InlineData("ell", true)]
    [InlineData("el", true)]
    [InlineData("pt-BR", true)]
    [InlineData("ze", true)]
    [InlineData("qaa", true)]
    [InlineData("xx", false)]
    [InlineData("qua", false)]
    public void Codes_in_the_ISO_list_are_known(string code, bool known)
    {
        Assert.Equal(known, LanguageCodes.IsKnown(code));
    }
}
