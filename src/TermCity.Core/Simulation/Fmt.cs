using System.Globalization;

namespace TermCity.Core.Simulation;

public static class Fmt
{
    /// <summary>Formats a dollar amount such as <c>$15,000</c> regardless of the machine's culture.</summary>
    public static string Money(int amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs((long)amount).ToString("N0", CultureInfo.InvariantCulture);
}
