using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;
using TermCity.Core.Util;

namespace TermCity.Core.Rendering;

/// <summary>
/// A smooth road drawn as one continuous curve. Coordinates are pixels at the normal (unzoomed) cell size, with the
/// origin at the top-left of map cell (0, 0).
/// </summary>
public sealed class RoadPath(RoadType type, bool connected, bool closed, double[] xs, double[] ys,
    IReadOnlyList<Pos>? sourceCells = null)
{
    public RoadType Type { get; } = type;

    /// <summary>Whether the road joins the edge of the map.</summary>
    public bool Connected { get; } = connected;

    public bool Closed { get; } = closed;
    public IReadOnlyList<Pos> SourceCells { get; } = sourceCells ?? [];

    public IReadOnlyList<double> Xs => xs;

    public IReadOnlyList<double> Ys => ys;

    public int Count => xs.Length;

    public double MinX { get; } = xs.Min();

    public double MaxX { get; } = xs.Max();

    public double MinY { get; } = ys.Min();

    public double MaxY { get; } = ys.Max();
}

/// <summary>
/// Turns the cells of every road, whatever its type, into flowing curves. The simulation still sees
/// the same four-connected cells; only the drawing of them is rounded off. Roads are traced as paths that run straight
/// through junctions, so a crossing or a T becomes two or more curves that the rasteriser then merges smoothly.
/// </summary>
public static class RoadCurves
{
    public const int CellWidth = 12;
    public const int CellHeight = 22;

    /// <summary>How far, in pixels, a bend is spread out: the half-width of the smoothing kernel.</summary>
    public const double SmoothingRadius = 56;

    /// <summary>
    /// How far, in pixels, the drawn road may stray from the cell centres it follows. A diagonal highway is laid as a
    /// staircase of cells; within this tolerance the steps are drawn as one straight line rather than a ripple.
    /// </summary>
    public const double StraightenTolerance = 24;

    public static bool Intersects(RoadPath path, CellRect area, int padding = 0) =>
        path.Xs.Min() / CellWidth <= area.Right + 1 + padding &&
        path.Xs.Max() / CellWidth >= area.X - padding &&
        path.Ys.Min() / CellHeight <= area.Bottom + 1 + padding &&
        path.Ys.Max() / CellHeight >= area.Y - padding;

    private const double SampleSpacing = 3;

    private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    public static IReadOnlyList<RoadPath> Extract(GameMap map, Func<int, int, bool> isConnected,
        IReadOnlyDictionary<int, RoadType>? planned = null)
    {
        var smooth = new Dictionary<int, RoadType>();
        foreach (int i in map.RoadCells)
        {
            if (map.RoadTypeAt(i % map.Width, i / map.Width) is { } type)
            {
                smooth[i] = type;
            }
        }

        if (planned is not null)
            foreach (var (index, type) in planned) smooth[index] = type;

        if (smooth.Count == 0)
        {
            return [];
        }

        int width = map.Width;
        var masks = new Dictionary<int, int>(smooth.Count);
        foreach (int i in smooth.Keys)
        {
            int x = i % width, y = i / width, mask = 0;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + Dirs[d].Dx, ny = y + Dirs[d].Dy;
                if (map.InBounds(nx, ny) && smooth.ContainsKey(ny * width + nx))
                {
                    mask |= 1 << d;
                }
            }

            masks[i] = mask;
        }

        // At a junction the roads of highest rank carry on through it, and the humbler ones branch off them.
        var partners = new Dictionary<int, int[]>();
        foreach (var (cell, mask) in masks)
        {
            if (System.Numerics.BitOperations.PopCount((uint)mask) >= 3)
            {
                partners[cell] = Pair(smooth, width, cell, mask);
            }
        }

        int PartnerOf(int cell, int arm)
        {
            int mask = masks[cell];
            return partners.TryGetValue(cell, out var pairs) ? pairs[arm] : Partner(mask, arm);
        }

        // An edge joins two adjacent cells; it is named by the cell to its north or west, and which of the two it is.
        int EdgeKey(int cell, int dir) => dir switch
        {
            1 => cell * 2,
            2 => cell * 2 + 1,
            3 => (cell - 1) * 2,
            _ => (cell - width) * 2 + 1,
        };

        int Neighbour(int cell, int dir) => cell + Dirs[dir].Dx + Dirs[dir].Dy * width;

        var visited = new HashSet<int>();
        var drafts = new List<DraftPath>();

        void Walk(int start, int startArm)
        {
            var cells = new List<int> { start };
            visited.Add(EdgeKey(start, startArm));
            int cur = Neighbour(start, startArm);
            int from = (startArm + 2) % 4;
            bool closed = false;
            cells.Add(cur);
            while (true)
            {
                int next = PartnerOf(cur, from);
                if (next < 0)
                {
                    break;
                }

                int edge = EdgeKey(cur, next);
                if (!visited.Add(edge))
                {
                    break;
                }

                cur = Neighbour(cur, next);
                from = (next + 2) % 4;
                cells.Add(cur);
                if (cur == start && PartnerOf(cur, from) == startArm)
                {
                    closed = true;
                    break;
                }
            }

            if (closed)
            {
                cells.RemoveAt(cells.Count - 1);
            }

            foreach (var (run, runType, runClosed) in SplitByType(cells, closed, smooth, c => Junction(masks[c])))
            {
                drafts.Add(Draft(map, runType, masks, run, runClosed, isConnected));
            }
        }

        // Open paths begin at a dead end, or where a branch meets a road it cannot continue straight into.
        foreach (int cell in smooth.Keys.OrderBy(c => c))
        {
            int mask = masks[cell];
            if (mask == 0)
            {
                drafts.Add(Draft(map, smooth[cell], masks, [cell], false, isConnected));
                continue;
            }

            for (int arm = 0; arm < 4; arm++)
            {
                if ((mask & (1 << arm)) != 0 && PartnerOf(cell, arm) < 0 && !visited.Contains(EdgeKey(cell, arm)))
                {
                    Walk(cell, arm);
                }
            }
        }

        // What is left is rings.
        foreach (int cell in smooth.Keys.OrderBy(c => c))
        {
            int mask = masks[cell];
            for (int arm = 0; arm < 4; arm++)
            {
                if ((mask & (1 << arm)) != 0 && !visited.Contains(EdgeKey(cell, arm)))
                {
                    Walk(cell, arm);
                }
            }
        }

        Join(drafts);
        return drafts.Select(d => new RoadPath(d.Type, d.Connected, d.Closed, d.Xs, d.Ys,
            d.Cells.Select(map.PosOf).ToArray())).ToList();
    }

    /// <summary>
    /// Cuts a path where the road type changes, so each stretch is drawn in its own colours. Neighbouring stretches
    /// overlap by a cell, which hides the seam.
    /// </summary>
    private static List<(List<int> Cells, RoadType Type, bool Closed)> SplitByType(
        List<int> cells, bool closed, Dictionary<int, RoadType> raw, Func<int, bool> isJunction)
    {
        // A junction cell belongs to every road meeting there, so on this path it takes the type of the cell beside it.
        var types = new Dictionary<int, RoadType>();
        for (int k = 0; k < cells.Count; k++)
        {
            int beside = k > 0 ? cells[k - 1] : closed ? cells[^1] : cells[Math.Min(1, cells.Count - 1)];
            types[cells[k]] = isJunction(cells[k]) ? raw[beside] : raw[cells[k]];
        }

        if (closed)
        {
            int turn = cells.FindIndex(c => types[c] != types[cells[(cells.IndexOf(c) + cells.Count - 1) % cells.Count]]);
            if (turn < 0)
            {
                return [(cells, types[cells[0]], true)];
            }

            cells = [.. cells.Skip(turn), .. cells.Take(turn)];
            cells.Add(cells[0]);
        }

        var runs = new List<(List<int>, RoadType, bool)>();
        int start = 0;
        var type = types[cells[0]];
        for (int k = 1; k < cells.Count; k++)
        {
            if (types[cells[k]] != types[cells[k - 1]])
            {
                runs.Add((cells.GetRange(start, k - start + 1), type, false));
                start = k - 1;
                type = types[cells[k]];
            }
        }

        runs.Add((cells.GetRange(start, cells.Count - start), type, false));
        return runs;
    }

    /// <summary>The arm a road carries on into when it arrives along <paramref name="arm"/>, or -1 if it ends there.</summary>
    private static int Partner(int mask, int arm)
    {
        int count = System.Numerics.BitOperations.PopCount((uint)mask);
        if (count == 2)
        {
            return (mask & ~(1 << arm)) switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
        }

        int opposite = (arm + 2) % 4;
        return count >= 3 && (mask & (1 << opposite)) != 0 ? opposite : -1;
    }

    private sealed class DraftPath(RoadType type, bool connected, bool closed, List<int> cells, List<(double X, double Y)> corners, bool startJunction, bool endJunction)
    {
        public RoadType Type { get; } = type;

        public bool Connected { get; } = connected;

        public bool Closed { get; } = closed;

        public List<int> Cells { get; } = cells;

        public List<(double X, double Y)> Corners { get; } = corners;

        public bool StartJunction { get; } = startJunction;

        public bool EndJunction { get; } = endJunction;

        public double[] Xs { get; set; } = [];

        public double[] Ys { get; set; } = [];

        /// <summary>The curve as first drawn, before its ends were bent to meet other roads.</summary>
        public double[] BaseXs { get; set; } = [];

        public double[] BaseYs { get; set; } = [];
    }

    private static DraftPath Draft(
        GameMap map, RoadType type, Dictionary<int, int> masks, List<int> cells, bool closed,
        Func<int, int, bool> isConnected)
    {
        int width = map.Width;
        var corners = new List<(double X, double Y)>();
        foreach (int c in cells)
        {
            corners.Add((c % width * CellWidth + CellWidth / 2.0, c / width * CellHeight + CellHeight / 2.0));
        }

        if (!closed)
        {
            // A dead end runs on to the edge of its cell.
            corners[0] = Extend(map, corners, 0, masks[cells[0]]);
            corners[^1] = Extend(map, corners, corners.Count - 1, masks[cells[^1]]);
            corners = Straighten(corners, StraightRunPins(corners, type));
        }
        else
        {
            var ring = corners.Append(corners[0]).ToList();
            var straightened = Straighten(ring, StraightRunPins(ring, type));
            if (straightened.Count >= 4)
                corners = straightened.Take(straightened.Count - 1).ToList();
        }

        corners = AlignToCells(corners, closed, type);
        int first = cells[0];
        var draft = new DraftPath(
            type, isConnected(first % width, first / width), closed, cells, corners,
            !closed && Junction(masks[cells[0]]), !closed && Junction(masks[cells[^1]]));
        (draft.Xs, draft.Ys) = SmoothRoad(corners, closed, type);
        draft.BaseXs = draft.Xs;
        draft.BaseYs = draft.Ys;
        return draft;
    }

    /// <summary>
    /// Where a road ends on another, the end is moved onto the other road's drawn curve (which need not pass through the
    /// middle of the junction cell). A branch that meets it at a slant curves in alongside it like a slip road; one that
    /// meets it squarely reaches the host centreline and lets the blend form the T.
    /// </summary>
    private static void Join(List<DraftPath> drafts)
    {
        var through = new Dictionary<int, List<DraftPath>>();
        foreach (var d in drafts)
        {
            int from = d.Closed ? 0 : 1, to = d.Closed ? d.Cells.Count : d.Cells.Count - 1;
            for (int i = from; i < to; i++)
            {
                if (!through.TryGetValue(d.Cells[i], out var list))
                {
                    through[d.Cells[i]] = list = [];
                }

                list.Add(d);
            }
        }

        foreach (var d in drafts)
        {
            if (d.Closed || (!d.StartJunction && !d.EndJunction))
            {
                continue;
            }

            if (d.EndJunction)
            {
                JoinEnd(d, d.Cells[^1], through, atStart: false);
            }

            if (d.StartJunction)
            {
                JoinEnd(d, d.Cells[0], through, atStart: true);
            }
        }
    }

    private static void JoinEnd(DraftPath d, int cell, Dictionary<int, List<DraftPath>> through, bool atStart)
    {
        DraftPath? host = null;
        if (through.TryGetValue(cell, out var candidates))
        {
            foreach (var c in candidates)
            {
                if (!ReferenceEquals(c, d) && (host is null || c.Type.Rank > host.Type.Rank))
                {
                    host = c;
                }
            }
        }

        var end = atStart ? d.Corners[0] : d.Corners[^1];
        if (host is null || host.BaseXs.Length < 2)
        {
            Settle(d, atStart, null);
            return;
        }

        var hit = Nearest(host.BaseXs, host.BaseYs, end);
        var neighbor = d.Corners.Count > 1 ? d.Corners[atStart ? 1 : ^2] : end;
        bool distinct = Dist(end, neighbor) > 1e-9;
        bool vertical = distinct && Math.Abs(end.X - neighbor.X) < 1e-9;
        bool horizontal = distinct && Math.Abs(end.Y - neighbor.Y) < 1e-9;
        bool connector = false;
        if (vertical || horizontal)
        {
            var aligned = AxisMeeting(host.BaseXs, host.BaseYs, end, vertical);
            if (aligned is { } meeting) hit = meeting;
            else connector = true;
        }
        Settle(d, atStart, Dist((hit.X, hit.Y), end) <= 4 * CellHeight ?
            new Meeting(hit.X, hit.Y, hit.Tx, hit.Ty, host.BaseXs, host.BaseYs, hit.Segment) : null, connector);
    }

    private static (double X, double Y, double Tx, double Ty, int Segment)? AxisMeeting(
        double[] xs, double[] ys, (double X, double Y) from, bool vertical)
    {
        (double X, double Y, double Tx, double Ty, int Segment)? result = null;
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < xs.Length; i++)
        {
            double dx = xs[i + 1] - xs[i], dy = ys[i + 1] - ys[i];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) continue;
            double cross = vertical ? dx : dy;
            double delta = vertical ? from.X - xs[i] : from.Y - ys[i];
            double t;
            if (Math.Abs(cross) < 1e-9)
            {
                if (Math.Abs(delta) > 1e-9) continue;
                t = Math.Clamp(vertical ? (from.Y - ys[i]) / dy : (from.X - xs[i]) / dx, 0, 1);
            }
            else
            {
                t = delta / cross;
                if (t < -1e-9 || t > 1 + 1e-9) continue;
                t = Math.Clamp(t, 0, 1);
            }
            double x = vertical ? from.X : xs[i] + dx * t;
            double y = vertical ? ys[i] + dy * t : from.Y;
            double distance = Sq((x - from.X) / CellWidth) + Sq((y - from.Y) / CellHeight);
            if (distance >= best) continue;
            best = distance;
            result = (x, y, dx / length, dy / length, i);
        }
        return result;
    }

    private sealed record Meeting(double X, double Y, double Tx, double Ty, double[] HostXs, double[] HostYs, int Segment);

    private static void Settle(DraftPath d, bool atStart, Meeting? at, bool connector = false)
    {
        var xs = d.Xs;
        var ys = d.Ys;
        if (at is not null)
        {
            var corners = new List<(double X, double Y)>(d.Corners);
            if (connector)
            {
                var end = corners[atStart ? 0 : ^1];
                var neighbor = corners[atStart ? 1 : ^2];
                double dx = (neighbor.X - end.X) / CellWidth, dy = (neighbor.Y - end.Y) / CellHeight;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double fraction = Math.Min(1, length / 2) / length;
                var approach = (end.X + (neighbor.X - end.X) * fraction,
                    end.Y + (neighbor.Y - end.Y) * fraction);
                corners.Insert(atStart ? 1 : corners.Count - 1, approach);
            }
            corners[atStart ? 0 : ^1] = (at.X, at.Y);
            (xs, ys) = SmoothRoad(corners, false, d.Type);
            d.Corners.Clear();
            d.Corners.AddRange(corners);
        }

        if (atStart)
        {
            xs = xs.Reverse().ToArray();
            ys = ys.Reverse().ToArray();
        }

        if (atStart)
        {
            xs = xs.Reverse().ToArray();
            ys = ys.Reverse().ToArray();
        }

        d.Xs = xs;
        d.Ys = ys;
    }

    /// <summary>The point on a polyline nearest to <paramref name="from"/>, with the unit direction of the segment it lies on.</summary>
    private static (double X, double Y, double Tx, double Ty, int Segment) Nearest(double[] xs, double[] ys, (double X, double Y) from)
    {
        double best = double.PositiveInfinity;
        (double X, double Y, double Tx, double Ty, int Segment) result = (xs[0], ys[0], 1, 0, 0);
        for (int i = 0; i + 1 < xs.Length; i++)
        {
            double ex = xs[i + 1] - xs[i], ey = ys[i + 1] - ys[i], len2 = ex * ex + ey * ey;
            if (len2 < 1e-12)
            {
                continue;
            }

            double t = Math.Clamp(((from.X - xs[i]) * ex + (from.Y - ys[i]) * ey) / len2, 0, 1);
            double qx = xs[i] + ex * t, qy = ys[i] + ey * t, d = Sq(from.X - qx) + Sq(from.Y - qy);
            if (d < best)
            {
                best = d;
                double len = Math.Sqrt(len2);
                result = (qx, qy, ex / len, ey / len, i);
            }
        }

        return result;
    }

    /// <summary>
    /// Pairs up the arms of a junction cell into the roads that run through it: the highest-ranked roads are kept whole,
    /// and among equals a straight run beats a bend. An odd arm out is a branch ending on it. Unpaired arms are -1.
    /// </summary>
    private static int[] Pair(Dictionary<int, RoadType> types, int width, int cell, int mask)
    {
        var pairs = new[] { -1, -1, -1, -1 };
        var arms = Enumerable.Range(0, 4).Where(a => (mask & (1 << a)) != 0).ToList();
        int Rank(int arm)
        {
            int n = cell + Dirs[arm].Dx + Dirs[arm].Dy * width;
            return types.TryGetValue(n, out var t) ? t.Rank : 0;
        }

        int Score(int a, int b) => Math.Min(Rank(a), Rank(b)) * 4 + ((a + 2) % 4 == b ? 1 : 0);

        var best = new List<(int, int)>();
        int bestScore = int.MinValue;
        void Consider(List<(int, int)> matching)
        {
            int score = matching.Sum(m => Score(m.Item1, m.Item2));
            if (score > bestScore)
            {
                bestScore = score;
                best = matching;
            }
        }

        if (arms.Count == 4)
        {
            Consider([(0, 2), (1, 3)]);
            Consider([(0, 1), (2, 3)]);
            Consider([(0, 3), (1, 2)]);
        }
        else
        {
            foreach (int a in arms)
            {
                foreach (int b in arms.Where(b => b > a))
                {
                    Consider([(a, b)]);
                }
            }
        }

        if (best.Count == 2 && (best[0].Item1 + 2) % 4 != best[0].Item2)
        {
            // Opposing rounded bends pull apart; join the other arms onto the main curve as branches instead.
            best = best.OrderByDescending(m => Score(m.Item1, m.Item2)).Take(1).ToList();
        }

        foreach (var (a, b) in best)
        {
            pairs[a] = b;
            pairs[b] = a;
        }

        return pairs;
    }

    /// <summary>
    /// Replaces runs of staircase steps with straight lines (Douglas-Peucker). Junction cells are kept as vertices so
    /// that a road still passes exactly through the place another joins it.
    /// </summary>
    internal static List<(double X, double Y)> Straighten(List<(double X, double Y)> points, IReadOnlyList<bool> pinned,
        double tolerance = StraightenTolerance)
    {
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        for (int i = 0; i < points.Count; i++)
        {
            keep[i] |= pinned[i];
        }

        int from = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (keep[i])
            {
                Simplify(points, keep, from, i, tolerance);
                from = i;
            }
        }

        return points.Where((_, i) => keep[i]).ToList();
    }

    private static void Simplify(List<(double X, double Y)> points, bool[] keep, int first, int last, double tolerance)
    {
        if (last - first < 2)
        {
            return;
        }

        var a = points[first];
        var b = points[last];
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        int worst = -1;
        double worstDistance = tolerance;
        for (int i = first + 1; i < last; i++)
        {
            double distance = length < 1e-9
                ? Dist(points[i], a)
                : Math.Abs(dy * (points[i].X - a.X) - dx * (points[i].Y - a.Y)) / length;
            if (distance > worstDistance)
            {
                worstDistance = distance;
                worst = i;
            }
        }

        if (worst >= 0)
        {
            keep[worst] = true;
            Simplify(points, keep, first, worst, tolerance);
            Simplify(points, keep, worst, last, tolerance);
        }
    }

    private static bool Junction(int mask) => System.Numerics.BitOperations.PopCount((uint)mask) >= 3;

    /// <summary>Shortens an open polyline by <paramref name="amount"/> of its length from one end (never to less than a point).</summary>
    internal static (double[] Xs, double[] Ys) Trim(double[] xs, double[] ys, bool atStart, double amount)
    {
        var px = atStart ? xs.Reverse().ToArray() : xs;
        var py = atStart ? ys.Reverse().ToArray() : ys;
        int last = px.Length - 1;
        double left = amount;
        while (last > 0)
        {
            double step = Math.Sqrt(Sq(px[last] - px[last - 1]) + Sq(py[last] - py[last - 1]));
            if (step >= left)
            {
                double t = step < 1e-9 ? 0 : left / step;
                var nx = px.Take(last).Append(px[last] + (px[last - 1] - px[last]) * t).ToArray();
                var ny = py.Take(last).Append(py[last] + (py[last - 1] - py[last]) * t).ToArray();
                return atStart ? (nx.Reverse().ToArray(), ny.Reverse().ToArray()) : (nx, ny);
            }

            left -= step;
            last--;
        }

        return (xs.Take(1).ToArray(), ys.Take(1).ToArray());
    }

    private static (double X, double Y) Extend(GameMap map, List<(double X, double Y)> corners, int end, int mask)
    {
        var point = corners[end];
        if (System.Numerics.BitOperations.PopCount((uint)mask) > 1 || corners.Count < 2)
        {
            return point;
        }

        var inner = corners[end == 0 ? 1 : end - 1];
        int directionX = Math.Sign(point.X - inner.X), directionY = Math.Sign(point.Y - inner.Y);
        int cellX = (int)Math.Floor(point.X / CellWidth), cellY = (int)Math.Floor(point.Y / CellHeight);
        int nextX = cellX + directionX, nextY = cellY + directionY;
        if (map.TerrainAt(cellX, cellY).Buildable && map.InBounds(nextX, nextY) && !map.TerrainAt(nextX, nextY).Buildable)
            return point;
        double dx = directionX * CellWidth / 2.0, dy = directionY * CellHeight / 2.0;
        return (point.X + dx, point.Y + dy);
    }

    private static List<(double X, double Y)> AlignToCells(List<(double X, double Y)> corners,
        bool closed, RoadType type)
    {
        if (type.WidthCells % 2 != 0) return corners;
        var world = corners.Select(p => (X: p.X / CellWidth, Y: p.Y / CellHeight)).ToArray();
        var first = world.Length > 1 ? (X: world[1].X - world[0].X, Y: world[1].Y - world[0].Y) : (X: 1.0, Y: 0.0);
        double side = Math.Abs(first.X) >= Math.Abs(first.Y) ? Math.Sign(first.X) : -Math.Sign(first.Y);
        var result = new List<(double X, double Y)>(world.Length);
        for (int i = 0; i < world.Length; i++)
        {
            var before = world[closed ? (i + world.Length - 1) % world.Length : Math.Max(0, i - 1)];
            var after = world[closed ? (i + 1) % world.Length : Math.Min(world.Length - 1, i + 1)];
            double ix = world[i].X - before.X, iy = world[i].Y - before.Y;
            double ox = after.X - world[i].X, oy = after.Y - world[i].Y;
            double incoming = Math.Sqrt(ix * ix + iy * iy), outgoing = Math.Sqrt(ox * ox + oy * oy);
            if (incoming < 1e-9) { ix = outgoing > 1e-9 ? ox : 1; iy = oy; incoming = outgoing > 1e-9 ? outgoing : 1; }
            if (outgoing < 1e-9) { ox = ix; oy = iy; outgoing = incoming; }
            ix /= incoming; iy /= incoming; ox /= outgoing; oy /= outgoing;
            double offset = side * 0.5 / Math.Max(0.5, 1 + ix * ox + iy * oy);
            result.Add(((world[i].X - (iy + oy) * offset) * CellWidth,
                (world[i].Y + (ix + ox) * offset) * CellHeight));
        }
        return result;
    }

    private static bool[] StraightRunPins(IReadOnlyList<(double X, double Y)> corners, RoadType type)
    {
        var pinned = new bool[corners.Count];
        double minimum = Math.Max(2 * type.BendRadiusCells, 2 * StraightenTolerance / CellWidth);
        var runs = new List<(int Start, int End, bool Vertical, double Length)>();
        for (int start = 0; start + 1 < corners.Count;)
        {
            var first = corners[start];
            var next = corners[start + 1];
            bool vertical = first.X == next.X;
            int end = start + 1;
            while (end + 1 < corners.Count &&
                (vertical ? corners[end + 1].X == first.X : corners[end + 1].Y == first.Y))
                end++;
            double length = vertical ? Math.Abs(corners[end].Y - first.Y) / CellHeight :
                Math.Abs(corners[end].X - first.X) / CellWidth;
            runs.Add((start, end, vertical, length));
            start = end;
        }
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            int Direction((int Start, int End, bool Vertical, double Length) value) =>
                Math.Sign(value.Vertical ? corners[value.End].Y - corners[value.Start].Y :
                    corners[value.End].X - corners[value.Start].X);
            bool StaircaseStep(int direction)
            {
                int next = i + direction * 2, step = i + direction;
                return next >= 0 && next < runs.Count && runs[step].Length <= 2 &&
                    runs[next].Vertical == run.Vertical && Direction(run) == Direction(runs[next]);
            }
            bool StaircaseNeighbor(int direction) =>
                StaircaseStep(direction) && runs[i + direction * 2].Length >= run.Length / 2;
            bool recurring = StaircaseStep(-1) && StaircaseStep(1) &&
                Direction(runs[i - 1]) == Direction(runs[i + 1]);
            // Repeated shallow staircase treads may straighten; a substantially longer actual leg must not drift.
            if (run.Length >= minimum && !recurring && !StaircaseNeighbor(-1) && !StaircaseNeighbor(1))
                pinned[run.Start] = pinned[run.End] = true;
        }
        return pinned;
    }

    private static (double[] Xs, double[] Ys) SmoothRoad(IReadOnlyList<(double X, double Y)> corners,
        bool closed, RoadType type)
    {
        if (type.BendRadiusCells <= 0) return Smooth(corners, closed);
        var square = corners.Select(p => (p.X, p.Y * CellWidth / CellHeight)).ToArray();
        var rounded = new List<(double X, double Y)>();
        for (int i = 0; i < square.Length; i++)
        {
            var corner = square[i];
            if (square.Length < 3 || !closed && (i == 0 || i == square.Length - 1))
            {
                rounded.Add(corner);
                continue;
            }
            var before = square[(i + square.Length - 1) % square.Length];
            var after = square[(i + 1) % square.Length];
            double beforeLength = Dist(before, corner), afterLength = Dist(corner, after);
            if (beforeLength < 1e-9 || afterLength < 1e-9) { rounded.Add(corner); continue; }
            double ix = (corner.Item1 - before.Item1) / beforeLength, iy = (corner.Item2 - before.Item2) / beforeLength;
            double ox = (after.Item1 - corner.Item1) / afterLength, oy = (after.Item2 - corner.Item2) / afterLength;
            double cross = ix * oy - iy * ox;
            double angle = Math.Acos(Math.Clamp(ix * ox + iy * oy, -1, 1));
            if (Math.Abs(cross) < 1e-9 || angle < 1e-6) { rounded.Add(corner); continue; }
            double tangent = Math.Tan(angle / 2);
            double lead = Math.Min(type.BendRadiusCells * CellWidth * tangent, Math.Min(beforeLength, afterLength) * 0.45);
            double radius = lead / tangent, sign = Math.Sign(cross);
            var enter = (X: corner.Item1 - ix * lead, Y: corner.Item2 - iy * lead);
            var exit = (X: corner.Item1 + ox * lead, Y: corner.Item2 + oy * lead);
            var center = (X: enter.X - iy * sign * radius, Y: enter.Y + ix * sign * radius);
            double start = Math.Atan2(enter.Y - center.Y, enter.X - center.X);
            int steps = Math.Max(2, (int)Math.Ceiling(radius * angle / SampleSpacing));
            rounded.Add(enter);
            for (int step = 1; step < steps; step++)
            {
                double at = start + sign * angle * step / steps;
                rounded.Add((center.X + Math.Cos(at) * radius, center.Y + Math.Sin(at) * radius));
            }
            rounded.Add(exit);
        }
        var sampled = new List<(double X, double Y)> { rounded[0] };
        int legs = closed ? rounded.Count : rounded.Count - 1;
        for (int i = 0; i < legs; i++)
        {
            var a = rounded[i]; var b = rounded[(i + 1) % rounded.Count];
            int steps = Math.Max(1, (int)Math.Ceiling(Dist(a, b) / SampleSpacing));
            for (int step = 1; step <= steps; step++)
            {
                if (closed && i == legs - 1 && step == steps) continue;
                double t = step / (double)steps;
                sampled.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }
        return (sampled.Select(p => p.X).ToArray(), sampled.Select(p => p.Y * CellHeight / CellWidth).ToArray());
    }

    internal static (double[] Xs, double[] Ys) Smooth(IReadOnlyList<(double X, double Y)> corners, bool closed,
        double radius = SmoothingRadius)
    {
        var pts = new List<(double X, double Y)>(corners);
        if (closed)
        {
            pts.Add(pts[0]);
        }

        double length = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            length += Math.Sqrt(Sq(pts[i].X - pts[i - 1].X) + Sq(pts[i].Y - pts[i - 1].Y));
        }

        if (length < 1e-9)
        {
            return ([pts[0].X], [pts[0].Y]);
        }

        int n = Math.Max(2, (int)Math.Ceiling(length / SampleSpacing));
        var px = new double[n + 1];
        var py = new double[n + 1];
        int segment = 0;
        double walked = 0, segmentLength = Dist(pts[0], pts[1]);
        for (int i = 0; i <= n; i++)
        {
            double target = length * i / n;
            while (segment < pts.Count - 2 && walked + segmentLength < target - 1e-9)
            {
                walked += segmentLength;
                segment++;
                segmentLength = Dist(pts[segment], pts[segment + 1]);
            }

            double t = segmentLength < 1e-9 ? 0 : Math.Clamp((target - walked) / segmentLength, 0, 1);
            px[i] = pts[segment].X + (pts[segment + 1].X - pts[segment].X) * t;
            py[i] = pts[segment].Y + (pts[segment + 1].Y - pts[segment].Y) * t;
        }

        int half = Math.Max(1, (int)Math.Round(radius / (length / n)));
        int count = closed ? n : n + 1;
        var xs = new double[count];
        var ys = new double[count];
        for (int i = 0; i < count; i++)
        {
            double sx = 0, sy = 0, total = 0;
            for (int j = -half + 1; j < half; j++)
            {
                double w = half - Math.Abs(j);
                var (x, y) = closed ? Wrapped(px, py, n, i + j) : Reflected(px, py, n, i + j);
                sx += w * x;
                sy += w * y;
                total += w;
            }

            xs[i] = sx / total;
            ys[i] = sy / total;
        }

        return (xs, ys);
    }

    private static (double X, double Y) Wrapped(double[] x, double[] y, int n, int i)
    {
        i = (i % n + n) % n;
        return (x[i], y[i]);
    }

    /// <summary>Sample <paramref name="i"/> of an open polyline, continued past either end by turning it half way round the end point.</summary>
    private static (double X, double Y) Reflected(double[] x, double[] y, int n, int i)
    {
        if (i < 0)
        {
            var (rx, ry) = Reflected(x, y, n, -i);
            return (2 * x[0] - rx, 2 * y[0] - ry);
        }

        if (i > n)
        {
            var (rx, ry) = Reflected(x, y, n, 2 * n - i);
            return (2 * x[n] - rx, 2 * y[n] - ry);
        }

        return (x[i], y[i]);
    }

    private static double Sq(double v) => v * v;

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y));
}
