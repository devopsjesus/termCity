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

    /// <summary>The one place gold is formatted: an amount such as <c>15,000g</c> regardless of the machine's culture.</summary>
    public static string Money(int amount) =>
        (amount < 0 ? "-" : string.Empty) + Math.Abs((long)amount).ToString("N0", CultureInfo.InvariantCulture) + "g";
}
