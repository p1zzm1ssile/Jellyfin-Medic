using System.Text.Json;
using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class WeeklyLedgerTests
{
    private static WeeklyLedger.DaySnapshot Day(int daysAgo, Dictionary<string, string> issues, params string[] ignored) =>
        new() { Day = DateTime.Today.AddDays(-daysAgo), Issues = issues, Ignored = ignored.ToList() };

    [Fact]
    public void An_issue_ignored_since_last_week_is_not_counted_as_fixed()
    {
        var then = Day(7, new() { ["a"] = "Issue A", ["b"] = "Issue B" });
        var now = Day(0, new() { ["a"] = "Issue A" }, "b");
        var summary = new WeeklySummary();

        WeeklyLedger.Compare(then, now, summary);

        Assert.Empty(summary.FixedIssues);
        Assert.Equal(new[] { "Issue B" }, summary.IgnoredIssues);
    }

    [Fact]
    public void An_issue_that_went_away_is_fixed()
    {
        var then = Day(7, new() { ["a"] = "Issue A", ["b"] = "Issue B" });
        var now = Day(0, new() { ["a"] = "Issue A" });
        var summary = new WeeklySummary();

        WeeklyLedger.Compare(then, now, summary);

        Assert.Equal(new[] { "Issue B" }, summary.FixedIssues);
        Assert.Empty(summary.IgnoredIssues);
    }

    [Fact]
    public void An_issue_shown_again_is_not_counted_as_new()
    {
        var then = Day(7, new() { ["a"] = "Issue A" }, "b");
        var now = Day(0, new() { ["a"] = "Issue A", ["b"] = "Issue B" });
        var summary = new WeeklySummary();

        WeeklyLedger.Compare(then, now, summary);

        Assert.Empty(summary.NewIssues);
    }

    [Fact]
    public void A_day_records_ignored_findings_under_the_key_its_issues_use()
    {
        var shown = new Finding { Area = "Server", Severity = Sev.Improve, Title = "Shown" };
        var hidden = new Finding { Area = "Libraries", Severity = Sev.Tip, Title = "Movies: something", Ignored = true };
        var good = new Finding { Area = "Logs", Severity = Sev.Good, Title = "Fine", Ignored = true };

        var day = WeeklyLedger.BuildDay(new[] { shown, hidden, good }, DateTime.Today);

        Assert.Equal(new[] { IgnoreStore.KeyFor(shown) }, day.Issues.Keys);
        Assert.Equal(new[] { IgnoreStore.KeyFor(hidden) }, day.Ignored);
        Assert.Equal(1, day.Others);
    }

    [Fact]
    public void A_snapshot_saved_before_ignored_keys_were_recorded_reads_as_none_ignored()
    {
        var day = JsonSerializer.Deserialize<WeeklyLedger.DaySnapshot>(
            "{\"Day\":\"2026-10-07T00:00:00\",\"Issues\":{\"a\":\"Issue A\"},\"Problems\":0,\"Others\":1}");

        Assert.NotNull(day);
        Assert.Empty(day!.Ignored);
    }
}
