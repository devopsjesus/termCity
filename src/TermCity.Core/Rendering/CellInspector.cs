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
        int index = map.Index(pos.X, pos.Y);
        if (!map.BuildingIsServed(index, game.Network.IsServed))
            return new("Disconnected: no connected-road access", error);
        if (game.Config.FullRules)
        {
            bool fuel = !game.Services.IsPowered(map, index), water = !game.Services.IsWatered(map, index);
            if (building.Zone != ZoneType.None && (fuel || water))
                return new(fuel && water ? "No fuel or water" : fuel ? "No fuel" : "No water", error);
            if (building.Service != ServiceKind.None && !building.Service.IsUtility())
            {
                double funding = game.Budget.Funding(building.Service);
                if (funding <= 0) return new("Inactive: no funding", error);
                if (game.Insolvent) return new("Reduced service: unpaid upkeep", warning);
                if (funding < 1) return new($"Reduced funding: {funding:P0}", warning);
            }
        }
        if (map.ZoneRemovalAt(pos.X, pos.Y) is { } removal)
            return new($"Unzoned: removal in {Math.Max(0, removal.RemoveAtDay - game.ElapsedDays):0.0} game days; restore {Zones.Get(removal.Zone).Name} to keep it", warning);
        return new("Active", Rgb.Hex(0x88ee99));
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

        if (map.FeatureAt(pos.X, pos.Y) is { } feature)
        {
            lines.Add(feature.Name);
        }

        if (map.HasRoad(pos.X, pos.Y))
        {
            string kind = map.RoadTypeAt(pos.X, pos.Y)!.Name + (map.TerrainAt(pos.X, pos.Y).Buildable ? string.Empty : " bridge");
            lines.Add(game.Network.IsConnected(map, pos.X, pos.Y) ? $"{kind} (connected)" : $"{kind} (NOT connected)");
        }

        var zone = map.ZoneAt(pos.X, pos.Y);
        if (zone != ZoneType.None)
        {
            bool served = game.Network.IsServed(map, pos.X, pos.Y);
            lines.Add($"{Zones.Get(zone).Name} plot" + (served ? string.Empty : " (no road to it)"));
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

        int i = game.Map.Index(pos.X, pos.Y);
        if (zone == ZoneType.Residential && !game.Map.HouseholdAt(pos.X, pos.Y).IsEmpty)
        {
            lines.Add($"Contentment {CityAnalysis.CellHappiness(game, i):0}");
        }

        lines.Add($"Land value {CityAnalysis.LandValue(game, i):0}");
    }
}
