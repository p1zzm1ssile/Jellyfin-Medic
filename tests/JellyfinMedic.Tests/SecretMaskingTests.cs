using System.Xml.Linq;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class SecretMaskingTests
{
    // Made up, but shaped like real web tokens (JWT): the same kind of token for two different people.
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJlLWFsaWNl";
    private const string OtherJwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJib2IifQ.c2lnbmF0dXJlLWJvYg";

    [Theory]
    [InlineData("Send failed for https://api.telegram.org/bot123456789:AAHfake1token2for3tests4only/sendMessage", "Send failed for https://api.telegram.org/bot••••/sendMessage")]
    [InlineData("Token rejected: " + Jwt, "Token rejected: ••••")]
    [InlineData("Proxy refused Authorization: Bearer abc123def456", "Proxy refused Authorization: Bearer ••••")]
    public void A_repeated_error_hides_a_secret_that_holds_digits(string message, string shown)
    {
        Assert.Equal(shown, LogScanner.Normalise(message));
    }

    [Fact]
    public void Two_errors_that_differ_only_by_their_token_count_as_one()
    {
        Assert.Equal(LogScanner.Normalise("Token rejected: " + Jwt), LogScanner.Normalise("Token rejected: " + OtherJwt));
    }

    [Fact]
    public void The_banner_hides_a_credential_in_the_line_it_quotes()
    {
        string shown = AdminAlerts.Shorten("[2026-10-16 03:12:45.123 +00:00] [FTL] [1] Main: Proxy refused Authorization: Bearer abc123def456");

        Assert.DoesNotContain("abc123def456", shown);
        Assert.EndsWith("Authorization: Bearer ••••", shown);
    }

    [Fact]
    public void The_banner_hides_a_token_that_starts_just_before_the_line_is_cut()
    {
        // The token starts 14 characters before the cut at 220: cut first, its start would still show.
        string shown = AdminAlerts.Shorten("[2026-10-16 03:12:45.123 +00:00] [FTL] [1] Main: " + new string('x', 150) + " token " + Jwt);

        Assert.DoesNotContain("eyJhbGci", shown);
        Assert.EndsWith(" token ••••", shown);
    }

    [Theory]
    [InlineData("const cfg = { apiKey: 'a1b2c3' };", "const cfg = { apiKey: '****' };")]
    [InlineData("const token = `a1b2c3`;", "const token = `****`;")]
    [InlineData("fetch(url, { headers: { \"X-Api-Key\": \"a1b2c3\" } });", "fetch(url, { headers: { \"X-Api-Key\": \"****\" } });")]
    public void The_support_export_hides_a_secret_in_a_script_whatever_its_quotes(string script, string exported)
    {
        Assert.Equal(exported, SettingsExporter.Mask("Script", script));
    }

    [Theory]
    [InlineData("token: getToken()")]
    [InlineData("placeholder: 'Enter your password'")]
    public void The_support_export_leaves_code_and_text_that_only_name_a_secret(string script)
    {
        Assert.Equal(script, SettingsExporter.Mask("Script", script));
    }

    [Theory]
    [InlineData("const cfg = { apiKey: 'a1b2c3' };", "const cfg = { apiKey: '••••' };")]
    [InlineData("const cfg = { \"apiKey\": \"a1b2c3\" };", "const cfg = { \"apiKey\": \"••••\" };")]
    [InlineData("fetch(url, { headers: { 'X-Api-Key': 'a1b2c3' } });", "fetch(url, { headers: { 'X-Api-Key': '••••' } });")]
    public void The_plugins_view_hides_a_secret_in_a_script_as_the_export_does(string script, string shown)
    {
        Assert.Equal(shown, PluginAuditor.ShownSetting("PluginConfiguration.CustomJavaScriptEntries.CustomJavaScriptEntry[0].Script", new XElement("Script", script)));
    }

    [Theory]
    [InlineData("PluginConfiguration.ApiKey", "a1b2c3", "•••• (hidden)")]
    [InlineData("PluginConfiguration.UseApiKey", "true", "On")]
    [InlineData("PluginConfiguration.Script", "", "(empty)")]
    [InlineData("PluginConfiguration.Script", "token: getToken()", "token: getToken()")]
    public void The_plugins_view_shows_other_settings_as_before(string path, string value, string shown)
    {
        Assert.Equal(shown, PluginAuditor.ShownSetting(path, new XElement(SettingsReader.LeafName(path), value)));
    }

    [Theory]
    [InlineData("X-Api-Key", "a1b2c3", "•••• (hidden)")]
    [InlineData("Title", "Seerr", "Seerr")]
    public void The_plugins_view_hides_the_value_of_a_pair_whose_label_is_secret(string label, string value, string shown)
    {
        var header = new XElement("Header", new XElement("Name", label), new XElement("Value", value));

        Assert.Equal(shown, PluginAuditor.ShownSetting("PluginConfiguration.Headers.Header.Value", header.Element("Value")!));
    }

    [Fact]
    public void The_support_export_still_writes_its_own_mask_for_a_secret_setting()
    {
        Assert.Equal("****", SettingsExporter.Mask("ApiKey", "a1b2c3"));
    }

    [Theory]
    [InlineData("Request failed: Authorization: Bearer abc123def456", "abc123def456")]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized) for http://sonarr:8989/api/v3/series?apikey=0123456789abcdef0123456789abcdef", "0123456789abcdef")]
    public void A_failed_task_error_in_checks_hides_a_token_or_key(string error, string secret)
    {
        Assert.DoesNotContain(secret, DiagnosticsEngine.Shorten(error));
    }

    [Fact]
    public void A_failed_task_error_hides_a_token_that_starts_just_before_it_is_cut()
    {
        // The token starts 13 characters before the cut at 600: cut first, its start would still show.
        string shown = DiagnosticsEngine.Shorten(new string('x', 580) + " token " + Jwt);

        Assert.DoesNotContain("eyJhbGci", shown);
        Assert.EndsWith(" token ••••", shown);
    }
}
