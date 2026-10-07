using TermCity.Core.Roads;
using TermCity.Core.Terrain;
using TermCity.Core.Util;

namespace TermCity.Core.World;

/// <summary>
/// Lays down the pre-existing road network as a sparse highway system: a few interchanges linked by long highways,
/// entering the map through gateways on its edges. Every interchange has straight arms along the four compass points; away
/// from them a highway is free to run at any angle (as a staircase of road cells that the renderer smooths into a straight
/// diagonal), easing out of one arm and into the next. Where water is crossed the bridge stays straight, and where an
/// angled link will not fit, a clean L or Z of right-angle corners is used instead. Every junction is an interchange with
/// three or four arms; highways never touch or run alongside each other except at an interchange. A few short streets leave
/// the interchanges as starting points for the player's own roads. Most of the map is left empty for the player.
/// </summary>
public static class HighwayGenerator
{
    public static void Generate(GenerationContext context) => new Planner(context).Run();

    private sealed class Node(int id, int x, int y)
    {
        public int Id { get; } = id;

        public int X { get; } = x;

        public int Y { get; } = y;

        /// <summary>Which of the four arms (N, E, S, W) are taken.</summary>
        public bool[] Used { get; } = new bool[4];

        public int Degree => Used.Count(u => u);
    }

    private sealed class Route(Node a, int portA, Node? b, int portB, List<Pos> cells, RoadType type)
    {
        public Node A { get; } = a;

        public int PortA { get; } = portA;

        /// <summary>The far interchange, or null for a gateway ray or a street stub.</summary>
        public Node? B { get; } = b;

        public int PortB { get; } = portB;

        public List<Pos> Cells { get; } = cells;

        public RoadType Type { get; } = type;

        public bool IsGateway => B is null && Type.Rank >= 3;
    }

    private sealed class Planner
    {
        private const int EdgeMargin = 7;

        // Interchanges keep this window of open (non-water) ground around them.
        private const int WindowX = 3;
        private const int WindowY = 2;

        // Free space kept around highways, so unrelated highways are never close together. Real highways run miles apart,
        // never side by side. Characters are about twice as tall as wide, so the vertical spacing is half the horizontal.
        // The same window around an interchange belongs to it: its own arms may share it, nothing else may enter it.
        private const int ClearanceX = 16;
        private const int ClearanceY = 8;

        // No bend may sit closer than this to an interchange or to another bend.
        private const int MinLeg = 4;

        // An interchange within this many columns (rows) of another's column (row) is snapped to it exactly.
        private const int SnapX = 3;
        private const int SnapY = 2;

        // Every arm leaves its interchange straight for this many cells before an angled highway may turn away: long
        // enough that the junction itself stays square. Columns are narrower than rows are tall, so the lead is longer.
        private const int LeadX = 5;
        private const int LeadY = 3;

        // The biggest turn an angled highway makes between a lead and its diagonal, as the cosine of the angle.
        private const double MaxTurnCos = 0.5;

        // Rows and columns around an interchange where its own arms may run close together.
        private const int ForkX = 6;
        private const int ForkY = 4;

        private const int MaxBridgeRun = 8;
        private const int MaxBridgeTotal = 16;

        private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        private const int North = 0, East = 1, South = 2, West = 3;

        private readonly GameMap _map;
        private readonly GameRandom _rng;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _refs;
        private readonly int[] _zone;
        private readonly List<Node> _nodes = [];
        private readonly List<Route> _routes = [];
        private readonly RoadType _highway;
        private readonly RoadType _street;

        public Planner(GenerationContext context)
        {
            _map = context.Map;
            _rng = context.CreateRandom("highways");
            _width = _map.Width;
            _height = _map.Height;
            _refs = new byte[_width * _height];
            _zone = new int[_width * _height];
            Array.Fill(_zone, -1);
            _highway = _map.Content.Roads.Find(DefaultRoads.KingsRoadName) ?? _map.Content.Roads.OrderBy(r => r.Rank).Last();
            _street = _map.Content.Roads.Default;
        }

        public void Run()
        {
            // A random layout can come out too thin (interchanges stranded by water, links that do not fit), so try a
            // few times and keep the first network that is long enough and reaches the edge of the map.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Reset();
                if (TryBuild())
                {
                    AddStreetStubs();
                    Rasterize();
                    return;
                }
            }

            Reset();
            Fallback();
        }


        private void Reset()
        {
            Array.Clear(_refs);
            Array.Fill(_zone, -1);
            _nodes.Clear();
            _routes.Clear();
        }

        private bool TryBuild()
        {
            PlaceInterchanges();
            if (_nodes.Count < 2)
            {
                return false;
            }

            GrowTree();
            AddLoops();
            FixDeadEnds();
            AddGateways();

            int length = _routes.Sum(r => r.Cells.Count);
            return _routes.Any(r => r.IsGateway) && length >= (_width + _height) / 2;
        }
        // ---- Geometry helpers ---------------------------------------------------------------------------------

        private int Index(int x, int y) => y * _width + x;

        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

        private bool Open(int x, int y) => _map.TerrainAt(x, y).Buildable;

        private static int Cheb(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

        // Cells are about twice as tall as wide, so vertical distance counts double.
        private static double Dist2(Node a, Node b)
        {
            double dx = a.X - b.X, dy = 2.0 * (a.Y - b.Y);
            return dx * dx + dy * dy;
        }

        private bool OnBoundary(int x, int y) => x == 0 || y == 0 || x == _width - 1 || y == _height - 1;

        // ---- Interchanges -------------------------------------------------------------------------------------

        private void PlaceInterchanges()
        {
            double area = _width * (double)_height;
            // Deliberately sparse: the default map gets three interchanges and the rest is room for the player to build.
            int target = Math.Max(3, (int)Math.Round(area / 6000.0));
            double minDist2 = 0.55 * 0.55 * 2.0 * area / target;

            for (int attempt = 0; attempt < target * 40 && _nodes.Count < target; attempt++)
            {
                int x = _rng.Next(EdgeMargin, _width - EdgeMargin);
                int y = _rng.Next(EdgeMargin - 2, _height - (EdgeMargin - 2));

                // Highways only bend cleanly, so near-aligned interchanges are lined up exactly: the link between them
                // is then a straight highway rather than an awkward little offset. Cells are about twice as tall as
                // wide, so the vertical snap distance is shorter. Any near miss left over (it could not be snapped
                // without clashing with another node) is rejected, as no clean route would fit between them.
                x = SnapTo(x, _nodes.Select(n => n.X), SnapX);
                y = SnapTo(y, _nodes.Select(n => n.Y), SnapY);
                if (_nodes.Any(n => (n.X != x && Math.Abs(n.X - x) < SnapX) || (n.Y != y && Math.Abs(n.Y - y) < SnapY)))
                {
                    continue;
                }

                if (!WindowIsOpen(x, y))
                {
                    continue;
                }

                var candidate = new Node(_nodes.Count, x, y);

                // Interchanges are far apart, and their windows never overlap.
                if (_nodes.Any(n => Dist2(n, candidate) < minDist2 || (Math.Abs(n.X - x) <= 2 * ClearanceX && Math.Abs(n.Y - y) <= 2 * ClearanceY)))
                {
                    continue;
                }

                _nodes.Add(candidate);
                for (int dy = -ClearanceY; dy <= ClearanceY; dy++)
                {
                    for (int dx = -ClearanceX; dx <= ClearanceX; dx++)
                    {
                        if (InBounds(x + dx, y + dy))
                        {
                            _zone[Index(x + dx, y + dy)] = candidate.Id;
                        }
                    }
                }
            }
        }

        /// <summary>The nearest existing coordinate if it is a little off (but not equal to) the candidate, else the candidate.</summary>
        private static int SnapTo(int value, IEnumerable<int> existing, int distance)
        {
            int best = value, bestGap = distance;
            foreach (int e in existing)
            {
                int gap = Math.Abs(e - value);
                if (gap > 0 && gap < bestGap)
                {
                    best = e;
                    bestGap = gap;
                }
            }

            return best;
        }

        private bool WindowIsOpen(int x, int y)
        {
            for (int dy = -WindowY; dy <= WindowY; dy++)
            {
                for (int dx = -WindowX; dx <= WindowX; dx++)
                {
                    if (!Open(x + dx, y + dy))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // ---- Linking ------------------------------------------------------------------------------------------

        /// <summary>Connects every interchange into one network, nearest first (Prim's algorithm).</summary>
        private void GrowTree()
        {
            var connected = new List<Node> { _nodes[_rng.Next(_nodes.Count)] };
            var remaining = _nodes.Where(n => n != connected[0]).ToList();
            var nearest = new double[_nodes.Count];
            foreach (var n in remaining)
            {
                nearest[n.Id] = Dist2(n, connected[0]);
            }

            while (remaining.Count > 0)
            {
                int pick = 0;
                for (int i = 1; i < remaining.Count; i++)
                {
                    if (nearest[remaining[i].Id] < nearest[remaining[pick].Id])
                    {
                        pick = i;
                    }
                }

                var next = remaining[pick];
                remaining[pick] = remaining[^1];
                remaining.RemoveAt(remaining.Count - 1);

                bool linked = false;
                foreach (var other in connected.OrderBy(c => Dist2(c, next)).ThenBy(c => c.Id).Take(5))
                {
                    var route = BestRoute(other, next);
                    if (route is not null)
                    {
                        Commit(route);
                        linked = true;
                        break;
                    }
                }

                // An interchange that cannot be reached (water, crowding) is simply left out.
                if (!linked)
                {
                    continue;
                }

                connected.Add(next);
                foreach (var r in remaining)
                {
                    nearest[r.Id] = Math.Min(nearest[r.Id], Dist2(r, next));
                }
            }
        }

        /// <summary>Adds some extra links between neighbouring interchanges so the network has loops.</summary>
        private void AddLoops()
        {
            var inTree = _nodes.Where(n => n.Degree > 0).ToList();
            foreach (var a in inTree.OrderBy(_ => _rng.NextDouble()).ToList())
            {
                if (!_rng.Chance(0.2))
                {
                    continue;
                }

                foreach (var b in inTree.Where(n => n != a && !Linked(a, n)).OrderBy(n => Dist2(a, n)).ThenBy(n => n.Id).Take(3))
                {
                    var route = BestRoute(a, b);
                    if (route is not null)
                    {
                        Commit(route);
                        break;
                    }
                }
            }
        }

        private bool Linked(Node a, Node b) => _routes.Any(r => (r.A == a && r.B == b) || (r.A == b && r.B == a));

        /// <summary>A highway must not simply end: give it a gateway, link it onward, or remove it.</summary>
        private void FixDeadEnds()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in _nodes.ToList())
                {
                    if (node.Degree != 1)
                    {
                        continue;
                    }

                    // A highway that would otherwise stop short runs off the map if an edge is near, else links onward.
                    if (TryGateway(node, maxLength: 70) || TryLinkOnward(node))
                    {
                        changed = true;
                        continue;
                    }

                    var only = _routes.First(r => r.A == node || r.B == node);
                    Remove(only);
                    changed = true;
                }
            }
        }

        private bool TryLinkOnward(Node node)
        {
            foreach (var other in _nodes.Where(n => n != node && n.Degree > 0 && !Linked(node, n)).OrderBy(n => Dist2(node, n)).ThenBy(n => n.Id).Take(4))
            {
                var route = BestRoute(node, other);
                if (route is not null)
                {
                    Commit(route);
                    return true;
                }
            }

            return false;
        }

        // ---- Gateways -----------------------------------------------------------------------------------------

        private Route? BestRay(Node node)
        {
            Route? best = null;
            for (int port = 0; port < 4; port++)
            {
                var ray = Ray(node, port);
                if (ray is not null && (best is null || ray.Cells.Count < best.Cells.Count))
                {
                    best = ray;
                }
            }

            return best;
        }

        private bool TryGateway(Node node, int maxLength)
        {
            var ray = BestRay(node);
            if (ray is null || ray.Cells.Count > maxLength)
            {
                return false;
            }

            Commit(ray);
            return true;
        }

        private Route? Ray(Node node, int port)
        {
            if (node.Used[port])
            {
                return null;
            }

            var (dx, dy) = Dirs[port];
            int lead = Lead(port);
            var p1 = new Pos(node.X + dx * lead, node.Y + dy * lead);
            if (!InBounds(p1.X, p1.Y) || OnBoundary(p1.X, p1.Y))
            {
                return null;
            }

            // The edge cell straight ahead, or one off to the side: an angled run to it, within what turns gently.
            var edge = new Pos(dx != 0 ? (dx > 0 ? _width - 1 : 0) : p1.X, dy != 0 ? (dy > 0 ? _height - 1 : 0) : p1.Y);
            int along = dx != 0 ? Math.Abs(edge.X - p1.X) : Math.Abs(edge.Y - p1.Y);
            double reach = 0.75 * along * (dx != 0 ? CellW / CellH : CellH / CellW);
            int drift = reach >= 1 && _rng.Chance(0.75) ? _rng.Next(-(int)reach, (int)reach + 1) : 0;
            if (dx != 0)
            {
                edge = new Pos(edge.X, Math.Clamp(edge.Y + drift, 1, _height - 2));
            }
            else
            {
                edge = new Pos(Math.Clamp(edge.X + drift, 1, _width - 2), edge.Y);
            }

            var cells = Expand([new Pos(node.X, node.Y), p1, edge]);
            int end = cells.FindIndex(c => OnBoundary(c.X, c.Y));
            if (end < 0)
            {
                return null;
            }

            cells.RemoveRange(end + 1, cells.Count - end - 1);
            return Valid(cells, [p1], node, null, MaxBridgeTotal, MaxBridgeRun, separate: true)
                ? new Route(node, port, null, -1, cells, _highway)
                : null;
        }

        /// <summary>Makes sure the network reaches the outside world in enough places for the map's size.</summary>
        private void AddGateways()
        {
            int wanted = Math.Max(2, (int)Math.Round(2.0 * (_width + _height) / 220.0));
            var ends = _routes.Where(r => r.IsGateway).Select(r => r.Cells[^1]).ToList();
            if (ends.Count >= wanted)
            {
                return;
            }

            var candidates = new List<Route>();
            foreach (var node in _nodes.Where(n => n.Degree > 0))
            {
                for (int port = 0; port < 4; port++)
                {
                    var ray = Ray(node, port);
                    if (ray is not null)
                    {
                        candidates.Add(ray);
                    }
                }
            }

            foreach (var ray in candidates.OrderBy(r => r.Cells.Count).ThenBy(r => r.A.Id).ThenBy(r => r.PortA))
            {
                if (ends.Count >= wanted)
                {
                    break;
                }

                var end = ray.Cells[^1];
                if (ray.A.Used[ray.PortA] || ends.Any(e => Cheb(e.X, e.Y, end.X, end.Y) < 24))
                {
                    continue;
                }

                // An earlier choice may have used up space this ray needs.
                if (!Valid(ray.Cells, [], ray.A, null, MaxBridgeTotal, MaxBridgeRun))
                {
                    continue;
                }

                Commit(ray);
                ends.Add(end);
            }
        }

        // ---- Streets ------------------------------------------------------------------------------------------

        /// <summary>Short streets off the interchanges: somewhere for the player to start building.</summary>
        private void AddStreetStubs()
        {
            var nodes = _nodes.Where(n => n.Degree > 0).ToList();
            int total = 0;

            // Each interchange gets a stub or two, at random; if chance gave a map none at all, add one so the player
            // always has a street to start from.
            for (int pass = 0; pass < 2 && total == 0; pass++)
            {
                foreach (var node in nodes)
                {
                    int placed = 0;
                    foreach (int port in Enumerable.Range(0, 4).OrderBy(_ => _rng.NextDouble()))
                    {
                        double chance = pass == 1 ? 1.0 : placed == 0 ? 0.6 : 0.2;
                        if (node.Used[port] || !_rng.Chance(chance))
                        {
                            continue;
                        }

                        int length = _rng.Next(6, 13);
                        var (dx, dy) = Dirs[port];
                        var cells = new List<Pos> { new(node.X, node.Y) };
                        for (int i = 1; i <= length; i++)
                        {
                            cells.Add(new Pos(node.X + dx * i, node.Y + dy * i));
                        }

                        var last = cells[^1];
                        if (!InBounds(last.X, last.Y) || OnBoundary(last.X, last.Y) || !Valid(cells, [], node, null, 0, 0))
                        {
                            continue;
                        }

                        Commit(new Route(node, port, null, -1, cells, _street));
                        total++;
                        if (++placed == 2 || pass == 1)
                        {
                            break;
                        }
                    }

                    if (pass == 1 && total > 0)
                    {
                        break;
                    }
                }
            }
        }
        // ---- Routes -------------------------------------------------------------------------------------------

        private Route? BestRoute(Node a, Node b)
        {
            Route? best = null;
            int bestScore = int.MaxValue;
            foreach (var (corners, portA, portB, turn) in Candidates(a, b))
            {
                if (a.Used[portA] || b.Used[portB])
                {
                    continue;
                }

                var cells = Expand(corners);
                bool angled = corners.Count == 4 && corners[1].X != corners[2].X && corners[1].Y != corners[2].Y;
                if (!Valid(cells, corners.Skip(1).SkipLast(1).ToList(), a, b, MaxBridgeTotal, MaxBridgeRun, separate: angled))
                {
                    continue;
                }

                int water = cells.Count(c => !Open(c.X, c.Y));
                int bends = angled ? 0 : corners.Count - 2;

                // Angled routes are the norm; right-angle ones are what is left when water or crowding rules them out.
                // A small seeded jitter decides between otherwise equal routes.
                int score = cells.Count + 6 * water + 8 * bends + turn + _rng.Next(0, 4);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Route(a, portA, b, portB, cells, _highway);
                }
            }

            return best;
        }

        /// <summary>
        /// Routes between two interchanges, with the arms they use. Straight when the two line up; otherwise angled (an
        /// arm out of each, joined by a diagonal) and, for the cases where those cannot be used, L- and Z-shaped
        /// right-angle routes whose legs are all at least MinLeg long. The last number is a small penalty for sharp turns.
        /// </summary>
        private static IEnumerable<(List<Pos> Corners, int PortA, int PortB, int Turn)> Candidates(Node a, Node b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            int horizontalOut = sx > 0 ? East : West;
            int verticalOut = sy > 0 ? South : North;

            if (dx == 0)
            {
                yield return ([new(a.X, a.Y), new(b.X, b.Y)], verticalOut, (verticalOut + 2) % 4, 0);
                yield break;
            }

            if (dy == 0)
            {
                yield return ([new(a.X, a.Y), new(b.X, b.Y)], horizontalOut, (horizontalOut + 2) % 4, 0);
                yield break;
            }

            foreach (int portA in new[] { horizontalOut, verticalOut })
            {
                foreach (int arrival in new[] { horizontalOut, verticalOut })
                {
                    int portB = (arrival + 2) % 4;
                    var p1 = new Pos(a.X + Dirs[portA].Dx * Lead(portA), a.Y + Dirs[portA].Dy * Lead(portA));
                    var p2 = new Pos(b.X + Dirs[portB].Dx * Lead(portB), b.Y + Dirs[portB].Dy * Lead(portB));
                    int ddx = p2.X - p1.X, ddy = p2.Y - p1.Y;
                    if (ddx * sx < 0 || ddy * sy < 0 || (ddx == 0 && ddy == 0))
                    {
                        continue;
                    }

                    // Turns are measured on screen, where a column is narrower than a row is tall.
                    double length = Math.Sqrt(Sq(ddx * CellW) + Sq(ddy * CellH));
                    double cosOut = (Dirs[portA].Dx * ddx * CellW * CellW + Dirs[portA].Dy * ddy * CellH * CellH) / (length * Pixels(Dirs[portA]));
                    double cosIn = (Dirs[arrival].Dx * ddx * CellW * CellW + Dirs[arrival].Dy * ddy * CellH * CellH) / (length * Pixels(Dirs[arrival]));
                    if (cosOut < MaxTurnCos || cosIn < MaxTurnCos)
                    {
                        continue;
                    }

                    int turn = (int)Math.Round((Math.Acos(Math.Min(1, cosOut)) + Math.Acos(Math.Min(1, cosIn))) * 180 / Math.PI / 6);
                    yield return ([new(a.X, a.Y), p1, p2, new(b.X, b.Y)], portA, portB, turn);
                }
            }

            int ax = Math.Abs(dx), ay = Math.Abs(dy);

            // L: along x first, then along y. Arrives at b from above (north) or below (south).
            if (ax >= MinLeg && ay >= MinLeg)
            {
                yield return ([new(a.X, a.Y), new(b.X, a.Y), new(b.X, b.Y)], horizontalOut, sy > 0 ? North : South, 8);

                // L: along y first, then along x. Arrives at b from the west or east.
                yield return ([new(a.X, a.Y), new(a.X, b.Y), new(b.X, b.Y)], verticalOut, sx > 0 ? West : East, 8);
            }

            // Z: x, then y, then x.
            if (ax >= 2 * MinLeg && ay >= MinLeg)
            {
                foreach (double fraction in new[] { 0.3, 0.5, 0.7 })
                {
                    int mx = a.X + (int)Math.Round(dx * fraction);
                    if (Math.Abs(mx - a.X) >= MinLeg && Math.Abs(b.X - mx) >= MinLeg)
                    {
                        yield return ([new(a.X, a.Y), new(mx, a.Y), new(mx, b.Y), new(b.X, b.Y)], horizontalOut, (horizontalOut + 2) % 4, 16);
                    }
                }
            }

            // Z: y, then x, then y.
            if (ay >= 2 * MinLeg && ax >= MinLeg)
            {
                foreach (double fraction in new[] { 0.3, 0.5, 0.7 })
                {
                    int my = a.Y + (int)Math.Round(dy * fraction);
                    if (Math.Abs(my - a.Y) >= MinLeg && Math.Abs(b.Y - my) >= MinLeg)
                    {
                        yield return ([new(a.X, a.Y), new(a.X, my), new(b.X, my), new(b.X, b.Y)], verticalOut, (verticalOut + 2) % 4, 16);
                    }
                }
            }
        }

        private const double CellW = 12, CellH = 22;

        private static double Sq(double v) => v * v;

        private static int Lead(int port) => port is East or West ? LeadX : LeadY;

        private static double Pixels((int Dx, int Dy) dir) => dir.Dx != 0 ? CellW : CellH;

        /// <summary>
        /// The cells along a route, leg by leg between consecutive corners. A leg along a row or column is straight; any
        /// other leg is a staircase of four-connected cells whose steps are spread evenly along the line.
        /// </summary>
        private static List<Pos> Expand(IReadOnlyList<Pos> corners)
        {
            var cells = new List<Pos> { corners[0] };
            for (int i = 1; i < corners.Count; i++)
            {
                int ax = Math.Abs(corners[i].X - corners[i - 1].X), ay = Math.Abs(corners[i].Y - corners[i - 1].Y);
                int sx = Math.Sign(corners[i].X - corners[i - 1].X), sy = Math.Sign(corners[i].Y - corners[i - 1].Y);
                int x = corners[i - 1].X, y = corners[i - 1].Y, stepsX = 0, stepsY = 0;
                while (stepsX < ax || stepsY < ay)
                {
                    // Move along whichever axis is further behind where the straight line would have it.
                    bool alongX = stepsY >= ay || (stepsX < ax && (stepsX + 0.5) * ay <= (stepsY + 0.5) * ax);
                    if (alongX)
                    {
                        x += sx;
                        stepsX++;
                    }
                    else
                    {
                        y += sy;
                        stepsY++;
                    }

                    cells.Add(new Pos(x, y));
                }
            }

            return cells;
        }

        /// <summary>
        /// A route is valid if it stays on the map, crosses only a limited stretch of water (as a bridge), puts its bends
        /// and interchanges on dry ground, and keeps clear of every other highway and interchange.
        /// </summary>
        private bool Valid(IReadOnlyList<Pos> cells, IReadOnlyList<Pos> bends, Node a, Node? b, int maxWater, int maxRun, bool separate = false)
        {
            int water = 0, run = 0;
            for (int n = 0; n < cells.Count; n++)
            {
                var c = cells[n];
                if (!InBounds(c.X, c.Y))
                {
                    return false;
                }

                if (Open(c.X, c.Y))
                {
                    run = 0;
                }
                else if (++water > maxWater || ++run > maxRun)
                {
                    return false;
                }
                else if (n > 0 && n < cells.Count - 1 && !Straight(cells[n - 1], c, cells[n + 1]))
                {
                    // A bridge never bends.
                    return false;
                }

                int zone = _zone[Index(c.X, c.Y)];
                if (zone >= 0 && zone != a.Id && (b is null || zone != b.Id))
                {
                    return false;
                }

                // Inside the window of its own interchange a route shares space with that interchange's other arms.
                bool inOwnWindow = InWindow(c, a) || (b is not null && InWindow(c, b));
                if (!inOwnWindow && Crowded(c.X, c.Y))
                {
                    return false;
                }

                // Two arms of one interchange may share its middle but must then fan out, not run side by side.
                if (separate && inOwnWindow && !Near(c, a) && (b is null || !Near(c, b)) && Touching(c.X, c.Y))
                {
                    return false;
                }
            }

            return bends.All(p => Open(p.X, p.Y)) && Open(cells[^1].X, cells[^1].Y);
        }

        private static bool Straight(Pos before, Pos at, Pos after) =>
            (before.X == at.X && at.X == after.X) || (before.Y == at.Y && at.Y == after.Y);

        private static bool Near(Pos c, Node node) => Math.Abs(c.X - node.X) <= ForkX && Math.Abs(c.Y - node.Y) <= ForkY;

        private bool Touching(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -3; dx <= 3; dx++)
                {
                    if (InBounds(x + dx, y + dy) && _refs[Index(x + dx, y + dy)] > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool InWindow(Pos c, Node node) =>
            Math.Abs(c.X - node.X) <= ClearanceX && Math.Abs(c.Y - node.Y) <= ClearanceY;

        private bool Crowded(int x, int y)
        {
            for (int dy = -ClearanceY; dy <= ClearanceY; dy++)
            {
                for (int dx = -ClearanceX; dx <= ClearanceX; dx++)
                {
                    if (InBounds(x + dx, y + dy) && _refs[Index(x + dx, y + dy)] > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void Commit(Route route)
        {
            foreach (var c in route.Cells.Skip(1))
            {
                int i = Index(c.X, c.Y);
                if (_refs[i] < byte.MaxValue)
                {
                    _refs[i]++;
                }
            }

            route.A.Used[route.PortA] = true;
            if (route.B is not null)
            {
                route.B.Used[route.PortB] = true;
            }

            _routes.Add(route);
        }

        private void Remove(Route route)
        {
            foreach (var c in route.Cells.Skip(1))
            {
                _refs[Index(c.X, c.Y)]--;
            }

            route.A.Used[route.PortA] = false;
            if (route.B is not null)
            {
                route.B.Used[route.PortB] = false;
            }

            _routes.Remove(route);
        }

        // ---- Output -------------------------------------------------------------------------------------------

        private void Rasterize()
        {
            // Streets first so an interchange cell always ends up as highway.
            foreach (var route in _routes.OrderBy(r => r.Type.Rank))
            {
                foreach (var c in route.Cells)
                {
                    _map.SetRoad(c.X, c.Y, route.Type);
                }
            }
        }

        /// <summary>If no interchange network could be built (for example a map that is mostly water), run straight highways across.</summary>
        private void Fallback()
        {
            foreach (bool horizontal in new[] { true, false })
            {
                int length = horizontal ? _width : _height;
                int across = horizontal ? _height : _width;
                List<Pos>? best = null;
                int bestWater = int.MaxValue;
                for (int attempt = 0; attempt < 24; attempt++)
                {
                    int lane = (int)(across * (0.25 + 0.5 * _rng.NextDouble()));
                    var path = Enumerable.Range(0, length).Select(t => horizontal ? new Pos(t, lane) : new Pos(lane, t)).ToList();
                    int water = path.Count(p => !Open(p.X, p.Y));
                    if (water < bestWater)
                    {
                        best = path;
                        bestWater = water;
                    }
                }

                foreach (var p in best!)
                {
                    _map.SetRoad(p.X, p.Y, _highway);
                }
            }
        }
    }
}
