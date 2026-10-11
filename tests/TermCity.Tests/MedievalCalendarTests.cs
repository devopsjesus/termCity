using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class MedievalCalendarTests
{
    [Theory]
    [InlineData(2026, 1, 1, 1)]
    [InlineData(2026, 10, 10, 41)]
    [InlineData(2026, 12, 31, 52)]
    [InlineData(2024, 12, 31, 52)]
    public void NewMapCanStartAtApproximateRealWorldWeek(int year, int month, int day, int expected)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 80, MapHeight = 24, StartingYear = 1289 },
            startingDate: new DateTime(year, month, day));
        Assert.Equal(expected, game.WeekOfYear);
        Assert.Equal(1289, game.Year);
        TestCity.Advance(game, 1);
        Assert.Equal(expected == 52 ? 1 : expected + 1, game.WeekOfYear);
        Assert.Equal(expected == 52 ? 1290 : 1289, game.Year);
    }

    [Theory]
    [InlineData(17, "Week the 17th AD 1289")]
    [InlineData(1, "Week the 1st  AD 1289")]
    [InlineData(44, "All Hallow's  AD 1289")]
    [InlineData(52, "Christmas     AD 1289")]
    public void DatePlacesPaddedWeekBeforeAnnoDominiYear(int week, string expected) =>
        Assert.Equal(expected, MedievalCalendar.DateLabel(1289, week));

    [Fact]
    public void YearPositionAndDateWidthAreStableAcrossEveryWeek()
    {
        var dates = Enumerable.Range(1, 52).Select(week => MedievalCalendar.DateLabel(1289, week)).ToArray();
        Assert.Single(dates.Select(date => date.Length).Distinct());
        Assert.All(dates, date => Assert.Equal(14, date.IndexOf("AD", StringComparison.Ordinal)));
        Assert.Equal(dates[0].Length, MedievalCalendar.DateLabel(1290, 1).Length);
    }

    [Fact]
    public void AllFiftyTwoOrdinalsUseTheCorrectSuffix()
    {
        for (int week = 1; week <= 52; week++)
        {
            string suffix = week is 11 or 12 or 13 ? "th" :
                (week % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            Assert.Equal($"Week the {week}{suffix}", MedievalCalendar.WeekLabel(week, 53));
            if (MedievalCalendar.Feast(week) == CalendarFeast.None)
                Assert.Equal($"Week the {week}{suffix}", MedievalCalendar.WeekLabel(week));
        }
    }

    [Theory]
    [InlineData(5, CalendarFeast.Candlemas, "Candlemas")]
    [InlineData(12, CalendarFeast.LadyDay, "Lady Day")]
    [InlineData(18, CalendarFeast.MayDay, "May Day")]
    [InlineData(25, CalendarFeast.Middsommer, "Middsommer")]
    [InlineData(31, CalendarFeast.Lammas, "Lammas")]
    [InlineData(39, CalendarFeast.Michaelmas, "Michaelmas")]
    [InlineData(44, CalendarFeast.AllHallows, "All Hallow's")]
    [InlineData(52, CalendarFeast.Christmas, "Christmas")]
    public void FixedFeastsReplaceOnlyTheirCalendarWeek(int week, CalendarFeast feast, string label)
    {
        Assert.Equal(feast, MedievalCalendar.Feast(week));
        Assert.Equal(label, MedievalCalendar.WeekLabel(week));
        Assert.Equal(CalendarFeast.None, MedievalCalendar.Feast(week, 53));
        if (week > 1) Assert.Equal(CalendarFeast.None, MedievalCalendar.Feast(week - 1));
        if (week < 52) Assert.Equal(CalendarFeast.None, MedievalCalendar.Feast(week + 1));
    }

    [Theory]
    [InlineData(0, 52)]
    [InlineData(53, 52)]
    [InlineData(1, 0)]
    public void InvalidWeeksAreRejected(int week, int weeksPerYear) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MedievalCalendar.WeekLabel(week, weeksPerYear));
}
