using System.Globalization;
using System.Text.Json;

namespace MedicShared.Tests;

public sealed class JsonTests
{
    [Fact]
    public void Text_reads_only_strings()
    {
        using var doc = JsonDocument.Parse("""{"s":"value","n":1,"z":null}""");
        var e = doc.RootElement;

        Assert.Equal("value", Json.Text(e, "s"));
        Assert.Null(Json.Text(e, "n"));
        Assert.Null(Json.Text(e, "z"));
        Assert.Null(Json.Text(e, "missing"));
    }

    [Fact]
    public void Numbers_are_read_as_numbers_only()
    {
        using var doc = JsonDocument.Parse("""{"fraction":2.9,"big":3000000000,"quoted":"42"}""");
        var e = doc.RootElement;

        Assert.Equal(2, Json.Int(e, "fraction"));
        Assert.Equal(3000000000L, Json.Long(e, "big"));
        Assert.Null(Json.Int(e, "quoted"));
        Assert.Null(Json.Int(e, "missing"));
        Assert.Equal(0L, Json.Long(e, "missing"));
    }

    [Fact]
    public void Bool_is_true_only_for_json_true()
    {
        using var doc = JsonDocument.Parse("""{"yes":true,"no":false,"text":"true","one":1}""");
        var e = doc.RootElement;

        Assert.True(Json.Bool(e, "yes"));
        Assert.False(Json.Bool(e, "no"));
        Assert.False(Json.Bool(e, "text"));
        Assert.False(Json.Bool(e, "one"));
        Assert.False(Json.Bool(e, "missing"));
    }

    [Theory]
    [InlineData("2026-10-16T10:00:00Z", "2026-10-16T10:00:00")]
    [InlineData("2026-10-16T10:00:00+02:00", "2026-10-16T08:00:00")]
    [InlineData("2026-10-16T10:00:00", "2026-10-16T10:00:00")]
    public void Dates_come_back_in_utc_and_one_without_an_offset_is_taken_as_utc(string text, string utc)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { at = text }));

        var date = Json.Date(doc.RootElement, "at");

        Assert.Equal(DateTime.Parse(utc, CultureInfo.InvariantCulture), date);
        Assert.Equal(DateTimeKind.Utc, date!.Value.Kind);
    }

    [Fact]
    public void Only_a_date_string_is_a_date()
    {
        using var doc = JsonDocument.Parse("""{"word":"soon","epoch":1760000000}""");

        Assert.Null(Json.Date(doc.RootElement, "word"));
        Assert.Null(Json.Date(doc.RootElement, "epoch"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void A_value_that_is_not_an_object_has_no_fields(string json)
    {
        // Other services' versions differ, so an unexpected shape reads as missing and never throws.
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;

        Assert.Null(Json.Text(e, "name"));
        Assert.Null(Json.Int(e, "name"));
        Assert.Equal(0L, Json.Long(e, "name"));
        Assert.False(Json.Bool(e, "name"));
        Assert.Null(Json.Date(e, "name"));
    }

    [Fact]
    public void An_empty_element_has_no_fields()
    {
        Assert.Null(Json.Text(default, "name"));
    }
}
