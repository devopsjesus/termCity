using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed record SettlementFootbridge(Pos Start, Pos End, int Progress)
{
    public const int ConstructionDays = 4;
    public const int MaximumWaterCells = 8;
    public bool Complete => Progress == ConstructionDays;
    public int Length => Math.Abs(End.X - Start.X) + Math.Abs(End.Y - Start.Y);
    public IEnumerable<Pos> Cells()
    {
        int dx = Math.Sign(End.X - Start.X), dy = Math.Sign(End.Y - Start.Y);
        for (int step = 0; step <= Length; step++) yield return Start.Offset(dx * step, dy * step);
    }

    public float DeckHeight(GameMap map) => Math.Max(
        Math.Max(TerrainSprite.Elevation(map, Start.X, Start.Y), TerrainSprite.Elevation(map, End.X, End.Y)),
        Cells().Where(p => map.TerrainAt(p.X, p.Y).Name == DefaultTerrains.WaterName)
            .Select(p => WallWaterLayout.Surface(map, new(p.X + .5f, p.Y + .5f)) + .18f).DefaultIfEmpty(0).Max());
}

public static class SettlementFootbridges
{
    internal static bool Advance(GameMap map)
    {
        if (map.Fortifications is not { } settlement ||
            !map.BuildingFootprints.Values.Any(area => !map.HouseholdAt(area.X, area.Y).IsEmpty)) return false;
        var active = map.FootbridgeList.FindIndex(bridge => !bridge.Complete);
        if (active >= 0)
        {
            var bridge = map.FootbridgeList[active];
            if (!Valid(map, bridge, requireEnclosure: true))
            {
                return false;
            }
            map.FootbridgeList[active] = bridge with { Progress = bridge.Progress + 1 };
            if (bridge.Progress + 1 == SettlementFootbridge.ConstructionDays)
            {
                foreach (var p in bridge.Cells()) map.SetPathWear(p.X, p.Y, 6);
                var cells = bridge.Cells().ToArray();
                for (int i = 1; i < cells.Length; i++)
                    map.LinkPathCells(map.Index(cells[i - 1].X, cells[i - 1].Y), map.Index(cells[i].X, cells[i].Y));
                settlement.InvalidateWallTopology(structureChanged: false);
            }
            map.InvalidateTravel();
            return true;
        }
        var reached = settlement.ReachableLand(map);
        foreach (var start in reached.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            if (!map.TerrainAt(start.X, start.Y).Buildable || !HasAdjacentWater(map, start) ||
                !settlement.WallNetwork.Contains(new(start.X + .5f, start.Y + .5f)))
                continue;
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                for (int step = 1; step <= SettlementFootbridge.MaximumWaterCells + 1; step++)
                {
                    var end = start.Offset(dx * step, dy * step);
                    if (!map.InBounds(end)) break;
                    if (map.TerrainAt(end.X, end.Y).Name == DefaultTerrains.WaterName) continue;
                    if (step < 2 || !map.TerrainAt(end.X, end.Y).Buildable || reached.Contains(end) ||
                        map.BuildingAt(end.X, end.Y) is not null) break;
                    var bridge = new SettlementFootbridge(start, end, 0);
                    if (Valid(map, bridge, requireEnclosure: true) &&
                        !map.FootbridgeList.Any(existing => existing.Cells().Intersect(bridge.Cells()).Any()))
                    {
                        map.FootbridgeList.Add(bridge);
                        return true;
                    }
                    break;
                }
            }
        }
        return false;
    }

    private static bool HasAdjacentWater(GameMap map, Pos start)
    {
        foreach (var (dx, dy) in RoadNetwork.Neighbors)
        {
            var next = start.Offset(dx, dy);
            if (map.InBounds(next) && map.TerrainAt(next.X, next.Y).Name == DefaultTerrains.WaterName)
                return true;
        }
        return false;
    }

    public static bool Valid(GameMap map, SettlementFootbridge bridge, bool requireEnclosure)
    {
        if (!map.InBounds(bridge.Start) || !map.InBounds(bridge.End) ||
            bridge.Start.X != bridge.End.X && bridge.Start.Y != bridge.End.Y ||
            bridge.Length < 2 || bridge.Length > SettlementFootbridge.MaximumWaterCells + 1 ||
            bridge.Progress is < 0 or > SettlementFootbridge.ConstructionDays ||
            !map.TerrainAt(bridge.Start.X, bridge.Start.Y).Buildable ||
            !map.TerrainAt(bridge.End.X, bridge.End.Y).Buildable ||
            Math.Abs(TerrainSprite.Elevation(map, bridge.Start.X, bridge.Start.Y) -
                TerrainSprite.Elevation(map, bridge.End.X, bridge.End.Y)) > .35f) return false;
        var cells = bridge.Cells().ToArray();
        for (int i = 0; i < cells.Length; i++)
        {
            var p = cells[i];
            if (map.BuildingAt(p.X, p.Y) is not null || map.HasRoad(p.X, p.Y) ||
                i > 0 && i < cells.Length - 1 && map.TerrainAt(p.X, p.Y).Name != DefaultTerrains.WaterName)
                return false;
            if (requireEnclosure && (map.Fortifications is not { } settlement ||
                !settlement.WallNetwork.Contains(new(p.X + .5f, p.Y + .5f)) ||
                i > 0 && !settlement.CanWalk(new(cells[i - 1].X + .5f, cells[i - 1].Y + .5f), new(p.X + .5f, p.Y + .5f))))
                return false;
        }
        return true;
    }
}
