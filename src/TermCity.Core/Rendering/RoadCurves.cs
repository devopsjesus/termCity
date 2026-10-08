using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>
/// A smooth road drawn as one continuous curve. Coordinates are pixels at the normal (unzoomed) cell size, with the
/// origin at the top-left of map cell (0, 0).
/// </summary>
public sealed class RoadPath(RoadType type, bool connected, bool closed, double[] xs, double[] ys)
{
    public RoadType Type { get; } = type;

    /// <summary>Whether the road joins the edge of the map (drawn in red when it does not).</summary>
    public bool Connected { get; } = connected;

    public bool Closed { get; } = closed;

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

    /// <summary>How far, in pixels, a branch curves in toward the road it joins, and how far along that road it merges.</summary>
    public const double RampLength = 40;

    /// <summary>A branch within this many degrees of square to the road it joins meets it square, like a plain T.</summary>
    private const double SquareSine = 0.97;

    private const double SampleSpacing = 3;

    private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    public static IReadOnlyList<RoadPath> Extract(GameMap map, Func<int, int, bool> isConnected)
    {
        var smooth = new Dictionary<int, RoadType>();
        foreach (int i in map.RoadCells)
        {
            if (map.RoadTypeAt(i % map.Width, i / map.Width) is { } type)
            {
                smooth[i] = type;
            }
        }

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
        return drafts.Select(d => new RoadPath(d.Type, d.Connected, d.Closed, d.Xs, d.Ys)).ToList();
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
            corners[0] = Extend(corners, 0, masks[cells[0]]);
            corners[^1] = Extend(corners, corners.Count - 1, masks[cells[^1]]);
            corners = Straighten(corners, new bool[corners.Count]);
        }

        int first = cells[0];
        var draft = new DraftPath(
            type, isConnected(first % width, first / width), closed, cells, corners,
            !closed && Junction(masks[cells[0]]), !closed && Junction(masks[cells[^1]]));
        (draft.Xs, draft.Ys) = Smooth(corners, closed);
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
        Settle(d, atStart, Dist((hit.X, hit.Y), end) <= 4 * CellHeight ? new Meeting(hit.X, hit.Y, hit.Tx, hit.Ty, host.BaseXs, host.BaseYs, hit.Segment) : null);
    }

    private sealed record Meeting(double X, double Y, double Tx, double Ty, double[] HostXs, double[] HostYs, int Segment);

    private static void Settle(DraftPath d, bool atStart, Meeting? at)
    {
        var xs = d.Xs;
        var ys = d.Ys;
        if (at is not null)
        {
            var corners = new List<(double X, double Y)>(d.Corners);
            corners[atStart ? 0 : ^1] = (at.X, at.Y);
            (xs, ys) = Smooth(corners, false);
        }

        if (atStart)
        {
            xs = xs.Reverse().ToArray();
            ys = ys.Reverse().ToArray();
        }

        if (at is not null)
        {
            (xs, ys) = Ramp(xs, ys, at);
        }

        if (atStart)
        {
            xs = xs.Reverse().ToArray();
            ys = ys.Reverse().ToArray();
        }

        d.Xs = xs;
        d.Ys = ys;
        if (at is not null)
        {
            // The other end of the same road may still be joined; keep the corners that match this curve.
            d.Corners[atStart ? 0 : ^1] = (at.X, at.Y);
        }
    }

    /// <summary>
    /// Bends the last stretch of a curve ending on a road into a quadratic curve that runs into that road's direction,
    /// merging with it some way along, unless the two already meet at nearly a right angle.
    /// </summary>
    private static (double[] Xs, double[] Ys) Ramp(double[] xs, double[] ys, Meeting hit)
    {
        int n = xs.Length;
        if (n < 4)
        {
            return (xs, ys);
        }

        double length = 0;
        for (int i = 1; i < n; i++)
        {
            length += Dist((xs[i], ys[i]), (xs[i - 1], ys[i - 1]));
        }

        double reach = Math.Min(RampLength, length * 0.6);
        double walked = 0;
        int k = n - 1;
        while (k > 1 && walked < reach)
        {
            walked += Dist((xs[k], ys[k]), (xs[k - 1], ys[k - 1]));
            k--;
        }

        int ahead = Math.Min(n - 1, k + 2), behind = Math.Max(0, k - 2);
        double dx = xs[ahead] - xs[behind], dy = ys[ahead] - ys[behind], dl = Math.Sqrt(dx * dx + dy * dy);
        if (dl < 1e-9)
        {
            return (xs, ys);
        }

        dx /= dl;
        dy /= dl;
        double tx = hit.Tx, ty = hit.Ty;
        double dot = dx * tx + dy * ty;
        if (Math.Abs(dx * ty - dy * tx) >= SquareSine || Math.Abs(dot) < 1e-3)
        {
            return (xs, ys);
        }

        if (dot < 0)
        {
            tx = -tx;
            ty = -ty;
        }

        var start = (X: xs[k], Y: ys[k]);
        var corner = (hit.X, hit.Y);
        var finish = AlongHost(hit, forward: tx * (hit.HostXs[hit.Segment + 1] - hit.HostXs[hit.Segment]) + ty * (hit.HostYs[hit.Segment + 1] - hit.HostYs[hit.Segment]) > 0);
        double arc = walked + RampLength;
        int steps = Math.Max(4, (int)Math.Ceiling(arc / SampleSpacing));
        var nx = new List<double>(xs.Take(k + 1));
        var ny = new List<double>(ys.Take(k + 1));
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            nx.Add(u * u * start.X + 2 * u * t * corner.Item1 + t * t * finish.X);
            ny.Add(u * u * start.Y + 2 * u * t * corner.Item2 + t * t * finish.Y);
        }

        return (nx.ToArray(), ny.ToArray());
    }

    /// <summary>The point <see cref="RampLength"/> along the road being joined, from where the ramp meets it.</summary>
    private static (double X, double Y) AlongHost(Meeting hit, bool forward)
    {
        var xs = hit.HostXs;
        var ys = hit.HostYs;
        double left = RampLength;
        double x = hit.X, y = hit.Y;
        int i = forward ? hit.Segment + 1 : hit.Segment;
        int step = forward ? 1 : -1;
        while (i >= 0 && i < xs.Length)
        {
            double d = Dist((x, y), (xs[i], ys[i]));
            if (d >= left)
            {
                double t = left / d;
                return (x + (xs[i] - x) * t, y + (ys[i] - y) * t);
            }

            left -= d;
            (x, y) = (xs[i], ys[i]);
            i += step;
        }

        return (x, y);
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
    internal static List<(double X, double Y)> Straighten(List<(double X, double Y)> points, IReadOnlyList<bool> pinned)
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
                Simplify(points, keep, from, i);
                from = i;
            }
        }

        return points.Where((_, i) => keep[i]).ToList();
    }

    private static void Simplify(List<(double X, double Y)> points, bool[] keep, int first, int last)
    {
        if (last - first < 2)
        {
            return;
        }

        var a = points[first];
        var b = points[last];
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        int worst = -1;
        double worstDistance = StraightenTolerance;
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
            Simplify(points, keep, first, worst);
            Simplify(points, keep, worst, last);
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

    private static (double X, double Y) Extend(List<(double X, double Y)> corners, int end, int mask)
    {
        var point = corners[end];
        if (System.Numerics.BitOperations.PopCount((uint)mask) > 1 || corners.Count < 2)
        {
            return point;
        }

        var inner = corners[end == 0 ? 1 : end - 1];
        double dx = Math.Sign(point.X - inner.X) * CellWidth / 2.0, dy = Math.Sign(point.Y - inner.Y) * CellHeight / 2.0;
        return (point.X + dx, point.Y + dy);
    }

    /// <summary>Resamples a polyline evenly and rounds its corners with a triangular moving average. Open ends stay put.</summary>
    internal static (double[] Xs, double[] Ys) Smooth(IReadOnlyList<(double X, double Y)> corners, bool closed)
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

        int half = Math.Max(1, (int)Math.Round(SmoothingRadius / (length / n)));
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
