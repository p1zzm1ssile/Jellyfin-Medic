using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public class WeekGridTests
{
    [Fact]
    public void A_run_past_midnight_takes_the_next_morning()
    {
        var grid = new WeekGrid();
        grid.Mark(DayOfWeek.Monday, 94, 4); // 23:30 to 00:30

        Assert.False(grid.IsFree(DayOfWeek.Tuesday, 0, 1));
        Assert.False(grid.IsFree(DayOfWeek.Tuesday, 1, 1));
        Assert.True(grid.IsFree(DayOfWeek.Tuesday, 2, 1));
        Assert.Equal(2, grid.Load(DayOfWeek.Monday));
        Assert.Equal(2, grid.Load(DayOfWeek.Tuesday));
    }

    [Fact]
    public void Saturday_night_runs_on_into_Sunday()
    {
        var grid = new WeekGrid();
        grid.Mark(DayOfWeek.Saturday, 95, 2); // 23:45 to 00:15

        Assert.False(grid.IsFree(DayOfWeek.Sunday, 0, 1));
        Assert.True(grid.IsFree(DayOfWeek.Sunday, 1, 1));
        Assert.False(grid.IsFree(DayOfWeek.Saturday, 90, 8)); // a run across the same midnight sees it
        Assert.True(grid.IsFree(DayOfWeek.Saturday, 0, 95));
    }
}
