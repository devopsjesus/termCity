using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public sealed record BuildingCondition(string Text, Rgb Color);

/// <summary>Produces the human readable description of a cell for the inspector panel.</summary>
public static class CellInspector
{
    public static BuildingCondition? Status(CityGame game, Pos pos)
    {
        var map = game.Map;
        if (!map.InBounds(pos) || map.BuildingAt(pos.X, pos.Y) is not { } building) return null;
        var error = Rgb.Hex(0xff7777);
        var warning = Rgb.Hex(0xffe066);
        int index = map.BuildingAnchor(map.Index(pos.X, pos.Y));
        if (!map.BuildingIsServed(index, game.Network.IsServed))
            return new("Unreachable over land: needs a bridge", error);
        if (game.Config.FullRules)
        {
            bool fuel = !game.Services.IsPowered(map, index), water = !game.Services.IsWatered(map, index);
            if (building.Zone != ZoneType.None && (fuel || water))
                return DeliveryStatus(game, fuel ? ServiceKind.Power : ServiceKind.Water, index);
            if (building.Service != ServiceKind.None && !building.Service.IsUtility())
            {
                double funding = game.Budget.Funding(building.Service);
                if (funding <= 0) return new("Inactive: no funding", error);
                if (game.Insolvent) return new("Reduced service: unpaid upkeep", warning);
                if (funding < 1) return new($"Reduced funding: {funding:P0}", warning);
                double load = game.Services.Workload(building.Service);
                if (load >= 0.85)
                    return new(load >= 1 ? $"Service at capacity: {load:P0} workload; responses delayed" :
                        $"Service nearing capacity: {load:P0} workload", warning);
            }
            else if (building.Service is ServiceKind.Power or ServiceKind.Water)
            {
                double load = game.Services.Workload(building.Service);
                if (load >= 0.85)
                    return new(load >= 1 ? $"Deliveries at capacity: {load:P0} demand; arrivals delayed" :
                        $"Deliveries nearing capacity: {load:P0} demand", warning);
            }
        }
        if (map.ZoneRemovalAt(pos.X, pos.Y) is { } removal)
            return new($"Unzoned: removal in {Math.Max(0, removal.RemoveAtDay - game.ElapsedDays):0.0} game days; restore {Zones.Get(removal.Zone).Name} to keep it", warning);
        if (game.Config.FullRules && building.Zone != ZoneType.None)
        {
            foreach (var kind in ServiceKinds.Area)
            {
                if (!CityProgression.IsServiceUnlocked(game, kind, game.Stats.Population)) continue;
                if (game.Services.BuildingCounts[(int)kind] == 0) continue;
                double days = game.Services.ResponseDays(kind, index);
                string name = CityReport.ServiceName(kind);
                if (double.IsPositiveInfinity(days)) return new($"{name} arrival blocked: needs a bridge", warning);
                if (!game.Services.HasArrived(kind, index)) return new($"{name} awaiting arrival: {days:0.0} day journey", warning);
                if (game.Services.WorkloadAt(kind, index) >= 0.85)
                    return new($"{name} near/at capacity: responses delayed", warning);
                if (days > 1) return new($"{name} slow response: {days:0.0} day journey", warning);
            }
        }
        return new("Active", Rgb.Hex(0x88ee99));
    }

    private static BuildingCondition DeliveryStatus(CityGame game, ServiceKind kind, int index)
    {
        string name = kind == ServiceKind.Power ? "Fuel" : "Water";
        var services = game.Services;
        string text = services.BuildingCounts[(int)kind] == 0 ? $"{name} delivery not dispatched" :
            double.IsPositiveInfinity(services.ResponseDays(kind, index)) ? $"{name} arrival blocked: needs a bridge" :
            !services.HasArrived(kind, index) ? $"{name} awaiting arrival: {services.ResponseDays(kind, index):0.0} day journey" :
            $"{name} deliveries at capacity: demand exceeds supply";
        return new(text, Rgb.Hex(0xffe066));
    }

    /// <summary>One-line description of a cell for the status line.</summary>
    public static string Summary(CityGame game, Pos pos) => string.Join(" · ", Describe(game, pos));

    public static IReadOnlyList<string> Describe(CityGame game, Pos pos)
    {
        var map = game.Map;
        if (!map.InBounds(pos))
        {
            return [];
        }

        var lines = new List<string> { $"({pos.X},{pos.Y}) {map.TerrainAt(pos.X, pos.Y).Name}" };
        if (map.TerrainAt(pos.X, pos.Y).Name == TermCity.Core.Terrain.DefaultTerrains.WaterName &&
            TerrainSprite.Flow(map).At(pos.X, pos.Y) is { River: true } flow)
            lines.Add($"River flow: {(flow.Direction.X > 0 ? "east" : flow.Direction.X < 0 ? "west" :
                flow.Direction.Y > 0 ? "south" : "north")}");

        if (map.FeatureAt(pos.X, pos.Y) is { } feature)
        {
            lines.Add(feature.Name);
        }
        if (map.PathWearAt(pos.X, pos.Y) > 0 && !map.HasRoad(pos.X, pos.Y))
            lines.Add(map.PathWearAt(pos.X, pos.Y) >= 3 ? "Worn footpath: faster walking" : "Footpath forming");

        if (game.Network.RoadAtCellCenter(pos.X, pos.Y) is { } visibleRoad)
        {
            string kind = visibleRoad.Type.Name + (game.Network.IsBridgeAtCellCenter(pos.X, pos.Y) ? " bridge" : string.Empty);
            lines.Add(visibleRoad.Connected ? $"{kind} (connected)" : $"{kind} (NOT connected)");
            double junctionDelay = TravelRoutes.JunctionDelayUnits(map, map.Index(pos.X, pos.Y));
            if (junctionDelay > 0)
                lines.Add($"Inefficient junction: +{junctionDelay / TravelRoutes.UnitsPerDay:0.00} days travel");
        }

        var zone = map.ZoneAt(pos.X, pos.Y);
        if (zone != ZoneType.None)
        {
            bool served = game.Network.IsServed(map, pos.X, pos.Y);
            lines.Add($"{Zones.Get(zone).Name} plot" + (served ? string.Empty : " (unreachable over land)"));
            if (map.BuildingAt(pos.X, pos.Y) is { } building)
            {
                lines.Add(building.Name);
                if (Status(game, pos) is { } status) lines.Add(status.Text);
                var h = map.HouseholdAt(pos.X, pos.Y);
                if (!h.IsEmpty)
                {
                    lines.Add($"{h.Total} souls: {h.Adults}A {h.Children}C {h.Seniors}S");
                }

                AddFullRulesLines(game, pos, zone, lines);
            }
            else
            {
                var diagnostic = GrowthDiagnostics.ForCell(game, pos.X, pos.Y);
                lines.Add("Vacant: " + diagnostic.Message);
                if (diagnostic.Paused)
                {
                    lines.Add("Paused - press P to resume");
                }
            }
        }
        else if (map.BuildingAt(pos.X, pos.Y) is { } standalone)
        {
            lines.Add(standalone.Name);
            var footprint = map.BuildingFootprintAt(pos.X, pos.Y);
            if (footprint.Area > 1) lines.Add($"{footprint.Width}x{footprint.Height} cells");
            if (Status(game, pos) is { } status) lines.Add(status.Text);
            if (map.ZoneRemovalAt(pos.X, pos.Y) is { } removal)
            {
                if (Status(game, pos)?.Text.StartsWith("Unzoned:", StringComparison.Ordinal) != true)
                {
                    double days = Math.Max(0, removal.RemoveAtDay - game.ElapsedDays);
                    lines.Add($"Unzoned: removal in {days:0.0} game days; restore {Zones.Get(removal.Zone).Name} to keep it");
                }
                var h = map.HouseholdAt(pos.X, pos.Y);
                if (!h.IsEmpty)
                {
                    lines.Add($"{h.Total} souls: {h.Adults}A {h.Children}C {h.Seniors}S");
                }
            }
        }

        return lines;
    }

    private static void AddFullRulesLines(CityGame game, Pos pos, ZoneType zone, List<string> lines)
    {
        if (!game.Config.FullRules)
        {
            return;
        }

        int i = game.Map.BuildingAnchor(game.Map.Index(pos.X, pos.Y));
        if (zone == ZoneType.Residential && !game.Map.HouseholdAt(pos.X, pos.Y).IsEmpty)
        {
            lines.Add($"Contentment {CityAnalysis.CellHappiness(game, i):0}");
        }

        lines.Add($"Land value {CityAnalysis.LandValue(game, i):0}");
    }
}
