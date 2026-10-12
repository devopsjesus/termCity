using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Session;

public static class ChoiceHelpContent
{
    public static string Icons(IEnumerable<string> glyphs) => string.Join(" ", glyphs.Distinct());

    public static string CapacityText(BuildingType building)
    {
        string units = building.Service switch
        {
            ServiceKind.Power => "fuel units",
            ServiceKind.Water => "water units",
            ServiceKind.Granary => "grain measures",
            _ => building.Zone switch
            {
                ZoneType.Residential => "residents",
                ZoneType.Commercial or ZoneType.Industrial => "jobs",
                _ => "units",
            },
        };
        return $"{building.Capacity:N0} {units}";
    }

    public static ChoiceHelp Building(CityGame game, BuildingType building)
    {
        var details = new List<string>
        {
            $"Does: {(building.IsMonument ? "Monument" : CityReport.ServiceName(building.Service))}",
            $"Footprint: {building.Width} x {building.Height} cells",
            $"Cost: {Fmt.Money(building.Cost)} per building (terrain may increase it)",
            $"Upkeep: {Fmt.Money(game.Config.FullRules ? (int)Math.Round(building.WeeklyUpkeep * game.Budget.Funding(building.Service)) : 0)}/week",
        };
        if (building.Capacity > 0) details.Add($"Capacity: {CapacityText(building)}");
        if (building.StaffingCapacity > 0) details.Add($"Staffing: {building.StaffingCapacity:N0} residents at full funding");
        if (building.Radius > 0)
            details.Add($"Response target: {building.ResponseTargetDays:0.00} game days; strength: {building.Strength}/100. Longer journeys and excess workload delay service; no distance cutoff.");
        if (building.SeatRank > 0) details.Add($"Seat of power: rank {building.SeatRank}");
        if (building.Pilgrims > 0) details.Add($"Pilgrims: {building.Pilgrims} per feast");
        details.Add("Placement: buildable, unzoned ground with a clear whole-cell footprint and enough gold");
        if (building.MinPopulation > 0)
        {
            int required = CityProgression.RequiredPopulation(building);
            string milestone = CityProgression.Milestones.FirstOrDefault(m => m.Population == required)?.Name ?? $"{required:N0}";
            int remaining = CityProgression.IsUnlocked(game, building) ? 0 : Math.Max(0, required - game.Stats.Population);
            details.Add($"Unlock: {milestone} ({remaining:N0} souls to go)");
        }
        if (building.DebugOnly)
            details.Add(building.IsMonument && CityGame.DebugToolsAvailable
                ? "Debug monument: available immediately; population gate and cost bypassed. Hidden in Release builds."
                : "Debug-only construction: unavailable in Release builds.");
        if (game.Config.FullRules && (building.PowerUse > 0 || building.WaterUse > 0))
            details.Add($"Supply use: {building.PowerUse} fuel, {building.WaterUse} water per week");
        if (building.RequiresWaterNearby) details.Add("Requires nearby open water");
        if (building.Service.IsUtility()) details.Add("Supplies buildings over land; roads speed deliveries. Granaries store city grain.");
        foreach (var area in AreaOfEffect.ForBuilding(building, new Pos(0, 0)))
            details.Add($"Area icons: {Icons(area.Glyphs)} — radius {area.Radius} cells");
        if (building.Pollution != 0)
            details.Add(building.Pollution < 0 ? "Cleans nearby air" : "Produces smoke pollution");
        return new(Icons(building.Glyphs), building.Description, string.Join("\n\n", details));
    }

    public static ChoiceHelp RoadTemplate(CityGame game, TermCity.Core.Roads.RoadTemplate template)
    {
        var road = game.Map.Content.Roads.Get(template.RoadName);
        var help = Road(game, road, false);
        return help with
        {
            Description = "Prebuilt cobbled traffic circle with a grassy center and four connection points.",
            Details = help.Details.Replace("fill valid selected cells", "place one complete traffic-circle ring", StringComparison.Ordinal) +
                $"\n\nSize: {template.Width}x{template.Height}\n\n" +
                "Placement: Clear, unzoned dry land; center must be free of roads. Saved and demolished as ordinary roads.",
        };
    }

    public static ChoiceHelp Road(CityGame game, RoadType road, bool line) => new(
        Icons(road.Glyphs), road.Description,
        $"Does: {(line ? "draw a straight line between two endpoints" : "fill valid selected cells")}.\n\n" +
        $"Traffic capacity: {road.TrafficCapacity} trips/cell/week\n\n" +
        $"Cost: {Fmt.Money((int)Math.Round(game.Config.RoadCostPerCell * road.CostMultiplier))}/cell before terrain; upgrades pay the difference\n\n" +
        $"Upkeep: {Fmt.Money(game.Config.FullRules ? road.WeeklyUpkeep : 0)}/cell/week at full funding\n\n" +
        $"Travel speed: {6 * road.Rank}x open-ground walking speed\n\n" +
        "Needs: buildable land and road clearance. Roads are optional travel boosts, including local roads.\n\n" +
        "Roads appear as smooth curves; these glyphs show connections on overlays and the minimap.");
}
