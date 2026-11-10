using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record PathSurfaceSource(CellRect View, float MaximumHill, TerrainElevationField Elevations,
        IReadOnlyDictionary<int, Vector3[]> Ground, IReadOnlySet<Pos>? Owners = null);
    private TerrainElevationField? _pathElevations;
    internal double FootpathWorkerMilliseconds { get; private set; }

    private PathSurfaceSource? CapturePathSource(GameMap map, CellRect? requestedView = null)
    {
        if (_footpathLayout is null || _footpathLayout.Surfaces.All(surface => surface.Polygons.Count == 0))
            return null;
        var ground = new Dictionary<int, Vector3[]>();
        var view = requestedView ?? _cameraVisibleView;
        foreach (var surface in _footpathLayout.Surfaces)
            foreach (var polygon in surface.Polygons)
            {
                float left = polygon.Min(point => point.X), right = polygon.Max(point => point.X);
                float back = polygon.Min(point => point.Y), front = polygon.Max(point => point.Y);
                if (right < view.X || left > view.Right + 1 || front < view.Y || back > view.Bottom + 1) continue;
                for (int y = Math.Max(0, (int)MathF.Floor(back) - 1); y <= Math.Min(map.Height - 1, (int)MathF.Floor(front) + 1); y++)
                    for (int x = Math.Max(0, (int)MathF.Floor(left) - 1); x <= Math.Min(map.Width - 1, (int)MathF.Floor(right) + 1); x++)
                    {
                        int cell = map.Index(x, y);
                        if (_groundFaces.TryGetValue(cell, out var faces)) ground.TryAdd(cell, faces);
                        else if (_preparedGround.TryGetValue((x / TerrainSprite.ChunkCells, y / TerrainSprite.ChunkCells), out var prepared) &&
                            prepared.Faces.TryGetValue(cell, out var pending)) ground.TryAdd(cell, pending);
                    }
            }
        return new(view, TerrainSprite.MaximumHillFor(map),
            _pathElevations ??= TerrainSprite.CaptureElevations(map), ground);
    }

    private bool PathGroundPending(GameSession session, CellRect? requestedView = null)
    {
        if (_footpathLayout is null || requestedView is null && _cameraGroundWork is null) return false;
        var map = session.Game.Map;
        var view = requestedView ?? _cameraVisibleView;
        foreach (var polygon in _footpathLayout.Surfaces.SelectMany(surface => surface.Polygons))
        {
            float left = polygon.Min(point => point.X), right = polygon.Max(point => point.X);
            float top = polygon.Min(point => point.Y), bottom = polygon.Max(point => point.Y);
            if (right < view.X || left > view.Right + 1 || bottom < view.Y || top > view.Bottom + 1) continue;
            const int span = TerrainSprite.ChunkCells;
            for (int y = Math.Max(0, (int)MathF.Floor(top) - 1) / span;
                y <= Math.Min(map.Height - 1, (int)MathF.Floor(bottom) + 1) / span; y++)
                for (int x = Math.Max(0, (int)MathF.Floor(left) - 1) / span;
                    x <= Math.Min(map.Width - 1, (int)MathF.Floor(right) + 1) / span; x++)
                    if (requestedView is not null
                        ? !_roadChunks.ContainsKey((x, y)) && !_preparedGround.ContainsKey((x, y))
                        : WaitingForGround((x, y), session)) return true;
        }
        return false;
    }

    private static IReadOnlyList<FootpathReliefTriangle> BuildPathSource(PathMeshWork work)
    {
        var source = new List<FootpathReliefTriangle>();
        var capture = work.Source;
        foreach (var surface in work.Layout.Surfaces)
        {
            int color = FootpathArtwork.ColorForWear(surface.Wear);
            Vector2 Convert(System.Numerics.Vector2 point) => new(point.X, point.Y);
            foreach (var polygon in surface.Polygons)
            {
                var input = polygon.Select(Convert).ToArray();
                float left = input.Min(point => point.X), right = input.Max(point => point.X);
                float back = input.Min(point => point.Y), front = input.Max(point => point.Y);
                if (right < capture.View.X || left > capture.View.Right + 1 ||
                    front < capture.View.Y || back > capture.View.Bottom + 1) continue;
                if (capture.Owners is { } owners && !TouchesPathOwners(left, back, right, front, owners)) continue;
                var pieces = new List<Vector2[]> { input };
                var roads = new HashSet<RoadMeshLayout.Segment>();
                if (work.Roads is { } layout)
                    for (int y = (int)MathF.Floor(back); y <= (int)MathF.Floor(front); y++)
                        for (int x = (int)MathF.Floor(left); x <= (int)MathF.Floor(right); x++)
                            foreach (var segment in layout.AtCell(x, y))
                                if (!segment.Bridge) roads.Add(segment);
                foreach (var road in roads)
                {
                    if (road.Road.Max(point => point.X) < left || road.Road.Min(point => point.X) > right ||
                        road.Road.Max(point => point.Y) < back || road.Road.Min(point => point.Y) > front) continue;
                    var boundary = road.Road.Select(Convert).ToArray();
                    var remaining = new List<Vector2[]>();
                    foreach (var piece in pieces)
                    {
                        var clipped = Geometry2D.ClipPolygons(piece, boundary);
                        remaining.AddRange(clipped);
                    }
                    pieces = remaining;
                    if (pieces.Count == 0) break;
                }
                foreach (var piece in pieces)
                {
                    var triangles = Geometry2D.TriangulatePolygon(piece);
                    for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                    {
                        var interior = (piece[triangles[triangle]] + piece[triangles[triangle + 1]] +
                            piece[triangles[triangle + 2]]) / 3;
                        Vector3 Ground(int vertex)
                        {
                            var point = piece[vertex];
                            var sample = FootpathSurfaceLayout.TerrainSample(new(point.X, point.Y), new(interior.X, interior.Y));
                            return new(point.X, CapturedGroundHeight(capture, sample.X, sample.Y), point.Y);
                        }
                        var a = Ground(triangles[triangle]);
                        var b = Ground(triangles[triangle + 1]);
                        var c = Ground(triangles[triangle + 2]);
                        float rise = Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y));
                        if (rise > TravelRoutes.MaximumFootpathStep) continue;
                        source.Add(new(new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z), new(c.X, c.Y, c.Z), color));
                    }
                }
            }
        }
        return source;
    }

    private static float CapturedGroundHeight(PathSurfaceSource source, float x, float z)
    {
        int cx = Math.Clamp((int)MathF.Floor(x), 0, source.Elevations.Width - 1);
        int cy = Math.Clamp((int)MathF.Floor(z), 0, source.Elevations.Height - 1);
        if (source.Ground.TryGetValue(cy * source.Elevations.Width + cx, out var faces))
        {
            var origin = new Vector3(x, source.MaximumHill + 2, z);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < faces.Length; i += 3)
                nearest = Math.Min(nearest, RayTriangleDistance(origin, Vector3.Down, faces[i], faces[i + 1], faces[i + 2]));
            if (!float.IsPositiveInfinity(nearest)) return origin.Y - nearest;
        }
        return source.Elevations[cx, cy];
    }
}
