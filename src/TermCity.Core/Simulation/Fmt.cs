using System.Globalization;

namespace TermCity.Core.Simulation;

public static class Fmt
{
    public static string WeekBar(int day, int daysPerWeek)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(daysPerWeek);
        day = Math.Clamp(day, 0, daysPerWeek - 1);
        return "[" + new string('=', day) + ">" + new string('.', daysPerWeek - day - 1) + "]";
    }

    /// <summary>Formats a dollar amount such as <c>$15,000</c> regardless of the machine's culture.</summary>
    public static string Money(int amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs((long)amount).ToString("N0", CultureInfo.InvariantCulture);
}
