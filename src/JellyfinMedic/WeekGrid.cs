namespace JellyfinMedic.Services;

/// <summary>
/// The planner's week: 15-minute cells from Sunday 00:00 to Saturday 24:00, kept in one line that wraps
/// around. A run that starts late in the evening carries on into the next morning, and Saturday night
/// into Sunday, instead of stopping at midnight.
/// </summary>
internal sealed class WeekGrid
{
    public const int CellMinutes = 15;
    public const int CellsPerDay = 24 * 60 / CellMinutes;
    private const int CellsPerWeek = 7 * CellsPerDay;

    private readonly bool[] _taken = new bool[CellsPerWeek];

    /// <summary>True when every cell of a run of <paramref name="length"/> cells from <paramref name="start"/> is free.</summary>
    public bool IsFree(DayOfWeek day, int start, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (_taken[Index(day, start + i)])
            {
                return false;
            }
        }

        return true;
    }

    public void Mark(DayOfWeek day, int start, int length)
    {
        for (int i = 0; i < length; i++)
        {
            _taken[Index(day, start + i)] = true;
        }
    }

    /// <summary>Cells taken during the day itself, 00:00 to 24:00, including what ran on from the night before.</summary>
    public int Load(DayOfWeek day)
    {
        int used = 0;
        for (int cell = 0; cell < CellsPerDay; cell++)
        {
            if (_taken[Index(day, cell)])
            {
                used++;
            }
        }

        return used;
    }

    // A cell past the end of a day is on the next day, and past Saturday it's Sunday again.
    private static int Index(DayOfWeek day, int cell) =>
        ((((int)day * CellsPerDay) + cell) % CellsPerWeek + CellsPerWeek) % CellsPerWeek;
}
