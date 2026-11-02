using Jellyfin.Plugin.MedicPicks.Picks;
using MediaBrowser.Model.Globalization;

namespace MedicPicks.Tests;

public sealed class PicksFiltersTests
{
    // Rows of Jellyfin's own language list (iso6392.txt: T|B|639-1|English name|French name).
    private static readonly CultureDto[] Cultures =
    {
        Row("deu|ger|de|German|allemand"),
        Row("eng||en|English|anglais"),
        Row("nno||nn|Norwegian (Nynorsk)|norvégien (nynorsk)"),
        Row("nob||nb|Norwegian (Bokmal)|norvégien (bokmål)"),
        Row("nor||no|Norwegian|norvégien"),
        Row("pol||pl|Polish|polonais"),
        Row("spa||es|Spanish; Castilian|espagnol; castillan"),
        Row("tur||tr|Turkish|turc")
    };

    [Theory]
    [InlineData("tr-TR", "Turkish", "tr")]
    // Jellyfin calls it "Spanish; Castilian", which the page would show as it is.
    [InlineData("es-MX", "Spanish", "es")]
    // All three Norwegian codes count, so the name covers them all.
    [InlineData("nb-NO", "Norwegian", "nb")]
    [InlineData("en-GB", "English", "en")]
    // TMDb is asked in en-GB when the setting is blank.
    [InlineData("", "English", "en")]
    public void The_name_comes_from_Jellyfins_list_and_the_code_from_the_TMDb_setting(string tmdbLanguage, string name, string code)
    {
        var language = AudioLanguage.FromTmdb(tmdbLanguage, FindLanguageInfo);

        Assert.Equal((name, code), (language.Name, language.TwoLetter));
    }

    [Theory]
    [InlineData("tr-TR", "tur")]
    [InlineData("tr-TR", "tr")]
    [InlineData("pl", "pol")]
    [InlineData("de", "ger")]
    [InlineData("de", "deu")]
    [InlineData("nb-NO", "nor")]
    [InlineData("nb-NO", "nob")]
    [InlineData("nb-NO", "nno")]
    [InlineData("en-GB", "en-US")]
    public void A_track_tagged_with_any_code_for_the_language_matches(string tmdbLanguage, string trackTag) =>
        Assert.True(AudioLanguage.FromTmdb(tmdbLanguage, FindLanguageInfo).Matches(trackTag));

    [Fact]
    public void Turkish_dubbed_only_no_longer_keeps_English_audio() =>
        Assert.False(AudioLanguage.FromTmdb("tr-TR", FindLanguageInfo).Matches("eng"));

    [Fact]
    public void A_code_Jellyfin_does_not_know_matches_only_itself()
    {
        // TMDb's code for Cantonese, which isn't an ISO 639-1 code, so Jellyfin's list doesn't have it.
        var cantonese = AudioLanguage.FromTmdb("cn-CN", FindLanguageInfo);

        Assert.Equal("cn", cantonese.TwoLetter);
        Assert.True(cantonese.Matches("cn"));
        Assert.False(cantonese.Matches("eng"));
    }

    // Read the way Jellyfin's LocalizationManager reads its list.
    private static CultureDto Row(string line)
    {
        var parts = line.Split('|');
        var threeLetter = string.IsNullOrEmpty(parts[1]) ? new[] { parts[0] } : new[] { parts[0], parts[1] };
        return new CultureDto(parts[3], parts[3], parts[2], threeLetter);
    }

    // Matches the way ILocalizationManager.FindLanguageInfo does: the first row with that name or code.
    private static CultureDto? FindLanguageInfo(string language) => Cultures.FirstOrDefault(c =>
        language.Equals(c.DisplayName, StringComparison.OrdinalIgnoreCase)
        || language.Equals(c.Name, StringComparison.OrdinalIgnoreCase)
        || c.ThreeLetterISOLanguageNames.Contains(language, StringComparer.OrdinalIgnoreCase)
        || language.Equals(c.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
}
