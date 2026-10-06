using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Terrain;

public enum MapSide
{
    North,
    East,
    South,
    West,
}

public enum WaterKind
{
    /// <summary>A shallow stretch of sea along one side of the map.</summary>
    Sea,

    /// <summary>A lake cut in half by the edge of the map.</summary>
    EdgeLake,

    /// <summary>A lake in a corner of the map, cut by both edges.</summary>
    CornerLake,

    /// <summary>A lake entirely within the map.</summary>
    InlandLake,
}

/// <summary>
/// The character of a map's water. Each map is one of these, and the type decides what its main water looks like.
/// Rivers run into the water and never into each other, except on a <see cref="RiverConfluence"/> map, which is the one
/// type where two rivers meet and flow on as one.
/// </summary>
public enum WaterMapType
{
    /// <summary>A shallow sea along one side with the main river emptying into it.</summary>
    SeaEdge,

    /// <summary>A city on a bay or lake shore: a body of water cut by an edge or a corner of the map.</summary>
    Bay,

    /// <summary>A large lake in the middle of the land, fed by rivers.</summary>
    LargeLake,

    /// <summary>Two rivers that join into one, which flows on into the sea or a lake.</summary>
    RiverConfluence,

    /// <summary>No sea or large lake: a few smaller lakes inside the land, fed by rivers.</summary>
    InlandLakes,
}

/// <summary>One body of water. Ids below <see cref="WaterPlan.FirstRiverId"/> are bodies; rivers use the ids above.</summary>
/// <param name="Side">The edge a sea or edge lake lies along (for a corner lake, the north or south edge).</param>
/// <param name="Span">How far it runs along the map edge it touches, or its width for an inland lake.</param>
/// <param name="Depth">How far it reaches in from that edge (its height for an inland lake).</param>
/// <param name="Fed">Whether a river flows into it. The inlet of an unfed body is off the map.</param>
public sealed record WaterBody(int Id, WaterKind Kind, MapSide? Side, int Span, int Depth, bool Fed);

/// <param name="Sources">How many separate sources feed it: 1 for an ordinary river, 2 where two rivers meet.</param>
public sealed record WaterRiver(int Id, int TargetBodyId, int Sources);

/// <summary>What was generated, and which water belongs to what.</summary>
public sealed class WaterPlan
{
    public const int FirstRiverId = 100;

    private readonly short[] _ids;
    private readonly int _width;

    internal WaterPlan(WaterMapType type, short[] ids, int width, List<WaterBody> bodies, List<WaterRiver> rivers)
    {
        Type = type;
        _ids = ids;
        _width = width;
        Bodies = bodies;
        Rivers = rivers;
    }

    public WaterMapType Type { get; }

    public IReadOnlyList<WaterBody> Bodies { get; }

    public IReadOnlyList<WaterRiver> Rivers { get; }

    /// <summary>0 for dry land, a body's id, or a river's id (at least <see cref="FirstRiverId"/>).</summary>
    public int IdAt(int x, int y) => _ids[y * _width + x];
}

/// <summary>
/// Lays down the water on a map. Each map has one of a few water types (see <see cref="WaterMapType"/>): a shallow
/// sea along one side, a bay or lake shore cut by an edge or corner, a large lake, or a river confluence. On top of
/// that come a few smaller lakes inside the map. Rivers flow into most bodies of water, each from a point on the edge of
/// the map, and never run into one another except in a confluence; a body without a river is simply fed from somewhere
/// off the map.
/// <para>
/// Water that touches an edge of the map reaches at most ten cells in from it, and a sea covers at most 30 percent of
/// the edge it lies along.
/// </para>
/// </summary>
public sealed class WaterGenerator : ITerrainGenerator
{
    /// <summary>The map area (in cells) that the lake counts are tuned for: the default 160x96 map.</summary>
    private const double ReferenceArea = 160 * 96;

    /// <summary>Water that touches a map edge reaches no further than this from it.</summary>
    public const int MaxEdgeDepth = 10;

    /// <summary>A sea covers no more than this share of the edge it lies along.</summary>
    public const double MaxSeaShareOfEdge = 0.30;

    public int Order => 20;

    /// <summary>The number of small extra lakes inside the map; by default it grows with the size of the map.</summary>
    public int? ExtraLakes { get; init; }

    public void Generate(GenerationContext context, TerrainType terrain) => Build(context.Map, terrain, context.Seed, ExtraLakes);

    /// <summary>Generates the water for a map and reports what it made.</summary>
    public static WaterPlan Build(GameMap map, TerrainType terrain, int seed, int? extraLakes = null)
    {
        var rng = GameRandom.ForStage(seed, "water");
        var ids = new short[map.Width * map.Height];
        var bodies = new List<WaterBody>();
        var rivers = new List<WaterRiver>();
        var targets = new Dictionary<int, (int X, int Y)>();

        // How much of each edge (north, east, south, west) is water already, so that the edges stay mostly land.
        var edgeWater = new int[4];

        var type = ChooseType(rng);
        var primary = PlacePrimary(map, terrain, rng, ids, edgeWater, type, bodies, targets);
        if (primary is null)
        {
            // A map too small for its type still gets some water.
            type = WaterMapType.LargeLake;
            primary = TryPlaceWithRetries(map, terrain, rng, ids, edgeWater, WaterKind.InlandLake, bodies, targets, large: false);
        }

        double scale = Math.Sqrt(map.Width * (double)map.Height / ReferenceArea);
        int extras = extraLakes ?? Math.Clamp((int)Math.Round(scale * 0.8 - 0.5 + rng.NextDouble()), 0, 6);
        for (int i = 0; i < extras; i++)
        {
            TryPlaceWithRetries(map, terrain, rng, ids, edgeWater, WaterKind.InlandLake, bodies, targets, large: false);
        }

        // Rivers run into the water. Each body is fed by one river, with a few exceptions (and a large lake sometimes by
        // two); the exception is a confluence map, whose first body is fed by two rivers that have joined.
        for (int i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            bool isPrimary = primary is not null && body.Id == primary.Id;
            int sources = isPrimary && type == WaterMapType.RiverConfluence ? 2 : 1;
            double fedChance = sources == 2 ? 1.0
                : isPrimary && type == WaterMapType.LargeLake ? 0.95
                : body.Kind == WaterKind.Sea ? 0.92
                : 0.75;
            if (!rng.Chance(fedChance))
            {
                continue;
            }

            int riverId = WaterPlan.FirstRiverId + rivers.Count;
            bool carved = TryCarveRiver(map, terrain, GameRandom.ForStage(seed, $"river:{i}"), ids, riverId, body, targets[body.Id], sources);
            if (!carved && sources == 2)
            {
                // No room for two rivers to meet here: it is an ordinary map after all.
                sources = 1;
                type = primary!.Kind == WaterKind.Sea ? WaterMapType.SeaEdge : WaterMapType.InlandLakes;
                carved = TryCarveRiver(map, terrain, GameRandom.ForStage(seed, $"river:{i}:single"), ids, riverId, body, targets[body.Id], 1);
            }

            if (carved)
            {
                rivers.Add(new WaterRiver(riverId, body.Id, sources));
                bodies[i] = bodies[i] with { Fed = true };

                // A large lake sometimes has a second inlet, from another direction. It never joins the first.
                if (isPrimary && type == WaterMapType.LargeLake && rng.Chance(0.4))
                {
                    int second = WaterPlan.FirstRiverId + rivers.Count;
                    if (TryCarveRiver(map, terrain, GameRandom.ForStage(seed, $"river:{i}:2"), ids, second, body, targets[body.Id], 1))
                    {
                        rivers.Add(new WaterRiver(second, body.Id, 1));
                    }
                }
            }
        }

        return new WaterPlan(type, ids, map.Width, bodies, rivers);
    }

    private static WaterMapType ChooseType(GameRandom rng)
    {
        double roll = rng.NextDouble();
        return roll < 0.28 ? WaterMapType.SeaEdge
            : roll < 0.50 ? WaterMapType.Bay
            : roll < 0.72 ? WaterMapType.LargeLake
            : roll < 0.92 ? WaterMapType.RiverConfluence
            : WaterMapType.InlandLakes;
    }

    private static WaterBody? PlacePrimary(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int[] edgeWater, WaterMapType type,
        List<WaterBody> bodies, Dictionary<int, (int X, int Y)> targets)
    {
        var kind = type switch
        {
            WaterMapType.SeaEdge => WaterKind.Sea,
            WaterMapType.Bay => rng.Chance(0.5) ? WaterKind.EdgeLake : WaterKind.CornerLake,
            WaterMapType.LargeLake or WaterMapType.InlandLakes => WaterKind.InlandLake,
            _ => rng.Chance(0.5) ? WaterKind.Sea : WaterKind.InlandLake,
        };

        return TryPlaceWithRetries(map, terrain, rng, ids, edgeWater, kind, bodies, targets, large: type == WaterMapType.LargeLake);
    }

    private static WaterBody? TryPlaceWithRetries(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int[] edgeWater, WaterKind kind,
        List<WaterBody> bodies, Dictionary<int, (int X, int Y)> targets, bool large)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            int id = bodies.Count + 1;
            if (TryPlace(map, terrain, rng, ids, edgeWater, id, kind, large, out var body, out var target))
            {
                bodies.Add(body!);
                targets[id] = target;
                return body;
            }
        }

        return null;
    }

    // ---- Bodies -------------------------------------------------------------------------------------------------

    private static bool TryPlace(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int[] edgeWater, int id, WaterKind kind, bool large,
        out WaterBody? body, out (int X, int Y) target)
    {
        body = null;
        target = default;
        return kind switch
        {
            WaterKind.Sea => TryPlaceSea(map, terrain, rng, ids, edgeWater, id, out body, out target),
            WaterKind.EdgeLake => TryPlaceEdgeLake(map, terrain, rng, ids, edgeWater, id, corner: false, out body, out target),
            WaterKind.CornerLake => TryPlaceEdgeLake(map, terrain, rng, ids, edgeWater, id, corner: true, out body, out target),
            _ => TryPlaceInlandLake(map, terrain, rng, ids, id, large, out body, out target),
        };
    }

    /// <summary>A shallow sea: a bump of water along one edge, at most 30 percent of its length and 10 cells deep.</summary>
    private static bool TryPlaceSea(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int[] edgeWater, int id, out WaterBody? body,
        out (int X, int Y) target)
    {
        var side = (MapSide)rng.Next(4);
        bool alongX = side is MapSide.North or MapSide.South;
        int edge = alongX ? map.Width : map.Height;
        int far = (alongX ? map.Height : map.Width) - 1;
        int span = Math.Max(6, (int)(edge * (0.12 + (MaxSeaShareOfEdge - 0.12) * rng.NextDouble())));
        int depth = rng.Next(5, MaxEdgeDepth + 1);
        target = default;
        body = null;
        if (edge - span / 2 - 1 <= span / 2 + 1)
        {
            return false;
        }

        int center = rng.Next(span / 2 + 1, edge - span / 2 - 1);
        var noise = new PerlinNoise(rng);
        double offset = rng.NextDouble() * 100;

        var cells = new List<(int X, int Y)>();
        for (int a = center - span / 2; a <= center + span / 2; a++)
        {
            // A half ellipse hugging the edge, with a rippling coast.
            double t = (a - center) / (span / 2.0);
            double profile = Math.Sqrt(Math.Max(0, 1 - t * t)) * (0.85 + 0.25 * (noise.Fractal(a * 0.08, offset, 2) * 2 - 1));
            int reach = Math.Clamp((int)Math.Round(depth * profile), 0, depth);
            for (int k = 0; k < reach; k++)
            {
                int inward = side is MapSide.North or MapSide.West ? k : far - k;
                cells.Add(alongX ? (a, inward) : (inward, a));
            }
        }

        if (cells.Count < 10 || !IsClear(map, ids, cells, margin: 6) || !FitsEdges(map, cells, edgeWater))
        {
            return false;
        }

        Commit(map, terrain, ids, id, cells);
        AddEdges(map, cells, edgeWater);

        // The river flows into the middle of the coast.
        int mid = Math.Max(1, depth / 2);
        int tx = alongX ? center : (side is MapSide.West ? mid : map.Width - 1 - mid);
        int ty = alongX ? (side is MapSide.North ? mid : map.Height - 1 - mid) : center;
        target = (tx, ty);
        body = new WaterBody(id, WaterKind.Sea, side, span, depth, Fed: false);
        return true;
    }

    /// <summary>A lake on an edge of the map (cut in half by it) or in a corner (cut by two).</summary>
    private static bool TryPlaceEdgeLake(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int[] edgeWater, int id, bool corner,
        out WaterBody? body, out (int X, int Y) target)
    {
        body = null;
        target = default;
        var side = (MapSide)rng.Next(4);
        bool alongX = side is MapSide.North or MapSide.South;

        // The reach in from the edge is capped (a radius of at most 8, plus a wobble of up to a fifth, stays within
        // ten cells); the other radius follows from the shape of a character (twice as wide as tall) and from the lake
        // not being too long along the edge.
        int ry, rx;
        if (alongX)
        {
            ry = rng.Next(3, 8);
            rx = (int)Math.Round(2 * ry * (0.9 + 0.4 * rng.NextDouble()));
        }
        else
        {
            rx = rng.Next(5, 9);
            ry = Math.Max(2, (int)Math.Round(rx / (2 * (0.9 + 0.4 * rng.NextDouble()))));
        }

        if (corner)
        {
            // A corner lake reaches in from two edges, so both of its radii are limited.
            rx = Math.Min(rx, 8);
            ry = Math.Min(ry, 7);
        }

        int alongLimit = (int)(MaxSeaShareOfEdge * (alongX ? map.Width : map.Height) / 2);
        if (alongX)
        {
            rx = Math.Min(rx, Math.Max(4, alongLimit));
        }
        else
        {
            ry = Math.Min(ry, Math.Max(2, alongLimit));
        }

        int cx, cy;
        if (corner)
        {
            cx = rng.Chance(0.5) ? 0 : map.Width - 1;
            cy = rng.Chance(0.5) ? 0 : map.Height - 1;
        }
        else
        {
            int lo = (alongX ? rx : ry) + 2;
            int hi = (alongX ? map.Width : map.Height) - lo;
            if (hi <= lo)
            {
                return false;
            }

            int along = rng.Next(lo, hi);
            cx = alongX ? along : (side is MapSide.West ? 0 : map.Width - 1);
            cy = alongX ? (side is MapSide.North ? 0 : map.Height - 1) : along;
        }

        var cells = LakeCells(map, rng, cx, cy, rx, ry);
        if (cells.Count < 12 || !IsClear(map, ids, cells, margin: 6) || !FitsEdges(map, cells, edgeWater))
        {
            return false;
        }

        Commit(map, terrain, ids, id, cells);
        AddEdges(map, cells, edgeWater);
        target = (Math.Clamp(cx + (cx == 0 ? rx / 3 : cx == map.Width - 1 ? -rx / 3 : 0), 0, map.Width - 1),
            Math.Clamp(cy + (cy == 0 ? ry / 3 : cy == map.Height - 1 ? -ry / 3 : 0), 0, map.Height - 1));
        body = new WaterBody(id, corner ? WaterKind.CornerLake : WaterKind.EdgeLake, side, alongX ? 2 * rx : 2 * ry, alongX ? ry : rx, Fed: false);
        return true;
    }

    /// <summary>A lake wholly inside the map, kept well clear of the edges. A large one is half as big again.</summary>
    private static bool TryPlaceInlandLake(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int id, bool large,
        out WaterBody? body, out (int X, int Y) target)
    {
        body = null;
        target = default;
        int maxRadius = Math.Clamp((int)(Math.Min(map.Width / 2.0, map.Height) / 10), 4, 14);
        int ry = large ? rng.Next(maxRadius, maxRadius * 3 / 2 + 1) : rng.Next(3, maxRadius + 1);
        int rx = (int)Math.Round(2 * ry * (0.9 + 0.4 * rng.NextDouble()));
        int marginX = rx + MaxEdgeDepth + 2, marginY = ry + MaxEdgeDepth / 2 + 2;
        if (map.Width <= 2 * marginX || map.Height <= 2 * marginY)
        {
            return false;
        }

        int cx = rng.Next(marginX, map.Width - marginX);
        int cy = rng.Next(marginY, map.Height - marginY);
        var cells = LakeCells(map, rng, cx, cy, rx, ry);
        if (!IsClear(map, ids, cells, margin: 6))
        {
            return false;
        }

        Commit(map, terrain, ids, id, cells);
        target = (cx, cy);
        body = new WaterBody(id, WaterKind.InlandLake, null, 2 * rx, 2 * ry, Fed: false);
        return true;
    }

    /// <summary>The cells of an ellipse with a wobbling shore, clipped to the map.</summary>
    private static List<(int X, int Y)> LakeCells(GameMap map, GameRandom rng, int cx, int cy, int rx, int ry)
    {
        var noise = new PerlinNoise(rng);
        double offset = rng.NextDouble() * 100;
        var cells = new List<(int X, int Y)>();
        for (int y = cy - ry - 2; y <= cy + ry + 2; y++)
        {
            for (int x = cx - rx - 3; x <= cx + rx + 3; x++)
            {
                if (!map.InBounds(x, y))
                {
                    continue;
                }

                double dx = (x - cx) / (double)rx, dy = (y - cy) / (double)ry;
                double shore = 1 + 0.2 * noise.Noise(x * 0.12 + offset, y * 0.24 + offset);
                if (Math.Sqrt(dx * dx + dy * dy) <= shore)
                {
                    cells.Add((x, y));
                }
            }
        }

        return cells;
    }

    private static int[] EdgeCounts(GameMap map, IEnumerable<(int X, int Y)> cells)
    {
        var counts = new int[4];
        foreach (var (x, y) in cells)
        {
            counts[0] += y == 0 ? 1 : 0;
            counts[1] += x == map.Width - 1 ? 1 : 0;
            counts[2] += y == map.Height - 1 ? 1 : 0;
            counts[3] += x == 0 ? 1 : 0;
        }

        return counts;
    }

    /// <summary>Whether the cells would leave every edge of the map at least 70 percent land.</summary>
    private static bool FitsEdges(GameMap map, List<(int X, int Y)> cells, int[] edgeWater)
    {
        var counts = EdgeCounts(map, cells);
        for (int side = 0; side < 4; side++)
        {
            int length = side is 0 or 2 ? map.Width : map.Height;
            if (edgeWater[side] + counts[side] > MaxSeaShareOfEdge * length)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddEdges(GameMap map, List<(int X, int Y)> cells, int[] edgeWater)
    {
        var counts = EdgeCounts(map, cells);
        for (int side = 0; side < 4; side++)
        {
            edgeWater[side] += counts[side];
        }
    }

    /// <summary>Whether none of the cells, nor anything within a few cells of them, is already water.</summary>
    private static bool IsClear(GameMap map, short[] ids, List<(int X, int Y)> cells, int margin)
    {
        foreach (var (x, y) in cells)
        {
            for (int dy = -margin / 2; dy <= margin / 2; dy++)
            {
                for (int dx = -margin; dx <= margin; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (map.InBounds(nx, ny) && ids[ny * map.Width + nx] != 0)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static void Commit(GameMap map, TerrainType terrain, short[] ids, int id, IEnumerable<(int X, int Y)> cells)
    {
        foreach (var (x, y) in cells)
        {
            map.SetTerrain(x, y, terrain);
            ids[y * map.Width + x] = (short)id;
        }
    }

    // ---- Rivers -------------------------------------------------------------------------------------------------

    private readonly record struct Segment(HashSet<(int X, int Y)> Cells, List<(int X, int Y, double Progress)> Centers);

    /// <summary>
    /// Carves a meandering river from a point on the edge of the map to the body of water (or two that join, for a
    /// confluence), trying several sources until one gives a course that does not touch any other water on the way.
    /// </summary>
    private static bool TryCarveRiver(GameMap map, TerrainType terrain, GameRandom rng, short[] ids, int riverId, WaterBody body,
        (int X, int Y) target, int sources)
    {
        double minimum = 0.3 * Math.Min(map.Width, 2.0 * map.Height);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var segments = new List<Segment>();
            if (sources == 1)
            {
                var source = RandomEdgePoint(map, rng, body);
                if (Distance(source, target) < minimum)
                {
                    continue;
                }

                segments.Add(MakeSegment(map, rng, source, target));
            }
            else
            {
                // Two rivers from well apart on the edge, meeting part of the way to the water and flowing on as one.
                var a = RandomEdgePoint(map, rng, body);
                var b = RandomEdgePoint(map, rng, body);
                if (Distance(a, b) < minimum)
                {
                    continue;
                }

                double mx = (a.X + b.X) / 2.0, my = (a.Y + b.Y) / 2.0;
                double frac = 0.45 + 0.2 * rng.NextDouble();
                var join = ((int)Math.Round(mx + (target.X - mx) * frac), (int)Math.Round(my + (target.Y - my) * frac));
                if (!map.InBounds(join.Item1, join.Item2) || Distance(join, target) < 0.25 * minimum ||
                    Distance(a, join) < 0.4 * minimum || Distance(b, join) < 0.4 * minimum)
                {
                    continue;
                }

                segments.Add(MakeSegment(map, rng, a, join));
                segments.Add(MakeSegment(map, rng, b, join));
                segments.Add(MakeSegment(map, rng, join, target));
            }

            if (segments.All(s => IsRiverClear(map, ids, s, riverId, body.Id)))
            {
                foreach (var segment in segments)
                {
                    Commit(map, terrain, ids, riverId, segment.Cells);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>The distance between two cells in visual units (a row is twice as tall as a column is wide).</summary>
    private static double Distance((int X, int Y) a, (int X, int Y) b)
    {
        double dx = a.X - b.X, dy = 2.0 * (a.Y - b.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (int X, int Y) RandomEdgePoint(GameMap map, GameRandom rng, WaterBody body)
    {
        // Not from the edge the body lies against: a river should flow towards it, across the land.
        for (int tries = 0; tries < 20; tries++)
        {
            var side = (MapSide)rng.Next(4);
            if (body.Side == side && body.Kind != WaterKind.InlandLake)
            {
                continue;
            }

            int margin = 6;
            return side switch
            {
                MapSide.North => (rng.Next(margin, map.Width - margin), 0),
                MapSide.South => (rng.Next(margin, map.Width - margin), map.Height - 1),
                MapSide.West => (0, rng.Next(margin, map.Height - margin)),
                _ => (map.Width - 1, rng.Next(margin, map.Height - margin)),
            };
        }

        return (0, 0);
    }

    private static Segment MakeSegment(GameMap map, GameRandom rng, (int X, int Y) from, (int X, int Y) to)
    {
        double length = Distance(from, to);
        double dx = to.X - from.X, dy = 2.0 * (to.Y - from.Y);
        var noise = new PerlinNoise(rng);
        double offset = rng.NextDouble() * 100;
        double amplitude = Math.Min(0.12 * length, 20);

        // Unit vector across the course, in visual units.
        double px = length > 0 ? -dy / length : 0, py = length > 0 ? dx / length : 0;
        int steps = Math.Max(2, (int)Math.Ceiling(length));
        var cells = new HashSet<(int X, int Y)>();
        var centers = new List<(int X, int Y, double Progress)>();

        for (int i = 0; i <= steps; i++)
        {
            double s = i / (double)steps;

            // The course wanders in the middle and runs straight at both ends, so it starts on the edge and meets the
            // water head on.
            double taper = Math.Min(1.0, Math.Min(s * 8, (1 - s) * 5));
            double sway = amplitude * taper * (noise.Fractal(s * length * 0.018, offset, 2) * 2 - 1) * 2;
            double vx = from.X + dx * s + px * sway;
            double vy = 2.0 * from.Y + dy * s + py * sway;
            int x = (int)Math.Round(vx), y = (int)Math.Round(vy / 2.0);
            centers.Add((x, y, s));

            // A stamp about five columns by three rows, widening and narrowing a little along the way.
            double ry = 1.0 + 0.5 * (noise.Noise(s * length * 0.05, offset + 50) > 0.2 ? 1 : 0);
            double rx = 2 * ry;
            for (int yy = (int)Math.Floor(y - ry); yy <= (int)Math.Ceiling(y + ry); yy++)
            {
                for (int xx = (int)Math.Floor(x - rx); xx <= (int)Math.Ceiling(x + rx); xx++)
                {
                    double ex = (xx - x) / rx, ey = (yy - y) / ry;
                    if (ex * ex + ey * ey <= 1.05 && map.InBounds(xx, yy))
                    {
                        cells.Add((xx, yy));
                    }
                }
            }
        }

        return new Segment(cells, centers);
    }

    private static bool IsRiverClear(GameMap map, short[] ids, Segment segment, int riverId, int targetId)
    {
        // The river must not run over other water...
        foreach (var (x, y) in segment.Cells)
        {
            int existing = ids[y * map.Width + x];
            if (existing != 0 && existing != targetId && existing != riverId)
            {
                return false;
            }
        }

        // ...nor pass close beside it, except where it arrives at the water it feeds.
        foreach (var (x, y, progress) in segment.Centers)
        {
            // A river crosses the land: it does not run along the edge of the map (it only starts and ends there).
            if (progress is > 0.12 and < 0.85 && Math.Min(Math.Min(x, map.Width - 1 - x), Math.Min(y, map.Height - 1 - y)) < 3)
            {
                return false;
            }

            for (int dy = -3; dy <= 3; dy++)
            {
                for (int dx = -6; dx <= 6; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!map.InBounds(nx, ny))
                    {
                        continue;
                    }

                    int existing = ids[ny * map.Width + nx];
                    if (existing != 0 && existing != riverId && !(existing == targetId && progress > 0.7))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
