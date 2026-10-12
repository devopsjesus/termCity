using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.World;
using NumericsVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

internal sealed class RoadSurfaceMeshes
{
    internal sealed record Chunk(ArrayMesh Ground, ArrayMesh? Roads, ArrayMesh? Banks,
        Dictionary<int, Vector3[]> Faces, List<(Vector2 A, Vector2 B)> CutEdges, List<Vector3> CutCenters,
        List<Vector3> WallSamples, List<(Vector3 Top, Vector3 Bottom)> ShoreSamples,
        List<(Vector3 Top, Vector3 Bottom)> BridgeSideSamples);

    private readonly RoadMeshLayout _layout;
    private readonly Dictionary<RoadMeshLayout.Segment, (Vector2[] Road, Vector2[] Corridor, Rect2 Bounds)> _polygons = [];
    private readonly Dictionary<int, List<Vector2[]>> _remnants = [];
    private readonly Dictionary<int, bool> _bridgeCuts = [];
    private readonly Dictionary<int, float> _landLevels = [];
    private readonly Dictionary<int, IReadOnlyList<ShorelineLayout.Patch>> _shorelines = [];
    private readonly Dictionary<int, bool> _beachCells = [];
    private static readonly List<Vector2[]> NoRemnants = [];
    internal int RoundedJoinCount { get; private set; }
    internal int NarrowCutFacesClosed { get; private set; }

    public RoadSurfaceMeshes(RoadMeshLayout layout)
    {
        _layout = layout;
        foreach (var segment in layout.Segments)
        {
            var road = Convert(segment.Road); var corridor = Convert(segment.Corridor);
            float left = corridor.Min(p => p.X), top = corridor.Min(p => p.Y);
            _polygons.Add(segment, (road, corridor,
                new Rect2(left, top, corridor.Max(p => p.X) - left, corridor.Max(p => p.Y) - top)));
        }
    }

    public Chunk Build(GameSession session, int chunkX, int chunkY)
    {
        var map = session.Game.Map;
        if (_remnants.Count > 100_000) _remnants.Clear();
        if (_landLevels.Count > 100_000) _landLevels.Clear();
        if (_shorelines.Count > 100_000) _shorelines.Clear();
        if (_beachCells.Count > 100_000) _beachCells.Clear();
        const int span = TerrainSprite.ChunkCells;
        var ground = new TriangleBuffer();
        var roads = new TriangleBuffer();
        var banks = new TriangleBuffer();
        int roadVertices = 0, bankVertices = 0;
        void SetSurfaceColor(TriangleBuffer tool, Color color) => tool.SetColor(color);
        var faces = new Dictionary<int, Vector3[]>();
        var cutEdges = new List<(Vector2 A, Vector2 B)>();
        var cutCenters = new List<Vector3>();
        var wallSamples = new List<Vector3>();
        var shoreSamples = new List<(Vector3 Top, Vector3 Bottom)>();
        var bridgeSideSamples = new List<(Vector3 Top, Vector3 Bottom)>();
        if (session.Overlay == MapOverlay.Off && FlatChunk(map, chunkX * span, chunkY * span, span))
        {
            float x = chunkX * span, y = chunkY * span;
            float level = TerrainSprite.Elevation(map, (int)x, (int)y);
            foreach (var point in new[] { new Vector2(x, y), new Vector2(x, y + span), new Vector2(x + span, y + span),
                new Vector2(x, y), new Vector2(x + span, y + span), new Vector2(x + span, y) })
            {
                ground.SetColor(new Color(Colors.White, level == 0 ? 0.7f : 1));
                ground.SetUV(new((point.X - x) / span, (point.Y - y) / span));
                ground.AddVertex(new(point.X, level, point.Y));
            }
            return new(ground.Commit(), null, null, faces, cutEdges, cutCenters, wallSamples, shoreSamples, bridgeSideSamples);
        }
        var waterCorners = new Dictionary<(int X, int Y), float>();
        var waterHeights = new Dictionary<Vector2, float>();
        var roadHeights = new Dictionary<Vector2, float>();
        var beachAmounts = new Dictionary<Vector2, float>();
        var bankAmounts = new Dictionary<Vector2, float>();
        var shoulderHeights = new Dictionary<Vector2, float>();
        float BeachAmount(Vector2 point)
        {
            if (beachAmounts.TryGetValue(point, out float amount)) return amount;
            amount = ShorelineLayout.BeachAmount(map, point.X, point.Y);
            beachAmounts.Add(point, amount);
            return amount;
        }
        float BankAmount(Vector2 point)
        {
            if (bankAmounts.TryGetValue(point, out float amount)) return amount;
            amount = ShorelineLayout.BankAmount(map, point.X, point.Y);
            bankAmounts.Add(point, amount);
            return amount;
        }
        float GroundHeight(int x, int y, Vector2 point)
        {
            var patches = Shoreline(map, x, y);
            bool water = patches.Count == 1 ? patches[0].Water :
                TerrainSprite.WaterSurface(map, point.X, point.Y) >= ShorelineLayout.WaterThreshold;
            return water
                ? WaterHeight(x, y, point) : LandSurface(x, y, point);
        }
        float LandHeight(int x, int y)
        {
            return LandLevel(map, x, y);
        }
        float LandSurface(int x, int y, Vector2 point)
        {
            float level = LandHeight(x, y);
            if (level > 0) return level;
            if (!BeachCell(map, x, y)) return level;
            float amount = BankAmount(point) * BeachAmount(point);
            return amount == 0 ? level : Mathf.Lerp(level, WaterHeight(x, y, point), amount);
        }
        float WaterHeight(int x, int y, Vector2 point)
        {
            if (waterHeights.TryGetValue(point, out float cached)) return cached;
            float WaterCorner(int dx, int dy)
            {
                var key = (x + dx, y + dy);
                if (waterCorners.TryGetValue(key, out float height)) return height;
                height = TerrainSprite.WaterCornerLevel(map, key.Item1, key.Item2);
                waterCorners.Add(key, height);
                return height;
            }
            float tx = Math.Clamp(point.X - x, 0, 1), ty = Math.Clamp(point.Y - y, 0, 1);
            float height = Mathf.Lerp(Mathf.Lerp(WaterCorner(0, 0), WaterCorner(1, 0), tx),
                Mathf.Lerp(WaterCorner(0, 1), WaterCorner(1, 1), tx), ty);
            waterHeights.Add(point, height);
            return height;
        }
        float GradedHeight(Vector2 point)
        {
            if (roadHeights.TryGetValue(point, out float height)) return height;
            height = RoadHeight(point);
            roadHeights.Add(point, height);
            return height;
        }
        float ShoulderHeight(Vector2 point)
        {
            if (shoulderHeights.TryGetValue(point, out float height)) return height;
            int cellX = Math.Clamp((int)Math.Floor(point.X), 0, map.Width - 1);
            int cellY = Math.Clamp((int)Math.Floor(point.Y), 0, map.Height - 1);
            height = _layout.SupportedShoulderHeight(ToNumerics(point), LandSurface(cellX, cellY, point));
            shoulderHeights.Add(point, height);
            return height;
        }
        bool CutAt(Vector2 point)
        {
            if (_layout.InGroundCorridor(ToNumerics(point)) ||
                _layout.InCorridor(ToNumerics(point)) && BridgeCuts(map, (int)Math.Floor(point.X), (int)Math.Floor(point.Y)))
                return true;
            return IsRemnant(map, point);
        }
        bool CutForCell(int x, int y, Vector2 point)
        {
            var inside = new Vector2(Math.Clamp(point.X, x + 0.0001f, x + 0.9999f),
                Math.Clamp(point.Y, y + 0.0001f, y + 0.9999f));
            return CutAt(inside);
        }
        for (int y = chunkY * span; y < Math.Min(map.Height, (chunkY + 1) * span); y++)
            for (int x = chunkX * span; x < Math.Min(map.Width, (chunkX + 1) * span); x++)
            {
                var terrain = map.TerrainAt(x, y);
                var cellBounds = new Rect2(x, y, 1, 1);
                var nearby = _layout.AtCell(x, y).Where(segment => _polygons[segment].Bounds.Intersects(cellBounds))
                    .OrderByDescending(segment => segment.Type.Rank).ToArray();
                bool groundCut = nearby.Any(s => !s.Bridge) ||
                    nearby.Any(s => s.Bridge) && BridgeCuts(map, x, y) || Remnants(map, x, y).Count > 0;
                float terrainLevel = TerrainSprite.Elevation(map, x, y);
                float landLevel = LandHeight(x, y);
                bool beach = BeachCell(map, x, y);
                bool slopedBank = beach && (ShorelineLayout.BankAmount(map, x, y) > 0 ||
                    ShorelineLayout.BankAmount(map, x + 1, y) > 0 || ShorelineLayout.BankAmount(map, x, y + 1) > 0 ||
                    ShorelineLayout.BankAmount(map, x + 1, y + 1) > 0 ||
                    ShorelineLayout.BankAmount(map, x + 0.5f, y + 0.5f) > 0);
                bool conformEdges = nearby.Length > 0 && (nearby.Any(s => s.Bridge ||
                    s.HeightA != terrainLevel || s.HeightB != terrainLevel) ||
                    Remnants(map, x, y).Count > 0 || TerrainSprite.WaterSurface(map, x + 0.5, y + 0.5) > 0) ||
                    slopedBank;
                var cellFaces = new List<Vector3>();
                var surfaces = new List<(Vector2[] Polygon, TriangleBuffer Tool, Func<Vector2, float> Height,
                    Func<Vector2, Color> Color, bool TerrainTexture, bool? WaterSurface)>();
                var cutPolygons = new List<Vector2[]>();
                var shoreEdges = new List<(Vector2 A, Vector2 B)>();
                var bridgeSurfaces = new List<(Vector2[] Polygon, TermCity.Core.Roads.RoadType Type)>();
                var edgePoints = new HashSet<Vector2>();
                var splitEdges = new Dictionary<(Vector2 A, Vector2 B), Vector2[]>();
                float coveredArea = 0;
                float expectedCellArea = 1;
                var tint = session.Overlay == MapOverlay.Off ? Colors.White :
                    MapOverlays.Tint(session.Game, session.Overlay, x, y, terrain.Background) is { } overlay
                        ? new Color(overlay.R / 255f, overlay.G / 255f, overlay.B / 255f).Lightened(0.3f) : Colors.White;
                var shoreline = Shoreline(map, x, y);
                {
                    if (nearby.Length == 0)
                    {
                        foreach (var patch in shoreline)
                            Emit(Convert(patch.Polygon), ground,
                                p => patch.Water ? WaterHeight(x, y, p) : beach ? LandSurface(x, y, p) : landLevel,
                                _ => tint, true, patch.Water);
                    }
                    else
                    {
                        Vector2[] tile = [new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1)];
                        var remaining = new List<Vector2[]> { tile };
                        var unassignedCorridor = new List<Vector2[]> { tile };
                        var corridors = new List<Vector2[]>();
                        foreach (var segment in nearby)
                        {
                            var footprint = _polygons[segment].Corridor;
                            foreach (var polygon in unassignedCorridor)
                                foreach (var clipped in IntersectRibbon(polygon, footprint))
                                {
                                    corridors.Add(clipped);
                                    if (segment.Bridge) bridgeSurfaces.Add((clipped, segment.Type));
                                }
                            unassignedCorridor = Difference(unassignedCorridor, footprint);
                            if (!segment.Bridge || BridgeCuts(map, x, y))
                            {
                                if (shoreline.Count == 1)
                                {
                                    if (!shoreline[0].Water) remaining = Difference(remaining, footprint);
                                }
                                else
                                    foreach (var patch in shoreline.Where(p => !p.Water))
                                        foreach (var excavation in Geometry2D.IntersectPolygons(Convert(patch.Polygon), footprint))
                                            remaining = Difference(remaining, excavation);
                            }
                        }
                        var unpaved = new List<Vector2[]> { tile };
                        foreach (var segment in nearby)
                        {
                            foreach (var source in unpaved)
                                foreach (var polygon in IntersectRibbon(source, _polygons[segment].Road))
                                {
                                    if (polygon.Length < 3) continue;
                                    Emit(polygon, roads, GradedHeight, p =>
                                    {
                                        var color = segment.Bridge && segment.Type.Rank == 1
                                            ? TermCity.Core.Util.Rgb.Hex(0x96794f) : segment.Type.SurfaceColor;
                                        return new Color(color.R / 255f, color.G / 255f, color.B / 255f) * tint;
                                    }, false);
                                    roadVertices++;
                                }
                            unpaved = Difference(unpaved, _polygons[segment].Road);
                        }
                        var shoulders = new List<Vector2[]>();
                        foreach (var corridor in corridors)
                            foreach (var polygon in unpaved)
                                shoulders.AddRange(Geometry2D.IntersectPolygons(corridor, polygon));
                        foreach (var remnant in Remnants(map, x, y))
                        {
                            shoulders.AddRange(Geometry2D.IntersectPolygons(tile, remnant));
                            remaining = Difference(remaining, remnant);
                        }
                        if (nearby.Any(s => s.Bridge) || shoreline.Any(patch => patch.Water))
                            foreach (var corridor in corridors)
                                foreach (var piece in remaining)
                                    expectedCellArea += Geometry2D.IntersectPolygons(corridor, piece).Sum(Area);
                        foreach (var piece in shoulders)
                        {
                            if (terrainLevel > 0 || nearby.Any(segment => segment.HeightA != segment.HeightB) ||
                                piece.Any(p => Math.Abs(GradedHeight(p) - GroundHeight(x, y, p)) >= 0.12f))
                            {
                                Emit(piece, roads, ShoulderHeight, _ => new Color("#71634b") * tint, false);
                                roadVertices++;
                            }
                            else Emit(piece, ground, ShoulderHeight, _ => tint, true);
                        }
                        foreach (var piece in remaining)
                            foreach (var patch in shoreline)
                                foreach (var polygon in shoreline.Count == 1 ? new[] { piece }.AsEnumerable() :
                                    Geometry2D.IntersectPolygons(piece, Convert(patch.Polygon)).AsEnumerable())
                                {
                                    Emit(polygon, ground, p => patch.Water ? WaterHeight(x, y, p) :
                                        beach ? LandSurface(x, y, p) : landLevel,
                                        _ => tint, true, patch.Water);
                                    if (!patch.Water) cutPolygons.Add(polygon);
                                }
                    }
                    if (shoreline.Count > 1)
                    {
                        var waterEdges = new HashSet<(NumericsVector2 A, NumericsVector2 B)>();
                        foreach (var patch in shoreline)
                        {
                            if (!patch.Water) continue;
                            for (int i = 0; i < patch.Polygon.Length; i++)
                            {
                                var a = patch.Polygon[i]; var b = patch.Polygon[(i + 1) % patch.Polygon.Length];
                                waterEdges.Add((a, b)); waterEdges.Add((b, a));
                            }
                        }
                        foreach (var patch in shoreline)
                        {
                            if (patch.Water) continue;
                            for (int i = 0; i < patch.Polygon.Length; i++)
                            {
                                var a = patch.Polygon[i]; var b = patch.Polygon[(i + 1) % patch.Polygon.Length];
                                if (waterEdges.Contains((a, b))) shoreEdges.Add((new(a.X, a.Y), new(b.X, b.Y)));
                            }
                        }
                    }
                }
                if (conformEdges)
                {
                    if (beach)
                    {
                        edgePoints.Add(new(x + 0.5f, y));
                        edgePoints.Add(new(x + 1, y + 0.5f));
                        edgePoints.Add(new(x + 0.5f, y + 1));
                        edgePoints.Add(new(x, y + 0.5f));
                    }
                    foreach (var surface in surfaces)
                        foreach (var point in surface.Polygon) edgePoints.Add(point);
                    foreach (var cell in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        foreach (var polygon in Remnants(map, cell.Item1, cell.Item2))
                            foreach (var point in polygon) edgePoints.Add(point);
                        if (beach && map.InBounds(cell.Item1, cell.Item2))
                            foreach (var patch in Shoreline(map, cell.Item1, cell.Item2))
                                foreach (var point in patch.Polygon) edgePoints.Add(new(point.X, point.Y));
                    }
                }
                foreach (var surface in surfaces)
                    EmitPrepared(surface.Polygon, surface.Tool, surface.Height, surface.Color, surface.TerrainTexture, surface.WaterSurface);
                AddCutWalls();
                foreach (var edge in shoreEdges) AddShoreWall(edge.A, edge.B);
                foreach (var surface in bridgeSurfaces) AddBridgeSides(surface.Polygon, surface.Type);
                AddTerraceSide(new(x, y), new(x + 1, y), x, y - 1);
                AddTerraceSide(new(x + 1, y), new(x + 1, y + 1), x + 1, y);
                AddTerraceSide(new(x + 1, y + 1), new(x, y + 1), x, y + 1);
                AddTerraceSide(new(x, y + 1), new(x, y), x - 1, y);
                faces.Add(map.Index(x, y), cellFaces.ToArray());
                if (Math.Abs(coveredArea - expectedCellArea) > 0.001f)
                    throw new InvalidOperationException($"Road/shoulder/terrain partition at {x},{y} covers {coveredArea}, expected {expectedCellArea}.");

                void Emit(Vector2[] polygon, TriangleBuffer tool, Func<Vector2, float> height,
                    Func<Vector2, Color> color, bool terrainTexture, bool? waterSurface = null)
                {
                    if (conformEdges) surfaces.Add((polygon, tool, height, color, terrainTexture, waterSurface));
                    else EmitPrepared(polygon, tool, height, color, terrainTexture, waterSurface);
                }

                Vector2[] SplitEdge(Vector2 a, Vector2 b)
                {
                    if (splitEdges.TryGetValue((a, b), out var cached)) return cached;
                    var delta = b - a;
                    float length = delta.LengthSquared();
                    if (length < 0.00000001f) return [a, b];
                    var points = new List<(float T, Vector2 Point)> { (0, a), (1, b) };
                    foreach (var point in edgePoints)
                    {
                        float t = (point - a).Dot(delta) / length;
                        if (t <= 0.0001f || t >= 0.9999f || point.DistanceSquaredTo(a + delta * t) > 0.00000001f)
                            continue;
                        points.Add((t, point));
                    }
                    points.Sort((left, right) => left.T.CompareTo(right.T));
                    var result = new List<Vector2>(points.Count);
                    foreach (var point in points)
                        if (result.Count == 0 || result[^1].DistanceSquaredTo(point.Point) > 0.00000001f)
                            result.Add(point.Point);
                    var split = result.ToArray();
                    splitEdges.Add((a, b), split);
                    splitEdges.TryAdd((b, a), split.Reverse().ToArray());
                    return split;
                }

                void EmitPrepared(Vector2[] polygon, TriangleBuffer tool, Func<Vector2, float> height,
                    Func<Vector2, Color> color, bool terrainTexture, bool? waterSurface)
                {
                    if (terrainTexture && nearby.Length == 0 && shoreline.Count == 1 && !slopedBank)
                    {
                        coveredArea += 1;
                        foreach (int index in new[] { 0, 3, 2, 0, 2, 1 })
                        {
                            var p = polygon[index];
                            var vertex = new Vector3(p.X, height(p), p.Y);
                            SetSurfaceColor(tool, new Color(tint, SurfaceMarker(p, vertex.Y, waterSurface)));
                            tool.SetUV(new((p.X - chunkX * span) / span, (p.Y - chunkY * span) / span));
                            tool.AddVertex(vertex);
                            cellFaces.Add(vertex);
                        }
                        return;
                    }
                    polygon = Clean(polygon);
                    if (Area(polygon) < 0.000001f) return;
                    bool convex = true;
                    float winding = 0;
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        float cross = (polygon[(i + 1) % polygon.Length] - polygon[i])
                            .Cross(polygon[(i + 2) % polygon.Length] - polygon[(i + 1) % polygon.Length]);
                        if (Math.Abs(cross) < 0.0000001f) continue;
                        if (winding != 0 && cross * winding < 0) { convex = false; break; }
                        winding = cross;
                    }
                    if (convex)
                    {
                        coveredArea += Area(polygon);
                        for (int i = 1; i < polygon.Length - 1; i++)
                            EmitTriangle(polygon[0], polygon[winding < 0 ? i : i + 1],
                                polygon[winding < 0 ? i + 1 : i], tool, height, color, terrainTexture, waterSurface);
                        return;
                    }
                    for (int vertex = 0; vertex < polygon.Length; vertex++)
                        for (int edge = 0; edge < polygon.Length; edge++)
                        {
                            int next = (edge + 1) % polygon.Length;
                            if (vertex == edge || vertex == next) continue;
                            var a = polygon[edge]; var delta = polygon[next] - a;
                            if (delta.LengthSquared() < 0.00000001f) continue;
                            float t = (polygon[vertex] - a).Dot(delta) / delta.LengthSquared();
                            if (t <= 0.0001f || t >= 0.9999f ||
                                polygon[vertex].DistanceSquaredTo(a + delta * t) > 0.00000001f) continue;
                            var split = polygon.ToList();
                            split.Insert(edge + 1, polygon[vertex]);
                            var remaining = split.ToArray();
                            int first = Array.IndexOf(remaining, polygon[vertex]);
                            int last = Array.LastIndexOf(remaining, polygon[vertex]);
                            var inner = remaining.Skip(first).Take(last - first).ToArray();
                            var outer = remaining.Take(first).Concat(remaining.Skip(last)).ToArray();
                            if (inner.Length >= 3) EmitPrepared(inner, tool, height, color, terrainTexture, waterSurface);
                            if (outer.Length >= 3) EmitPrepared(outer, tool, height, color, terrainTexture, waterSurface);
                            return;
                        }
                    for (int i = 0; i < polygon.Length; i++)
                        for (int j = i + 2; j < polygon.Length; j++)
                        {
                            if (i == 0 && j == polygon.Length - 1 || polygon[i].DistanceSquaredTo(polygon[j]) > 0.00000001f)
                                continue;
                            var inner = polygon.Skip(i).Take(j - i).ToArray();
                            var outer = polygon.Take(i).Concat(polygon.Skip(j)).ToArray();
                            if (inner.Length >= 3) EmitPrepared(inner, tool, height, color, terrainTexture, waterSurface);
                            if (outer.Length >= 3) EmitPrepared(outer, tool, height, color, terrainTexture, waterSurface);
                            return;
                        }
                    if (Geometry2D.IsPolygonClockwise(polygon)) Array.Reverse(polygon);
                    var indices = Geometry2D.TriangulatePolygon(polygon.Select(p => (p - polygon[0]) * 1024).ToArray());
                    if (indices.Length == 0) throw new InvalidOperationException(
                        $"Road/terrain polygon could not be triangulated at {x},{y}: {string.Join(';', polygon)}.");
                    coveredArea += Area(polygon);
                    for (int i = 0; i < indices.Length; i += 3)
                        EmitTriangle(polygon[indices[i]], polygon[indices[i + 2]], polygon[indices[i + 1]],
                            tool, height, color, terrainTexture, waterSurface);
                }

                void EmitTriangle(Vector2 a, Vector2 b, Vector2 c, TriangleBuffer tool, Func<Vector2, float> height,
                    Func<Vector2, Color> color, bool terrainTexture, bool? waterSurface)
                {
                    if (Math.Abs((b - a).Cross(c - a)) < 0.00000001f) return;
                    if (!conformEdges)
                    {
                        Vertex(a); Vertex(b); Vertex(c);
                        return;
                    }
                    var boundary = SplitEdge(a, b).SkipLast(1).Concat(SplitEdge(b, c).SkipLast(1))
                        .Concat(SplitEdge(c, a).SkipLast(1)).ToArray();
                    if (boundary.Length == 3)
                    {
                        foreach (var p in boundary) Vertex(p);
                    }
                    else
                    {
                        var center = (a + b + c) / 3;
                        for (int i = 0; i < boundary.Length; i++)
                        {
                            Vertex(center);
                            Vertex(boundary[i]);
                            Vertex(boundary[(i + 1) % boundary.Length]);
                        }
                    }
                    void Vertex(Vector2 p)
                    {
                        var vertex = new Vector3(p.X, height(p), p.Y);
                        SetSurfaceColor(tool, terrainTexture ? new Color(color(p), SurfaceMarker(p, vertex.Y, waterSurface)) : color(p));
                        tool.SetUV(terrainTexture ? new((p.X - chunkX * span) / span, (p.Y - chunkY * span) / span) :
                            new(p.X * RoadCurves.CellWidth / 32, p.Y * RoadCurves.CellHeight / 32));
                        tool.AddVertex(vertex);
                        cellFaces.Add(vertex);
                    }
                }

                float SurfaceMarker(Vector2 point, float height, bool? waterSurface)
                {
                    if (waterSurface == true) return 0;
                    if (waterSurface == false && beach)
                    {
                        float sand = BeachAmount(point);
                        return sand > 0 ? 0.75f - sand * 0.2f : 1 - BankAmount(point) * 0.2f;
                    }
                    return waterSurface == false || height >= 0 ? 1 : 0;
                }

                void AddBridgeSides(Vector2[] polygon, TermCity.Core.Roads.RoadType type)
                {
                    var baseColor = type.Rank == 1 ? new Color("#806d4d") : new Color("#777567");
                    SetSurfaceColor(roads, baseColor * tint);
                    for (int i = 0; i < polygon.Length; i++)
                    {
                        var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                        if (OnCellBoundary(a, b, x, y) || a.DistanceSquaredTo(b) < 0.000001f) continue;
                        var normal = new Vector2(-(b - a).Y, (b - a).X).Normalized() * 0.002f;
                        var mid = (a + b) / 2;
                        if (_layout.InCorridor(ToNumerics(mid + normal)) == _layout.InCorridor(ToNumerics(mid - normal))) continue;
                        var points = SplitEdge(a, b);
                        for (int j = 1; j < points.Length; j++)
                        {
                            var start = points[j - 1]; var end = points[j];
                            var topA = new Vector3(start.X, GradedHeight(start), start.Y);
                            var topB = new Vector3(end.X, GradedHeight(end), end.Y);
                            var bottomA = topA - new Vector3(0, 0.12f, 0);
                            var bottomB = topB - new Vector3(0, 0.12f, 0);
                            bridgeSideSamples.Add((topA, bottomA));
                            bridgeSideSamples.Add((topB, bottomB));
                            foreach (var vertex in new[] { topA, topB, bottomB, topA, bottomB, bottomA })
                            {
                                roads.SetUV(new(vertex.X * RoadCurves.CellWidth / 32, vertex.Y * 8));
                                roads.AddVertex(vertex);
                                roadVertices++;
                                cellFaces.Add(vertex);
                            }
                        }
                    }
                }

                void AddCutWalls()
                {
                    if (nearby.Length == 0 || !groundCut) return;
                    var edges = new HashSet<(Vector2 A, Vector2 B)>();
                    foreach (var polygon in cutPolygons)
                        for (int edge = 0; edge < polygon.Length; edge++)
                        {
                            var points = SplitEdge(polygon[edge], polygon[(edge + 1) % polygon.Length]);
                            for (int part = 1; part < points.Length; part++)
                            {
                                var start = points[part - 1]; var end = points[part];
                                if (!edges.Remove((end, start))) edges.Add((start, end));
                            }
                        }
                    foreach (var (start, end) in edges)
                    {
                        var delta = end - start;
                        if (delta.LengthSquared() < 0.000001f || OnCellBoundary(start, end, x, y)) continue;
                        var midpoint = (start + end) / 2;
                        if (_layout.InRoadInterior(ToNumerics(midpoint))) continue;
                        var normal = new Vector2(-delta.Y, delta.X).Normalized() * 0.002f;
                        if (CutAt(midpoint + normal) == CutAt(midpoint - normal))
                            NarrowCutFacesClosed++;
                        AddWall(start, end, LandSurface(x, y, start), LandSurface(x, y, end),
                            ShoulderHeight(start), ShoulderHeight(end), "cut");
                    }
                }

                void AddShoreWall(Vector2 a, Vector2 b)
                {
                    var delta = b - a;
                    if (delta.LengthSquared() < 0.00000001f) return;
                    if (nearby.Length == 0)
                    {
                        AddWall(a, b, LandSurface(x, y, a), LandSurface(x, y, b),
                            WaterHeight(x, y, a), WaterHeight(x, y, b), "shore");
                        return;
                    }
                    var cuts = new List<float> { 0, 1 };
                    foreach (var point in SplitEdge(a, b))
                        cuts.Add(Math.Clamp((point - a).Dot(delta) / delta.LengthSquared(), 0, 1));
                    foreach (var polygon in nearby.SelectMany(s => new[] { _polygons[s].Corridor, _polygons[s].Road })
                        .Concat(Remnants(map, x, y)))
                        for (int i = 0; i < polygon.Length; i++)
                        {
                            var hit = Geometry2D.SegmentIntersectsSegment(a, b, polygon[i], polygon[(i + 1) % polygon.Length]);
                            if (hit.VariantType != Variant.Type.Vector2) continue;
                            var point = (Vector2)hit;
                            cuts.Add(Math.Clamp((point - a).Dot(delta) / delta.LengthSquared(), 0, 1));
                        }
                    cuts.Sort();
                    for (int i = 1; i < cuts.Count; i++)
                    {
                        if (cuts[i] - cuts[i - 1] < 0.00001f) continue;
                        var start = a.Lerp(b, cuts[i - 1]); var end = a.Lerp(b, cuts[i]);
                        bool excavated = CutForCell(x, y, (start + end) / 2);
                        float topA = LandSurface(x, y, start);
                        float topB = LandSurface(x, y, end);
                        if (excavated)
                        {
                            topA = ShoulderHeight(start);
                            topB = ShoulderHeight(end);
                        }
                        AddWall(start, end, topA, topB, WaterHeight(x, y, start), WaterHeight(x, y, end), "shore");
                    }
                }

                void AddTerraceSide(Vector2 a, Vector2 b, int nx, int ny)
                {
                    bool currentWater = shoreline[0].Water;
                    bool neighborWater = map.InBounds(nx, ny) && !map.TerrainAt(nx, ny).Buildable;
                    if (currentWater) return;
                    if (!beach && map.InBounds(nx, ny) && LandHeight(x, y) == LandHeight(nx, ny) &&
                        !neighborWater && Remnants(map, x, y).Count == 0 && Remnants(map, nx, ny).Count == 0)
                        return;
                    var cuts = new List<float> { 0, 1 };
                    var neighborShoreline = map.InBounds(nx, ny) &&
                        TerrainSprite.WaterSurface(map, nx + 0.5, ny + 0.5) > 0 ? Shoreline(map, nx, ny) : [];
                    foreach (var patch in (shoreline.Count > 1 ? shoreline : []).Concat(
                        neighborShoreline.Count > 1 ? neighborShoreline : []))
                        foreach (var vertex in patch.Polygon)
                        {
                            var point = new Vector2(vertex.X, vertex.Y);
                            float t = (point - a).Dot(b - a) / (b - a).LengthSquared();
                            if (t > 0 && t < 1 && point.DistanceSquaredTo(a.Lerp(b, t)) < 0.00000001f)
                                cuts.Add(t);
                        }
                    foreach (var point in SplitEdge(a, b))
                        cuts.Add(Math.Clamp((point - a).Dot(b - a) / (b - a).LengthSquared(), 0, 1));
                    var candidates = nearby.Concat(_layout.AtCell(nx, ny)).Distinct();
                    foreach (var segment in candidates)
                    {
                        foreach (var polygon in new[] { _polygons[segment].Corridor, _polygons[segment].Road })
                            for (int i = 0; i < polygon.Length; i++)
                                if (Geometry2D.SegmentIntersectsSegment(a, b, polygon[i], polygon[(i + 1) % polygon.Length]).VariantType == Variant.Type.Vector2)
                                {
                                    Vector2 crossing = (Vector2)Geometry2D.SegmentIntersectsSegment(a, b, polygon[i], polygon[(i + 1) % polygon.Length]);
                                    cuts.Add(Math.Clamp((crossing - a).Dot(b - a) / (b - a).LengthSquared(), 0, 1));
                                }
                    }
                    foreach (var polygon in Remnants(map, x, y).Concat(Remnants(map, nx, ny)))
                        for (int j = 0; j < polygon.Length; j++)
                        {
                            var hit = Geometry2D.SegmentIntersectsSegment(a, b, polygon[j], polygon[(j + 1) % polygon.Length]);
                            if (hit.VariantType == Variant.Type.Vector2)
                            {
                                Vector2 crossing = (Vector2)hit;
                                cuts.Add(Math.Clamp((crossing - a).Dot(b - a) / (b - a).LengthSquared(), 0, 1));
                            }
                        }
                    cuts.Sort();
                    for (int i = 1; i < cuts.Count; i++)
                    {
                        if (cuts[i] - cuts[i - 1] < 0.00001f) continue;
                        var start = a.Lerp(b, cuts[i - 1]); var end = a.Lerp(b, cuts[i]);
                        var midpoint = (start + end) / 2;
                        bool groundCorridor = _layout.InGroundCorridor(ToNumerics(midpoint));
                        bool currentCut = groundCorridor || CutForCell(x, y, midpoint);
                        bool neighborCut = map.InBounds(nx, ny) && (groundCorridor || CutForCell(nx, ny, midpoint));
                        float ta = currentCut ? ShoulderHeight(start) : LandSurface(x, y, start);
                        float tb = currentCut ? ShoulderHeight(end) : LandSurface(x, y, end);
                        float ba = !map.InBounds(nx, ny) ? -0.04f : neighborWater ? WaterHeight(nx, ny, start) :
                            neighborCut ? ShoulderHeight(start) : LandSurface(nx, ny, start);
                        float bb = !map.InBounds(nx, ny) ? -0.04f : neighborWater ? WaterHeight(nx, ny, end) :
                            neighborCut ? ShoulderHeight(end) : LandSurface(nx, ny, end);
                        if (ta <= ba + 0.00001f && tb <= bb + 0.00001f) continue;
                        AddWall(start, end, ta, tb, ba, bb, neighborWater ? "shore" : "terrace");
                    }
                }

                void AddWall(Vector2 a, Vector2 b, float a1, float b1, float a2, float b2, string kind)
                {
                    if (Math.Max(Math.Abs(a1 - a2), Math.Abs(b1 - b2)) < 0.001f) return;
                    var midpoint = ToNumerics((a + b) / 2);
                    bool belowRoad = _layout.InRoadInterior(midpoint) &&
                        Math.Max(a1, a2) <= _layout.HeightAt(ToNumerics(a)) + 0.001f &&
                        Math.Max(b1, b2) <= _layout.HeightAt(ToNumerics(b)) + 0.001f;
                    if (_layout.InRoadInterior(midpoint) && !belowRoad)
                        throw new InvalidOperationException($"{kind} wall crosses road at {a}-{b}, cell {x},{y}, " +
                            $"heights {a1},{b1}/{a2},{b2}, deck {_layout.HeightAt(midpoint)}, " +
                            $"bridge {_layout.RoadAt(midpoint)?.Bridge}.");
                    var topA = new Vector3(a.X, Math.Max(a1, a2), a.Y);
                    var topB = new Vector3(b.X, Math.Max(b1, b2), b.Y);
                    var lowA = new Vector3(a.X, Math.Min(a1, a2), a.Y);
                    var lowB = new Vector3(b.X, Math.Min(b1, b2), b.Y);
                    if (kind == "shore")
                    {
                        shoreSamples.Add((topA, lowA));
                        shoreSamples.Add((topB, lowB));
                    }
                    ReadOnlySpan<float> samples = kind == "cut"
                        ? [0.005f, 0.025f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 0.975f, 0.995f]
                        : [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f];
                    foreach (float t in samples)
                    {
                        var high = topA.Lerp(topB, t); var low = lowA.Lerp(lowB, t);
                        if (high.Y - low.Y < 0.05f) continue;
                        wallSamples.Add(high.Lerp(low, 0.25f));
                        wallSamples.Add(high.Lerp(low, 0.5f));
                        wallSamples.Add(high.Lerp(low, 0.75f));
                    }
                    var normal = (topB - topA).Cross(lowA - topA).Normalized();
                    float shade = normal.X + normal.Z < 0 ? 1 : 0.78f;
                    float bottom = Math.Min(lowA.Y, lowB.Y), top = Math.Max(topA.Y, topB.Y);
                    for (int band = (int)Math.Floor(bottom / 0.24f); band * 0.24f < top; band++)
                    {
                        float lower = band * 0.24f, upper = lower + 0.24f;
                        var aLow = new Vector3(a.X, Math.Clamp(lower, lowA.Y, topA.Y), a.Y);
                        var aHigh = new Vector3(a.X, Math.Clamp(upper, lowA.Y, topA.Y), a.Y);
                        var bLow = new Vector3(b.X, Math.Clamp(lower, lowB.Y, topB.Y), b.Y);
                        var bHigh = new Vector3(b.X, Math.Clamp(upper, lowB.Y, topB.Y), b.Y);
                        if (aHigh.Y - aLow.Y < 0.00001f && bHigh.Y - bLow.Y < 0.00001f) continue;
                        float stratum = band % 2 == 0 ? 1 : 0.94f;
                        banks.SetColor(new Color(0x72 / 255f * shade * stratum,
                            0x5b / 255f * shade * stratum, 0x40 / 255f * shade * stratum));
                        banks.AddVertex(aHigh); banks.AddVertex(bHigh); banks.AddVertex(bLow);
                        banks.AddVertex(aHigh); banks.AddVertex(bLow); banks.AddVertex(aLow);
                        bankVertices += 6;
                    }
                    if (!belowRoad && kind != "shore") cutEdges.Add((a, b));
                    if (kind == "cut") cutCenters.Add((topA + topB + lowA + lowB) / 4);
                }
            }
        return new(ground.Commit(), roadVertices > 0 ? roads.Commit() : null,
            bankVertices > 0 ? banks.Commit() : null, faces, cutEdges, cutCenters, wallSamples, shoreSamples, bridgeSideSamples);
    }

    private float RoadHeight(Vector2 point) => _layout.HeightAt(ToNumerics(point));

    internal bool IsRemnant(GameMap map, Vector2 point)
    {
        foreach (var polygon in Remnants(map, (int)Math.Floor(point.X), (int)Math.Floor(point.Y)))
            if (ContainsPolygon(polygon, point)) return true;
        return false;
    }

    private List<Vector2[]> Remnants(GameMap map, int x, int y)
    {
        if (!map.InBounds(x, y) || TerrainSprite.Elevation(map, x, y) <= 0 || map.BuildingAt(x, y) is not null)
            return NoRemnants;
        int index = map.Index(x, y);
        if (_remnants.TryGetValue(index, out var cached)) return cached;
        var bounds = new Rect2(x, y, 1, 1);
        var nearby = _layout.AtCell(x, y).Where(s => _polygons[s].Bounds.Intersects(bounds)).ToArray();
        if (!nearby.Any(s => !s.Bridge))
        {
            _remnants.Add(index, NoRemnants);
            return _remnants[index];
        }
        float terrainHeight = TerrainSprite.Elevation(map, x, y);
        if (nearby.All(s => !s.Bridge && s.HeightA == terrainHeight && s.HeightB == terrainHeight) &&
            TerrainSprite.WaterSurface(map, x + 0.5, y + 0.5) == 0)
        {
            _remnants.Add(index, NoRemnants);
            return NoRemnants;
        }
        var pieces = Shoreline(map, x, y).Where(patch => !patch.Water).Select(patch => Convert(patch.Polygon)).ToList();
        foreach (var segment in nearby) pieces = Difference(pieces, _polygons[segment].Corridor);
        var remnants = new List<Vector2[]>();
        foreach (var raw in pieces)
        {
            var piece = Clean(raw);
            if (piece.Length < 3 || Area(piece) < 0.000001f) continue;
            if (RoadMeshLayout.IsThinRemnant(piece.Select(ToNumerics).ToArray()) &&
                piece.All(p => _layout.TryHeightAt(ToNumerics(p), out _)))
            {
                remnants.Add(piece);
                continue;
            }
            bool clockwise = Geometry2D.IsPolygonClockwise(piece);
            for (int i = 0; i < piece.Length; i++)
            {
                var before = piece[(i + piece.Length - 1) % piece.Length];
                var corner = piece[i];
                var after = piece[(i + 1) % piece.Length];
                if (corner.DistanceSquaredTo(before) < 0.000001f ||
                    corner.DistanceSquaredTo(after) < 0.000001f) continue;
                float turn = (corner - before).Cross(after - corner);
                if (clockwise ? turn >= -0.000001f : turn <= 0.000001f) continue;
                bool incomingBoundary = OnCellBoundary(before, corner, x, y);
                bool outgoingBoundary = OnCellBoundary(corner, after, x, y);
                if (incomingBoundary == outgoingBoundary) continue;
                var boundaryPoint = (corner + (incomingBoundary ? before : after)) / 2;
                int nx = x, ny = y;
                if (Math.Abs(boundaryPoint.X - x) < 0.0001f) nx--;
                else if (Math.Abs(boundaryPoint.X - x - 1) < 0.0001f) nx++;
                else if (Math.Abs(boundaryPoint.Y - y) < 0.0001f) ny--;
                else ny++;
                if (!map.InBounds(nx, ny) || TerrainSprite.Elevation(map, nx, ny) >= terrainHeight) continue;
                var clearance = Convert(RoadMeshLayout.RoundedTerraceCorner(ToNumerics(before), ToNumerics(corner), ToNumerics(after)));
                if (!clearance.All(p => _layout.TryHeightAt(ToNumerics(p), out _))) continue;
                foreach (var rounded in Geometry2D.IntersectPolygons(piece, clearance))
                    if (Area(rounded) >= 0.000001f)
                    {
                        remnants.Add(rounded);
                        RoundedJoinCount++;
                    }
            }
        }
        _remnants.Add(index, remnants);
        return remnants;
    }

    private bool BridgeCuts(GameMap map, int x, int y)
    {
        if (!map.InBounds(x, y) || LandLevel(map, x, y) <= 0) return false;
        int index = map.Index(x, y);
        if (_bridgeCuts.TryGetValue(index, out bool cached)) return cached;
        float terrain = LandLevel(map, x, y);
        var bounds = new Rect2(x, y, 1, 1);
        var nearby = _layout.AtCell(x, y);
        if (!nearby.Any(s => s.Bridge && _polygons[s].Bounds.Intersects(bounds)))
        {
            _bridgeCuts.Add(index, false);
            return false;
        }
        float bankMargin = TerrainSprite.HillStepHeight / 2 + 0.06f;
        Vector2[] tile = [new(x, y), new(x + 1, y), new(x + 1, y + 1), new(x, y + 1)];
        bool cut = false;
        foreach (var segment in nearby)
        {
            if (!segment.Bridge || !_polygons[segment].Bounds.Intersects(bounds)) continue;
            foreach (var polygon in Geometry2D.IntersectPolygons(tile, _polygons[segment].Corridor))
                for (int i = 0; i < polygon.Length; i++)
                    if (_layout.HeightAt(ToNumerics(polygon[i])) < terrain + bankMargin ||
                        _layout.HeightAt(ToNumerics((polygon[i] + polygon[(i + 1) % polygon.Length]) / 2)) < terrain + bankMargin)
                    {
                        cut = true;
                        break;
                    }
            if (cut) break;
        }
        _bridgeCuts.Add(index, cut);
        return cut;
    }

    private IReadOnlyList<ShorelineLayout.Patch> Shoreline(GameMap map, int x, int y)
    {
        int index = map.Index(x, y);
        if (_shorelines.TryGetValue(index, out var cached)) return cached;
        var patches = ShorelineLayout.BlockyCell(map, x, y);
        _shorelines.Add(index, patches);
        return patches;
    }

    private bool BeachCell(GameMap map, int x, int y)
    {
        int index = map.Index(x, y);
        if (_beachCells.TryGetValue(index, out bool cached)) return cached;
        bool beach = false;
        if (LandLevel(map, x, y) == 0)
            beach = ShorelineLayout.BeachTouchesCell(map, x, y) && Shoreline(map, x, y).Any(p => !p.Water);
        _beachCells.Add(index, beach);
        return beach;
    }

    private float LandLevel(GameMap map, int x, int y)
    {
        int index = map.Index(x, y);
        if (_landLevels.TryGetValue(index, out float cached)) return cached;
        if (map.TerrainAt(x, y).Buildable)
        {
            float height = TerrainSprite.Elevation(map, x, y);
            _landLevels.Add(index, height);
            return height;
        }
        float land = float.PositiveInfinity;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (map.InBounds(x + dx, y + dy) && map.TerrainAt(x + dx, y + dy).Buildable)
                    land = Math.Min(land, TerrainSprite.Elevation(map, x + dx, y + dy));
        if (float.IsPositiveInfinity(land)) land = 0;
        _landLevels.Add(index, land);
        return land;
    }

    private bool FlatChunk(GameMap map, int x, int y, int span)
    {
        if (x < 2 || y < 2 || x + span + 2 >= map.Width || y + span + 2 >= map.Height) return false;
        float level = TerrainSprite.Elevation(map, x, y);
        for (int cy = y - 2; cy < y + span + 2; cy++)
            for (int cx = x - 2; cx < x + span + 2; cx++)
            {
                var terrain = map.TerrainAt(cx, cy);
                if (!terrain.Buildable || TerrainSprite.Elevation(map, cx, cy) != level || _layout.AtCell(cx, cy).Count > 0)
                    return false;
            }
        return true;
    }

    private static List<Vector2[]> Difference(List<Vector2[]> source, Vector2[] cutter, int depth = 0)
    {
        var result = new List<Vector2[]>();
        foreach (var polygon in source)
        {
            var pieces = Geometry2D.ClipPolygons(polygon, cutter);
            var hole = pieces.FirstOrDefault(piece => Area(piece) >= 0.000001f && Geometry2D.IsPolygonClockwise(piece));
            if (hole is not null)
            {
                if (depth >= 8) throw new InvalidOperationException("Road polygon hole subdivision did not converge.");
                float split = hole.Average(p => p.X);
                float left = polygon.Min(p => p.X) - 1, right = polygon.Max(p => p.X) + 1;
                float top = polygon.Min(p => p.Y) - 1, bottom = polygon.Max(p => p.Y) + 1;
                Vector2[][] halves =
                [
                    [new(left, top), new(split, top), new(split, bottom), new(left, bottom)],
                    [new(split, top), new(right, top), new(right, bottom), new(split, bottom)],
                ];
                foreach (var half in halves)
                    result.AddRange(Difference(Geometry2D.IntersectPolygons(polygon, half).ToList(), cutter, depth + 1));
                continue;
            }
            foreach (var piece in pieces)
                if (Area(piece) >= 0.000001f) result.Add(piece);
        }
        return result;
    }

    private static float Area(Vector2[] polygon)
    {
        float area = 0;
        for (int i = 0; i < polygon.Length; i++)
            area += (polygon[i] - polygon[0]).Cross(polygon[(i + 1) % polygon.Length] - polygon[0]);
        return Math.Abs(area) / 2;
    }

    private static IEnumerable<Vector2[]> IntersectRibbon(Vector2[] polygon, Vector2[] ribbon)
    {
        if (polygon.Length < 3) return [];
        if (polygon.Length > 16) return Geometry2D.IntersectPolygons(polygon, ribbon);
        float winding = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            float turn = (polygon[(i + 1) % polygon.Length] - polygon[i])
                .Cross(polygon[(i + 2) % polygon.Length] - polygon[(i + 1) % polygon.Length]);
            if (Math.Abs(turn) < 0.0000001f) continue;
            if (winding * turn < 0)
                return Geometry2D.IntersectPolygons(polygon, ribbon);
            winding = turn;
        }
        Span<Vector2> input = stackalloc Vector2[32];
        Span<Vector2> output = stackalloc Vector2[32];
        polygon.CopyTo(input);
        int count = polygon.Length;
        float orientation = 0;
        for (int i = 0; i < ribbon.Length; i++)
            orientation += (ribbon[i] - ribbon[0]).Cross(ribbon[(i + 1) % ribbon.Length] - ribbon[0]);
        for (int side = 0; side < ribbon.Length && count > 0; side++)
        {
            var a = ribbon[side]; var edge = ribbon[(side + 1) % ribbon.Length] - a;
            float Distance(Vector2 point) => edge.Cross(point - a) * Math.Sign(orientation);
            int written = 0;
            var previous = input[count - 1];
            float previousDistance = Distance(previous);
            for (int i = 0; i < count; i++)
            {
                var current = input[i];
                float currentDistance = Distance(current);
                if ((previousDistance >= 0) != (currentDistance >= 0))
                    output[written++] = previous.Lerp(current, previousDistance / (previousDistance - currentDistance));
                if (currentDistance >= 0) output[written++] = current;
                previous = current;
                previousDistance = currentDistance;
            }
            count = written;
            var swap = input; input = output; output = swap;
        }
        return count >= 3 ? new[] { input[..count].ToArray() } : [];
    }

    private static bool ContainsPolygon(Vector2[] polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j]; var edge = b - a;
            if (edge.LengthSquared() > 0)
            {
                float t = Math.Clamp((point - a).Dot(edge) / edge.LengthSquared(), 0, 1);
                if (point.DistanceSquaredTo(a + edge * t) < 0.00000004f) return true;
            }
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static Vector2[] Clean(Vector2[] polygon)
    {
        var result = new List<Vector2>();
        foreach (var p in polygon)
            if (result.Count == 0 || result[^1].DistanceSquaredTo(p) > 0.00000001f) result.Add(p);
        if (result.Count > 1 && result[0].DistanceSquaredTo(result[^1]) < 0.00000001f) result.RemoveAt(result.Count - 1);
        bool changed = true;
        while (changed && result.Count > 2)
        {
            changed = false;
            for (int i = result.Count - 1; i >= 0; i--)
            {
                var a = result[(i + result.Count - 1) % result.Count];
                var b = result[i]; var c = result[(i + 1) % result.Count];
                if (Math.Abs((b - a).Cross(c - b)) > 0.0001f * Math.Max((b - a).Length(), (c - b).Length()))
                    continue;
                result.RemoveAt(i);
                changed = true;
            }
        }
        return result.ToArray();
    }

    private static bool OnCellBoundary(Vector2 a, Vector2 b, int x, int y) =>
        Math.Abs(a.X - x) < 0.0001f && Math.Abs(b.X - x) < 0.0001f ||
        Math.Abs(a.X - x - 1) < 0.0001f && Math.Abs(b.X - x - 1) < 0.0001f ||
        Math.Abs(a.Y - y) < 0.0001f && Math.Abs(b.Y - y) < 0.0001f ||
        Math.Abs(a.Y - y - 1) < 0.0001f && Math.Abs(b.Y - y - 1) < 0.0001f;

    private static Vector2[] Convert(NumericsVector2[] points) => points.Select(p => new Vector2(p.X, p.Y)).ToArray();
    private static NumericsVector2 ToNumerics(Vector2 point) => new(point.X, point.Y);

    private sealed class TriangleBuffer
    {
        private readonly List<Vector3> _vertices = [];
        private readonly List<Color> _colors = [];
        private readonly List<Vector2> _uvs = [];
        private Color _color = Colors.White;
        private Vector2 _uv;
        private bool _hasUv;

        public void SetColor(Color color) => _color = color;
        public void SetUV(Vector2 uv) { _uv = uv; _hasUv = true; }

        public void AddVertex(Vector3 vertex)
        {
            _vertices.Add(vertex);
            _colors.Add(_color);
            if (_hasUv) _uvs.Add(_uv);
        }

        public ArrayMesh Commit()
        {
            if (_vertices.Count % 3 != 0 || _hasUv && _uvs.Count != _vertices.Count)
                throw new InvalidOperationException("Terrain meshes require complete triangles with matching vertex attributes.");
            var mesh = new ArrayMesh();
            if (_vertices.Count == 0) return mesh;
            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            using Variant vertices = _vertices.ToArray();
            using Variant colors = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            if (_hasUv)
            {
                using Variant uvs = _uvs.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            }
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }
}
