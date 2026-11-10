using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public readonly record struct HouseYard(Pos Cell, Pos Direction);
public readonly record struct HouseOutlineEdge(Pos Start, Pos End, Pos Cell);

/// <summary>One-cell garden extensions on free land, without changing saved building footprints.</summary>
public static class HouseYardLayout
{
    public const float WalkwayWidth = .56f;
    public const float BedCenter = .38f;
    public const float BedWidth = .18f;
    internal static bool IsAdjoining(CellRect area, Pos cell) =>
        cell.X >= area.X && cell.X <= area.Right && (cell.Y == area.Y - 1 || cell.Y == area.Bottom + 1) ||
        cell.Y >= area.Y && cell.Y <= area.Bottom && (cell.X == area.X - 1 || cell.X == area.Right + 1);

    public static IReadOnlyList<HouseOutlineEdge> Outline(CellRect footprint, Pos? yard = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(footprint.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(footprint.Height);
        var cells = footprint.Cells().ToHashSet();
        if (yard is { } extension)
        {
            bool adjoining = !cells.Contains(extension) &&
                (cells.Contains(extension.Offset(-1, 0)) || cells.Contains(extension.Offset(1, 0)) ||
                    cells.Contains(extension.Offset(0, -1)) || cells.Contains(extension.Offset(0, 1)));
            if (!adjoining) throw new ArgumentException("The yard must be one cell adjoining the house.", nameof(yard));
            cells.Add(extension);
        }
        var edges = new List<HouseOutlineEdge>();
        foreach (var cell in cells.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            if (!cells.Contains(cell.Offset(0, -1)))
                edges.Add(new(cell, cell.Offset(1, 0), cell));
            if (!cells.Contains(cell.Offset(1, 0)))
                edges.Add(new(cell.Offset(1, 0), cell.Offset(1, 1), cell));
            if (!cells.Contains(cell.Offset(0, 1)))
                edges.Add(new(cell.Offset(1, 1), cell.Offset(0, 1), cell));
            if (!cells.Contains(cell.Offset(-1, 0)))
                edges.Add(new(cell.Offset(0, 1), cell, cell));
        }
        return edges;
    }

    public static IReadOnlyDictionary<Pos, HouseYard> Create(GameMap map,
        RoadMeshLayout? roads = null, FootpathSurfaceLayout? footpaths = null)
        => CreateCore(map, roads, footpaths, null);

    public static IReadOnlyDictionary<Pos, HouseYard> CreateLazy(GameMap map, RoadMeshLayout? roads,
        Func<Pos, FootpathSurfaceLayout> footpaths)
        => CreateCore(map, roads, null, footpaths);

    private static IReadOnlyDictionary<Pos, HouseYard> CreateCore(GameMap map,
        RoadMeshLayout? roads, FootpathSurfaceLayout? footpaths, Func<Pos, FootpathSurfaceLayout>? createFootpaths)
    {
        var yards = new Dictionary<Pos, HouseYard>();
        var claimed = map.HouseYardCells.Values.Select(map.PosOf).ToHashSet();
        foreach (var (index, area) in map.BuildingFootprints.OrderBy(pair => pair.Key))
        {
            if (map.BuildingAt(area.X, area.Y)?.Zone != ZoneType.Residential) continue;
            var anchor = new Pos(area.X, area.Y);
            if (map.HouseYardCells.TryGetValue(index, out int established))
            {
                var cell = map.PosOf(established);
                if (IsAdjoining(area, cell) && Available(cell, established: true))
                {
                    var direction = cell.Y > area.Bottom ? new Pos(0, 1)
                        : cell.Y < area.Y ? new Pos(0, -1)
                        : cell.X < area.X ? new Pos(-1, 0) : new Pos(1, 0);
                    yards.Add(anchor, new(cell, direction));
                }
                continue;
            }
            int rotation = map.BuildingRotations.GetValueOrDefault(index);
            for (int side = 0; side < 4; side++)
            {
                int facing = (rotation + side) % 4;
                var direction = facing switch
                {
                    1 => new Pos(-1, 0), 2 => new Pos(0, -1), 3 => new Pos(1, 0), _ => new Pos(0, 1),
                };
                int span = direction.X == 0 ? area.Width : area.Height;
                foreach (int along in Enumerable.Range(0, span).OrderBy(i => Math.Abs(i - (span - 1) / 2f)))
                {
                    var cell = direction.X == 0
                        ? new Pos(area.X + along, direction.Y > 0 ? area.Bottom + 1 : area.Y - 1)
                        : new Pos(direction.X > 0 ? area.Right + 1 : area.X - 1, area.Y + along);
                    if (claimed.Contains(cell) || !Available(cell))
                        continue;
                    yards.Add(anchor, new(cell, direction));
                    claimed.Add(cell);
                    map.RegisterHouseYard(index, map.Index(cell.X, cell.Y));
                    break;
                }
                if (yards.ContainsKey(anchor)) break;
            }
        }
        return yards;

        bool Available(Pos cell, bool established = false)
        {
            if (!(map.InBounds(cell) && map.TerrainAt(cell.X, cell.Y).Buildable &&
            !map.HasRoad(cell.X, cell.Y) && map.BuildingAt(cell.X, cell.Y) is null &&
            map.FeatureAt(cell.X, cell.Y) is null && map.ZoneAt(cell.X, cell.Y) == ZoneType.Residential &&
            roads?.RoadOverlapsCell(cell.X, cell.Y, includeShoulders: true) != true)) return false;
            if (established) return true;
            var paths = footpaths ?? createFootpaths?.Invoke(cell);
            return paths?.AtCell(cell.X, cell.Y).Any(polygon =>
                RoadMeshLayout.OverlapsCell(polygon, cell.X, cell.Y)) != true;
        }
    }
}
