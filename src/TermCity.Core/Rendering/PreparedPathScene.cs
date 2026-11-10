using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

public sealed record PathSceneRequest(CellRect View, RoadMeshLayout Roads);

public sealed record PreparedPathScene(CellRect View, int Navigation, int Paths, int Terrain,
    FootpathSurfaceLayout Footpaths, IReadOnlyDictionary<Pos, HouseYard> Yards, IReadOnlySet<Pos> YardCells,
    TerrainElevationField? Elevations)
{
    internal static PreparedPathScene Create(CityGame game, PathSceneRequest request)
    {
        var map = game.Map;
        var roads = request.Roads.ForkForMap(map);
        var footpaths = new FootpathSurfaceLayout(map, roads, request.View);
        var strokes = FootpathLayout.Strokes(map);
        var localPaths = new Dictionary<(int X, int Y), FootpathSurfaceLayout>();
        var yards = HouseYardLayout.CreateLazy(map, roads, cell =>
        {
            var key = (X: cell.X / 16, Y: cell.Y / 16);
            if (!localPaths.TryGetValue(key, out var layout))
                localPaths[key] = layout = new FootpathSurfaceLayout(map, strokes, roads,
                    new CellRect(key.X * 16, key.Y * 16, 16, 16));
            return layout;
        });
        var elevations = footpaths.Surfaces.Any(surface => surface.Polygons.Count > 0)
            ? TerrainSprite.CaptureElevations(map) : null;
        return new(request.View, map.NavigationVersion, map.FootpathVersion, map.TerrainVersion,
            footpaths, yards, yards.Values.Select(yard => yard.Cell).ToHashSet(), elevations);
    }
}
