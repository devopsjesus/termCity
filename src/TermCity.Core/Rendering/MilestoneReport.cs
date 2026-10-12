using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public static class MilestoneReport
{
    public const string Title = "Your City Grew!";

    public static ReportPage Build(CityGame game, int index)
    {
        var milestone = CityProgression.Milestones[index];
        var label = Rgb.Hex(0x70d7ff);
        var grey = Rgb.Hex(0x9fb4c7);
        ReportCell[] Row(string name, string value) => [new(name, label), new(value, grey)];
        var groups = new List<ReportGroup>
        {
            new(milestone.Name.ToUpperInvariant(),
            [
                Row("Population", $"{milestone.Population:N0} souls"),
                Row("Gold grant", Fmt.Money(CityProgression.GoldBonus(game, milestone))),
                Row("Status", game.HighestMilestone >= milestone.Population ? "Reached" : "Upcoming"),
                Row("Advice", milestone.Advice),
            ]),
        };
        foreach (var building in game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
            building.MinPopulation > 0 && CityProgression.RequiredPopulation(building) == milestone.Population)
            .OrderBy(building => building.Cost))
        {
            var rows = new List<ReportCell[]>
            {
                Row("Summary", building.Description),
                Row("Cost", Fmt.Money(building.Cost)),
                Row("Upkeep", $"{Fmt.Money(building.WeeklyUpkeep)}/week"),
                Row("Footprint", $"{building.Width} x {building.Height} cells"),
            };
            if (building.Capacity > 0) rows.Add(Row("Capacity", ChoiceHelpContent.CapacityText(building)));
            if (building.Radius > 0) rows.Add(Row("Coverage",
                $"Timely range {building.Radius} walking cells; target {building.ResponseTargetDays:0.00} days; " +
                $"strength {building.Strength}/100; no distance cutoff"));
            if (building.StaffingCapacity > 0) rows.Add(Row("Staffing", $"{building.StaffingCapacity:N0} residents at full funding"));
            if (building.Pollution != 0) rows.Add(Row("Air", building.Pollution > 0 ? "Produces smoke pollution" : "Reduces nearby pollution"));
            if (building.SeatRank > 0) rows.Add(Row("Seat of power", $"Rank {building.SeatRank}"));
            if (building.Pilgrims > 0) rows.Add(Row("Pilgrims", $"{building.Pilgrims} per feast"));
            groups.Add(new(building.Name.ToUpperInvariant(), rows));
        }
        if (milestone.Population is 500 or 5_000)
            groups.Add(new("REDEVELOPMENT", [Row("Unlocks", milestone.Population == 500
                ? "Burgage-house redevelopment with a lord's seat and good land value."
                : "Tenement redevelopment with a keep, room and good land value.")]));
        if (groups.Count == 1)
            groups.Add(new("UNLOCKED BUILDINGS", [Row("Availability", "All earlier buildings remain available.")]));
        return new(milestone.Name, groups);
    }
}
