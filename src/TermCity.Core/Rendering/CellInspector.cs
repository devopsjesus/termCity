using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>Produces the human readable description of a cell for the inspector panel.</summary>
public static class CellInspector
{
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
            if (map.ZoneRemovalAt(pos.X, pos.Y) is { } removal)
            {
                double days = Math.Max(0, removal.RemoveAtDay - game.ElapsedDays);
                lines.Add($"Unzoned: removal in {days:0.0} game days; restore {Zones.Get(removal.Zone).Name} to keep it");
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
        var services = game.Services;
        if (!services.IsPowered(game.Map, i)) lines.Add("No fuel");
        if (!services.IsWatered(game.Map, i)) lines.Add("No water");
        if (zone == ZoneType.Residential && !game.Map.HouseholdAt(pos.X, pos.Y).IsEmpty)
        {
            lines.Add($"Contentment {CityAnalysis.CellHappiness(game, i):0}");
        }

        lines.Add($"Land value {CityAnalysis.LandValue(game, i):0}");
    }
}
