namespace TermCity.Core.Simulation;

public enum CalendarFeast
{
    None,
    Candlemas,
    LadyDay,
    MayDay,
    Middsommer,
    Lammas,
    Michaelmas,
    AllHallows,
    Christmas,
}

public static class MedievalCalendar
{
    public static string DateLabel(int year, int weekOfYear, int weeksPerYear = 52)
    {
        int labelWidth = Math.Max(13, 11 + weeksPerYear.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return $"{WeekLabel(weekOfYear, weeksPerYear).PadRight(labelWidth)} AD {year,4}";
    }

    public static CalendarFeast Feast(int weekOfYear, int weeksPerYear = 52)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weeksPerYear);
        if (weekOfYear < 1 || weekOfYear > weeksPerYear)
            throw new ArgumentOutOfRangeException(nameof(weekOfYear));
        if (weeksPerYear != 52) return CalendarFeast.None;
        // Fixed feasts mapped to the game's seven-day weeks, starting in January.
        return weekOfYear switch
        {
            5 => CalendarFeast.Candlemas,
            12 => CalendarFeast.LadyDay,
            18 => CalendarFeast.MayDay,
            25 => CalendarFeast.Middsommer,
            31 => CalendarFeast.Lammas,
            39 => CalendarFeast.Michaelmas,
            44 => CalendarFeast.AllHallows,
            52 => CalendarFeast.Christmas,
            _ => CalendarFeast.None,
        };
    }

    public static string WeekLabel(int weekOfYear, int weeksPerYear = 52) =>
        Feast(weekOfYear, weeksPerYear) switch
        {
            CalendarFeast.Candlemas => "Candlemas",
            CalendarFeast.LadyDay => "Lady Day",
            CalendarFeast.MayDay => "May Day",
            CalendarFeast.Middsommer => "Middsommer",
            CalendarFeast.Lammas => "Lammas",
            CalendarFeast.Michaelmas => "Michaelmas",
            CalendarFeast.AllHallows => "All Hallow's",
            CalendarFeast.Christmas => "Christmas",
            _ => $"Week the {weekOfYear}{OrdinalSuffix(weekOfYear)}",
        };

    private static string OrdinalSuffix(int number) => number % 100 is 11 or 12 or 13 ? "th" :
        (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}
