using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Session;

public static class ChoiceHelpContent
{
    public static string Icons(IEnumerable<string> glyphs) => string.Join(" ", glyphs.Distinct());

    public static ChoiceHelp Building(CityGame game, BuildingType building)
    {
        var details = new List<string>
        {
            $"Does: {CityReport.ServiceName(building.Service)}",
            $"Footprint: {building.Width} x {building.Height} cells",
            $"Cost: {Fmt.Money(building.Cost)} per building (terrain may increase it)",
            $"Upkeep: {Fmt.Money(game.Config.FullRules ? (int)Math.Round(building.WeeklyUpkeep * game.Budget.Funding(building.Service)) : 0)}/week at current funding",
        };
        if (building.Capacity > 0) details.Add($"Capacity: {building.Capacity:N0} units");
        if (building.Radius > 0)
            details.Add($"Radius: {building.Radius} cells; strength: {building.Strength}/100 at centre, fading to edge");
        if (building.SeatRank > 0) details.Add($"Seat of power: rank {building.SeatRank}");
        if (building.Pilgrims > 0) details.Add($"Pilgrims: {building.Pilgrims} per feast");
        details.Add("Placement: buildable, unzoned ground with a clear whole-cell footprint and enough gold");
        details.Add("Operation: connected road access");
        if (building.MinPopulation > 0)
            details.Add($"Unlock: City Grew! at {CityProgression.RequiredPopulation(building):N0} souls" +
                (CityProgression.IsUnlocked(game, building) ? " (unlocked)" : " (locked)"));
        if (game.Config.FullRules && (building.PowerUse > 0 || building.WaterUse > 0))
            details.Add($"Supply use: {building.PowerUse} fuel, {building.WaterUse} water per week");
        if (building.RequiresWaterNearby) details.Add("Requires nearby open water");
        if (building.Service.IsUtility()) details.Add("Supplies connected buildings; an isolated utility is inactive.");
        foreach (var area in AreaOfEffect.ForBuilding(building, new Pos(0, 0)))
            details.Add($"Area icons: {Icons(area.Glyphs)} — radius {area.Radius} cells");
        if (building.Pollution != 0)
            details.Add(building.Pollution < 0 ? "Cleans nearby air" : "Produces smoke pollution");
        return new(Icons(building.Glyphs), building.Description, string.Join("\n\n", details));
    }

    public static ChoiceHelp Road(CityGame game, RoadType road, bool line) => new(
        Icons(road.Glyphs), road.Description,
        $"Does: {(line ? "draw a straight line between two endpoints" : "fill valid selected cells")}.\n\n" +
        $"Traffic capacity: {road.TrafficCapacity} trips/cell/week\n\n" +
        $"Cost: {Fmt.Money((int)Math.Round(game.Config.RoadCostPerCell * road.CostMultiplier))}/cell before terrain; upgrades pay the difference\n\n" +
        $"Upkeep: {Fmt.Money(game.Config.FullRules ? road.WeeklyUpkeep : 0)}/cell/week at full funding\n\n" +
        $"Road access reach: {game.Config.RoadServiceReach} cells\n\n" +
        "Needs: buildable land and road clearance. Connect to the map edge for town access.\n\n" +
        "Roads appear as smooth curves; these glyphs show connections on overlays and the minimap.");
}
