using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed record DebugStarterSetup(Pos Spur, CellRect Residential, CellRect Well, CellRect Woodlot)
{
    public Pos CameraTarget => new(Residential.X + Residential.Width / 2, Residential.Y + Residential.Height / 2);

    public static DebugStarterSetup Apply(CityGame game)
    {
        if (!CityGame.DebugToolsAvailable)
            throw new InvalidOperationException("The starter preset is available only in Debug builds.");
        if (game.Config.Scenario != CityScenario.Random || game.Map.BuildingFootprints.Count > 0 ||
            Zones.Placeable.Any(zone => game.Map.ZoneCells(zone).Any()))
            throw new InvalidOperationException("The Debug starter preset requires a new, empty random city.");
        var map = game.Map;
        var spur = HighwayGenerator.StartingSpur(map) ??
            throw new InvalidOperationException("The Debug starter preset requires a generated starting spur.");
        var well = map.Content.Buildings.Get("Town Well");
        var woodlot = map.Content.Buildings.Get("Woodlot");
        var nearby = new CellRect(spur.X - 16, spur.Y - 16, 33, 33).Cells()
            .Where(map.InBounds).OrderBy(p => Distance(p, spur)).ThenBy(p => p.Y).ThenBy(p => p.X).ToArray();
        var wells = nearby.Where(p => Distance(p, spur) <= 8 && game.CanPlaceBuilding(well, p.X, p.Y))
            .Select(p => well.FootprintAt(p.X, p.Y)).ToArray();
        var woodlots = nearby.Where(p => Distance(p, spur) <= 8 && game.CanPlaceBuilding(woodlot, p.X, p.Y))
            .Select(p => woodlot.FootprintAt(p.X, p.Y)).ToArray();
        DebugStarterSetup? setup = null;
        foreach (var origin in nearby)
        {
            var residential = new CellRect(origin.X, origin.Y, 4, 10);
            if (!residential.Cells().All(p => game.CanBuildOn(p.X, p.Y))) continue;
            foreach (var lot in woodlots)
            {
                if (Overlaps(lot, residential)) continue;
                var water = wells.FirstOrDefault(area => !Overlaps(area, residential) && !Overlaps(area, lot));
                if (water.Width == 0) continue;
                setup = new(spur, residential, water, lot);
                break;
            }
            if (setup is not null) break;
        }
        if (setup is null)
            throw new InvalidOperationException("No complete 4x10 starter settlement fits beside this map's starting spur.");
        int cost = game.QuoteBuilding(well, setup.Well).Cost + game.QuoteBuilding(woodlot, setup.Woodlot).Cost;
        if (game.Money < cost)
            throw new InvalidOperationException($"The Debug starter preset needs {cost} gold for its well and woodlot.");

        Require(game.PlaceBuilding(well, setup.Well));
        Require(game.PlaceBuilding(woodlot, setup.Woodlot));
        var zoning = game.Designate(setup.Residential, ZoneType.Residential);
        Require(zoning);
        if (zoning.Cells != 40)
            throw new InvalidOperationException("The Debug starter preset failed to designate all 40 residential cells.");
        game.Paused = false;
        return setup;
    }

    private static int Distance(Pos a, Pos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private static bool Overlaps(CellRect a, CellRect b) =>
        a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;
    private static void Require(ActionResult result)
    {
        if (!result.Success) throw new InvalidOperationException($"Debug starter setup failed: {result.Message}");
    }
}
