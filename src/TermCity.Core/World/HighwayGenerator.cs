using TermCity.Core.Roads;
using TermCity.Core.Terrain;
using TermCity.Core.Util;

namespace TermCity.Core.World;

/// <summary>
/// Lays down the pre-existing road network as a sparse highway system: a few interchanges linked by long straight
/// highways, entering the map through gateways on its edges. Highways turn through clean 90-degree bends, chamfered
/// corners, or a diagonal jog between two parallel runs. Diagonals are drawn as double-line staircases. Every junction is an interchange with three or four
/// arms; highways never touch or run alongside each other except at an interchange. A few short streets leave the
/// interchanges as starting points for the player's own roads. Most of the map is left empty for the player.
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
            _highway = _map.Content.Roads.Find(DefaultRoads.HighwayName) ?? _map.Content.Roads.OrderBy(r => r.Rank).Last();
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
            var cells = new List<Pos> { new(node.X, node.Y) };
            int x = node.X, y = node.Y;
            do
            {
                x += dx;
                y += dy;
                cells.Add(new Pos(x, y));
            }
            while (InBounds(x, y) && !OnBoundary(x, y));

            if (!InBounds(x, y))
            {
                return null;
            }

            return Valid(cells, [], node, null, MaxBridgeTotal, MaxBridgeRun)
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
            // Some routes keep sharp 90-degree corners and some use diagonals, so the network is not all one style.
            bool orthogonalOnly = _rng.Chance(0.3);

            Route? best = null;
            int bestScore = int.MaxValue;
            foreach (var (corners, portA, portB) in Candidates(a, b))
            {
                if (a.Used[portA] || b.Used[portB])
                {
                    continue;
                }

                bool diagonal = HasDiagonal(corners);
                if (diagonal && orthogonalOnly)
                {
                    continue;
                }

                var cells = Expand(corners);
                if (!Valid(cells, corners.Skip(1).SkipLast(1).ToList(), a, b, MaxBridgeTotal, MaxBridgeRun))
                {
                    continue;
                }

                int water = cells.Count(c => !Open(c.X, c.Y));

                // Diagonal runs are never drawn over water: a bridge should run straight.
                if (diagonal && water > 0)
                {
                    continue;
                }

                // A staircase takes as many cells as the square corner it replaces, so a small bonus keeps diagonals common.
                int score = cells.Count + 6 * water + BendCost(corners) - (diagonal ? 3 : 0);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Route(a, portA, b, portB, cells, _highway);
                }
            }

            return best;
        }

        private static bool HasDiagonal(IReadOnlyList<Pos> corners)
        {
            for (int i = 1; i < corners.Count; i++)
            {
                if (corners[i].X != corners[i - 1].X && corners[i].Y != corners[i - 1].Y)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A 90-degree corner is costlier than a gentler 45-degree one.</summary>
        private static int BendCost(IReadOnlyList<Pos> corners)
        {
            int cost = 0;
            for (int i = 1; i < corners.Count - 1; i++)
            {
                int ax = Math.Sign(corners[i].X - corners[i - 1].X), ay = Math.Sign(corners[i].Y - corners[i - 1].Y);
                int bx = Math.Sign(corners[i + 1].X - corners[i].X), by = Math.Sign(corners[i + 1].Y - corners[i].Y);
                cost += ax * bx + ay * by == 0 ? 8 : 4;
            }

            return cost;
        }
        /// <summary>Straight, L-shaped and Z-shaped Manhattan routes between two interchanges, with the arms they use.</summary>
        private static IEnumerable<(List<Pos> Corners, int PortA, int PortB)> Candidates(Node a, Node b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            int horizontalOut = sx > 0 ? East : West;
            int verticalOut = sy > 0 ? South : North;

            if (dx == 0)
            {
                yield return ([new(a.X, a.Y), new(b.X, b.Y)], verticalOut, (verticalOut + 2) % 4);
                yield break;
            }

            if (dy == 0)
            {
                yield return ([new(a.X, a.Y), new(b.X, b.Y)], horizontalOut, (horizontalOut + 2) % 4);
                yield break;
            }

            int ax = Math.Abs(dx), ay = Math.Abs(dy);

            // L: along x first, then along y. Arrives at b from above (north) or below (south).
            if (ax >= MinLeg && ay >= MinLeg)
            {
                yield return ([new(a.X, a.Y), new(b.X, a.Y), new(b.X, b.Y)], horizontalOut, sy > 0 ? North : South);

                // L: along y first, then along x. Arrives at b from the west or east.
                yield return ([new(a.X, a.Y), new(a.X, b.Y), new(b.X, b.Y)], verticalOut, sx > 0 ? West : East);
            }

            // Chamfered L: the same two legs, but the corner is cut with a short 45-degree diagonal.
            foreach (int k in new[] { 2, 3, 5 })
            {
                if (ax - k >= MinLeg && ay - k >= MinLeg)
                {
                    yield return ([new(a.X, a.Y), new(b.X - sx * k, a.Y), new(b.X, a.Y + sy * k), new(b.X, b.Y)], horizontalOut, sy > 0 ? North : South);
                    yield return ([new(a.X, a.Y), new(a.X, b.Y - sy * k), new(a.X + sx * k, b.Y), new(b.X, b.Y)], verticalOut, sx > 0 ? West : East);
                }
            }

            // Diagonal jog: a straight run, a 45-degree diagonal that shifts the road sideways, and another straight run.
            if (ax >= ay + 2 * MinLeg && ay >= 2)
            {
                foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
                {
                    int lead = MinLeg + (int)Math.Round((ax - ay - 2 * MinLeg) * fraction);
                    yield return ([new(a.X, a.Y), new(a.X + sx * lead, a.Y), new(a.X + sx * (lead + ay), b.Y), new(b.X, b.Y)], horizontalOut, (horizontalOut + 2) % 4);
                }
            }

            if (ay >= ax + 2 * MinLeg && ax >= 2)
            {
                foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
                {
                    int lead = MinLeg + (int)Math.Round((ay - ax - 2 * MinLeg) * fraction);
                    yield return ([new(a.X, a.Y), new(a.X, a.Y + sy * lead), new(b.X, a.Y + sy * (lead + ax)), new(b.X, b.Y)], verticalOut, (verticalOut + 2) % 4);
                }
            }

            // Z: x, then y, then x.
            if (ax >= 2 * MinLeg && ay >= 3)
            {
                foreach (double fraction in new[] { 0.3, 0.5, 0.7 })
                {
                    int mx = a.X + (int)Math.Round(dx * fraction);
                    if (Math.Abs(mx - a.X) >= MinLeg && Math.Abs(b.X - mx) >= MinLeg)
                    {
                        yield return ([new(a.X, a.Y), new(mx, a.Y), new(mx, b.Y), new(b.X, b.Y)], horizontalOut, (horizontalOut + 2) % 4);
                    }
                }
            }

            // Z: y, then x, then y.
            if (ay >= 2 * MinLeg && ax >= 3)
            {
                foreach (double fraction in new[] { 0.3, 0.5, 0.7 })
                {
                    int my = a.Y + (int)Math.Round(dy * fraction);
                    if (Math.Abs(my - a.Y) >= MinLeg && Math.Abs(b.Y - my) >= MinLeg)
                    {
                        yield return ([new(a.X, a.Y), new(a.X, my), new(b.X, my), new(b.X, b.Y)], verticalOut, (verticalOut + 2) % 4);
                    }
                }
            }
        }

        /// <summary>
        /// The cells along a route. A diagonal segment becomes a staircase of ordinary road cells (one step right or
        /// left, then one up or down), so it joins up through the normal four-way connections and is drawn with the
        /// same double-line glyphs as the rest of the highway. Each diagonal step therefore takes two cells.
        /// </summary>
        private static List<Pos> Expand(IReadOnlyList<Pos> corners)
        {
            var cells = new List<Pos> { corners[0] };
            for (int i = 1; i < corners.Count; i++)
            {
                int sx = Math.Sign(corners[i].X - corners[i - 1].X);
                int sy = Math.Sign(corners[i].Y - corners[i - 1].Y);
                bool diagonal = sx != 0 && sy != 0;
                var p = corners[i - 1];
                while (p != corners[i])
                {
                    if (diagonal)
                    {
                        p = new Pos(p.X + sx, p.Y);
                        cells.Add(p);
                        p = new Pos(p.X, p.Y + sy);
                        cells.Add(p);
                    }
                    else
                    {
                        p = new Pos(p.X + sx, p.Y + sy);
                        cells.Add(p);
                    }
                }
            }

            return cells;
        }
        /// <summary>
        /// A route is valid if it stays on the map, crosses only a limited stretch of water (as a bridge), puts its bends
        /// and interchanges on dry ground, and keeps clear of every other highway and interchange.
        /// </summary>
        private bool Valid(IReadOnlyList<Pos> cells, IReadOnlyList<Pos> bends, Node a, Node? b, int maxWater, int maxRun)
        {
            int water = 0, run = 0;
            foreach (var c in cells)
            {
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
            }

            return bends.All(p => Open(p.X, p.Y)) && Open(cells[^1].X, cells[^1].Y);
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
