using System.Text.Json;
using JellyfinMedic.Api;

namespace JellyfinMedic.Tests;

public sealed class ServerTimeZoneTests
{
    [Fact]
    public void An_iana_zone_is_sent_as_it_is()
    {
        Assert.Equal("America/New_York", ServerTimeZone.IanaId(TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
    }

    // A Windows server names its zones its own way, which no browser knows.
    [Fact]
    public void A_windows_zone_is_sent_as_its_iana_name()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        Assert.False(zone.HasIanaId);
        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId(zone));
    }

    // Left out rather than sent under a name the browser can't place: the page then falls back to the offset.
    [Fact]
    public void A_zone_with_no_iana_name_is_left_out()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Nowhere Standard Time", TimeSpan.FromHours(3), "Nowhere", "Nowhere");

        Assert.Null(ServerTimeZone.IanaId(zone));
    }

    [Fact]
    public void The_report_says_the_server_zone()
    {
        var json = JsonSerializer.Serialize(new DiagnosticReport());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(ServerTimeZone.IanaId(TimeZoneInfo.Local), doc.RootElement.GetProperty("TimeZoneId").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("TimeZoneId").GetString()));
    }
}
