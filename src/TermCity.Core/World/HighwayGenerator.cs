using TermCity.Core.Rendering;
using TermCity.Core.Roads;
using TermCity.Core.Terrain;
using TermCity.Core.Util;

namespace TermCity.Core.World;

/// <summary>Generates broadly intersecting intercity trunks and a waterfront starter street.</summary>
public static class HighwayGenerator
{
    /// <summary>Maximum Manhattan distance from buildable ground to a logical highway centerline.</summary>
    public const int MaximumHighwayDistance = 96;

    public static void Generate(GenerationContext context) => new Planner(context).Run();

    public static Pos? StartingSpur(GameMap map) => map.RoadCells.Select(map.PosOf)
        .Where(p => map.RoadTypeAt(p.X, p.Y)?.Name == DefaultRoads.TrackName &&
            !map.IsEdge(p.X, p.Y) && map.TerrainAt(p.X, p.Y).Buildable &&
            RoadNeighbors(map, p) == 1 &&
            new CellRect(p.X - 6, p.Y - 6, 13, 13).Cells().Any(w => map.InBounds(w) &&
                Math.Abs(w.X - p.X) + Math.Abs(w.Y - p.Y) <= 6 &&
                map.TerrainAt(w.X, w.Y).Name == DefaultTerrains.WaterName))
        .OrderBy(p => Math.Abs(p.X - map.Width / 2) + Math.Abs(p.Y - map.Height / 2))
        .ThenBy(p => p.Y).ThenBy(p => p.X).Select(p => (Pos?)p).FirstOrDefault();

    private static int RoadNeighbors(GameMap map, Pos p) =>
        (map.HasRoad(p.X - 1, p.Y) ? 1 : 0) + (map.HasRoad(p.X + 1, p.Y) ? 1 : 0) +
        (map.HasRoad(p.X, p.Y - 1) ? 1 : 0) + (map.HasRoad(p.X, p.Y + 1) ? 1 : 0);

    private sealed class Planner
    {
        private const int ClearanceX = 16;
        private const int ClearanceY = 8;
        private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        private readonly GameMap _map;
        private readonly GameRandom _rng;
        private readonly int _width, _height;
        private readonly RoadType _highway, _street;
        private readonly List<List<Pos>> _trunks = [];
        private readonly int _roadBudget;

        public Planner(GenerationContext context)
        {
            _map = context.Map;
            _rng = context.CreateRandom("highways");
            _width = _map.Width;
            _height = _map.Height;
            _highway = _map.Content.Roads.Find(DefaultRoads.KingsRoadName) ?? _map.Content.Roads.OrderBy(r => r.Rank).Last();
            _street = _map.Content.Roads.Default;
            _roadBudget = Math.Max(Math.Max(_width, _height) + 16,
                (int)(_width * (long)_height * (_width * (long)_height <= 160 * 96 ? 0.025 : 0.015)));
        }

        public void Run()
        {
            bool horizontal = _width >= _height;
            int across = horizontal ? _height : _width;
            int length = horizontal ? _width : _height;
            int lanes = (across + 159) / 160;
            int crossLanes = across >= 80 && _width * (long)_height >= 8192
                ? length >= 600 && across >= 300 ? 2 : 1 : 0;
            int reserve = crossLanes * (across * 2 / 3 + Math.Min(length / 10, across / 4)) + 12;
            int primaryBudget = Math.Max(lanes * length + lanes * 8, _roadBudget - reserve);
            for (int lane = 0; lane < lanes; lane++)
            {
                int center = (2 * lane + 1) * across / (2 * lanes);
                int sway = Math.Max(1, MaximumHighwayDistance - (across + lanes - 1) / (2 * lanes) - 2);
                int movement = Math.Min(sway * 2, Math.Max(12, (primaryBudget - lanes * length) / lanes));
                if (!AddThroughRoad(horizontal, center, sway, false, primaryBudget, movement))
                    AddStraightFallback(horizontal, center, sway);
            }
            for (int lane = 0; lane < crossLanes; lane++)
            {
                int crossAcross = horizontal ? _width : _height;
                int center = (lane + 1) * crossAcross / (crossLanes + 1) +
                    _rng.Next(-crossAcross / 12, crossAcross / 12 + 1);
                AddThroughRoad(!horizontal, center, Math.Min(crossAcross / (crossLanes * 5), across / 4),
                    true, _roadBudget - 24, Math.Min(32, across / 6), lane > 0);
            }
            AddWaterfrontStreetStubs();
            if (_map.RoadCells.All(i => _map.RoadTypeAt(i % _width, i / _width) != _street))
                AddFallbackStreetStub();
        }

        private int Index(int x, int y) => y * _width + x;
        private bool InBounds(int x, int y) => _map.InBounds(x, y);
        private bool Open(int x, int y) => _map.TerrainAt(x, y).Buildable;
        private bool OnBoundary(int x, int y) => _map.IsEdge(x, y);
        private static int Cheb(int ax, int ay, int bx, int by) => Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

        private bool WaterCrossingsValid(IReadOnlyList<Pos> cells)
        {
            int run = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (Open(cell.X, cell.Y)) { run = 0; continue; }
                if (++run > 16 || i == 0 || i == cells.Count - 1 ||
                    !(cells[i - 1].X == cell.X && cell.X == cells[i + 1].X ||
                        cells[i - 1].Y == cell.Y && cell.Y == cells[i + 1].Y)) return false;
            }
            return true;
        }

        private List<Pos>? StraightenWaterCrossings(List<Pos> cells, bool horizontal)
        {
            int Along(Pos p) => horizontal ? p.X : p.Y;
            int direction = Math.Sign(Along(cells[^1]) - Along(cells[0]));
            for (int index = 1; index < cells.Count - 1; index++)
            {
                if (Open(cells[index].X, cells[index].Y)) continue;
                int last = index;
                while (last + 1 < cells.Count && !Open(cells[last + 1].X, cells[last + 1].Y)) last++;
                if (last + 1 == cells.Count) return null;
                var inlet = cells[index - 1];
                var outlet = inlet;
                int steps = 0;
                do
                {
                    outlet = horizontal ? outlet.Offset(direction, 0) : outlet.Offset(0, direction);
                    if (!InBounds(outlet.X, outlet.Y) || ++steps > 17) return null;
                } while (!Open(outlet.X, outlet.Y));
                int resume = last + 1;
                while (resume < cells.Count - 1 &&
                    ((Along(cells[resume]) - Along(outlet)) * direction < 6 ||
                        !Open(cells[resume].X, cells[resume].Y))) resume++;
                if ((Along(cells[resume]) - Along(outlet)) * direction < 0) return null;
                var replacement = CellLines.Between(inlet, outlet)
                    .Concat(CellLines.Between(outlet, cells[resume]).Skip(1)).ToList();
                cells.RemoveRange(index - 1, resume - index + 2);
                cells.InsertRange(index - 1, replacement);
                index += steps - 1;
            }
            return cells;
        }

        private bool AddThroughRoad(bool horizontal, int center, int sway, bool intersect,
            int budget, int movement, bool reverse = false)
        {
            int length = horizontal ? _width : _height;
            int across = horizontal ? _height : _width;
            bool middleJoin = reverse;
            List<Pos>? best = null;
            int bestScore = int.MaxValue;
            for (int attempt = 0; attempt < 240; attempt++)
            {
                int margin = Math.Min(7, Math.Max(1, across / 8));
                Pos Point(int along, int cross) => horizontal ? new(along, cross) : new(cross, along);
                int Cross() => Math.Clamp(center + _rng.Next(-sway, sway + 1), margin, across - margin - 1);
                var corners = new List<Pos>
                {
                    Point(0, Cross()), Point((int)(length * (0.2 + _rng.NextDouble() * 0.18)), Cross()),
                    Point((int)(length * (0.58 + _rng.NextDouble() * 0.18)), Cross()), Point(length - 1, Cross())
                };
                if (!intersect)
                    for (int i = 1; i < corners.Count; i++)
                    {
                        int along = horizontal ? corners[i].X : corners[i].Y;
                        int previousAlong = horizontal ? corners[i - 1].X : corners[i - 1].Y;
                        int previousCross = horizontal ? corners[i - 1].Y : corners[i - 1].X;
                        int change = Math.Max(1, (along - previousAlong - 8) / 3);
                        int low = Math.Max(Math.Max(margin, center - sway), previousCross - change);
                        int high = Math.Min(Math.Min(across - margin - 1, center + sway), previousCross + change);
                        corners[i] = Point(along, _rng.Next(low, high + 1));
                    }
                if (!intersect && length >= 80)
                {
                    var approaches = new List<Pos> { corners[0] };
                    foreach (var corner in corners.Skip(1).SkipLast(1))
                    {
                        int along = horizontal ? corner.X : corner.Y;
                        int cross = horizontal ? corner.Y : corner.X;
                        approaches.Add(Point(along - 4, cross));
                        approaches.Add(Point(along + 4, cross));
                    }
                    approaches.Add(corners[^1]);
                    corners = approaches;
                }
                if (intersect)
                {
                    reverse = middleJoin || _rng.Chance(0.5);
                    var crossings = new List<Pos>();
                    foreach (var trunk in _trunks)
                    {
                        var choices = trunk.Where(p => (horizontal ? p.Y : p.X) >= margin &&
                            (horizontal ? p.Y : p.X) < across - margin &&
                            (crossings.Count == 0 || Math.Abs((horizontal ? p.Y : p.X) -
                                (horizontal ? crossings[^1].Y : crossings[^1].X)) <=
                                Math.Abs((horizontal ? p.X : p.Y) -
                                    (horizontal ? crossings[^1].X : crossings[^1].Y)) * 0.45) &&
                            Open(p.X, p.Y) && Enumerable.Range(-2, 5).All(offset => horizontal
                                ? _map.RoadTypeAt(p.X, p.Y + offset) == _highway
                                : _map.RoadTypeAt(p.X + offset, p.Y) == _highway)).ToArray();
                        if (choices.Length == 0) break;
                        crossings.Add(choices[_rng.Next(0, choices.Length)]);
                    }
                    if (crossings.Count != _trunks.Count) continue;
                    if (crossings.Count > 1 && crossings.Max(p => horizontal ? p.X : p.Y) -
                        crossings.Min(p => horizontal ? p.X : p.Y) < 24) continue;
                    var ordered = crossings.OrderBy(p => horizontal ? p.X : p.Y).ToArray();
                    if (middleJoin && ordered.Length > 1) ordered = ordered.Skip(ordered.Length / 2).ToArray();
                    if (ordered.Length == 1)
                        reverse = (horizontal ? ordered[0].X : ordered[0].Y) < length / 2;
                    var terminal = reverse ? ordered[0] : ordered[^1];
                    int terminalAlong = horizontal ? terminal.X : terminal.Y;
                    int travel = reverse ? length - 1 - terminalAlong : terminalAlong;
                    if (travel < length / 2) continue;
                    int entryCross = Cross();
                    if (attempt >= 160)
                    {
                        var first = reverse ? ordered[^1] : ordered[0];
                        entryCross = horizontal ? first.Y : first.X;
                    }
                    corners = [Point(reverse ? length - 1 : 0, entryCross)];
                    foreach (var crossing in ordered)
                    {
                        int along = horizontal ? crossing.X : crossing.Y;
                        int cross = horizontal ? crossing.Y : crossing.X;
                        if (crossing == terminal)
                        {
                            corners.Add(Point(along + (reverse ? 5 : -5), cross));
                            corners.Add(crossing);
                        }
                        else
                        {
                            corners.Add(Point(along - 5, cross));
                            corners.Add(Point(along + 5, cross));
                        }
                    }
                    corners = corners.OrderBy(p => (horizontal ? p.X : p.Y) * (reverse ? -1 : 1)).ToList();
                }
                if (corners.Zip(corners.Skip(1)).Any(pair =>
                    Math.Abs(horizontal ? pair.Second.Y - pair.First.Y : pair.Second.X - pair.First.X) >
                    Math.Abs(horizontal ? pair.Second.X - pair.First.X : pair.Second.Y - pair.First.Y) *
                        (intersect ? 0.55 : 1.0 / 3))) continue;
                var expanded = corners.Zip(corners.Skip(1), (a, b) => CellLines.Between(a, b))
                    .SelectMany((leg, index) => index == 0 ? leg : leg.Skip(1)).ToList();
                var cells = StraightenWaterCrossings(expanded, horizontal);
                if (cells is null) continue;
                if (cells.Count(p => !_map.HasRoad(p.X, p.Y)) + _map.RoadCount > budget) continue;
                if (!WaterCrossingsValid(cells)) continue;
                var plan = RoadRules.Plan(_map, cells, _highway.Rank, (_, _) => true);
                if (plan.Count != cells.Count(p => !_map.HasRoad(p.X, p.Y)) || !CleanContacts(cells)) continue;
                int water = cells.Count(p => !Open(p.X, p.Y));
                int alongSpan = Math.Abs(horizontal ? corners[^1].X - corners[0].X :
                    corners[^1].Y - corners[0].Y);
                int bends = cells.Count - alongSpan - 1;
                double deviation = corners.Max(p =>
                {
                    double along = horizontal ? p.X : p.Y;
                    double first = horizontal ? corners[0].X : corners[0].Y;
                    double last = horizontal ? corners[^1].X : corners[^1].Y;
                    double startCross = horizontal ? corners[0].Y : corners[0].X;
                    double endCross = horizontal ? corners[^1].Y : corners[^1].X;
                    return Math.Abs((horizontal ? p.Y : p.X) - startCross -
                        (endCross - startCross) * (along - first) / (last - first));
                });
                int score = water * 8 + Math.Abs(bends - movement) +
                    (!intersect && deviation < Math.Min(12, sway / 3.0) ? 1000 : 0);
                if (score >= bestScore) continue;
                best = cells;
                bestScore = score;
            }
            if (best is null) return false;
            foreach (var cell in best) _map.SetRoad(cell.X, cell.Y, _highway);
            if (!intersect) _trunks.Add(best);
            return true;
        }

        private bool CleanContacts(IReadOnlyList<Pos> cells)
        {
            var pending = cells.ToHashSet();
            bool Road(int x, int y) => _map.HasRoad(x, y) || pending.Contains(new(x, y));
            foreach (var cell in cells)
                foreach (int dx in new[] { -1, 1 })
                    foreach (int dy in new[] { -1, 1 })
                        if (Road(cell.X + dx, cell.Y + dy) &&
                            !Road(cell.X + dx, cell.Y) && !Road(cell.X, cell.Y + dy)) return false;
            return true;
        }

        private void AddStraightFallback(bool horizontal, int center, int sway)
        {
            int length = horizontal ? _width : _height;
            int across = horizontal ? _height : _width;
            List<Pos>? best = null;
            int bestScore = int.MaxValue;
            for (int lane = Math.Max(1, center - sway); lane <= Math.Min(across - 2, center + sway); lane++)
            {
                var cells = Enumerable.Range(0, length).Select(t => horizontal ? new Pos(t, lane) : new Pos(lane, t)).ToList();
                int score = cells.Count(p => !Open(p.X, p.Y)) * 100 + Math.Abs(lane - center);
                if (score >= bestScore) continue;
                best = cells;
                bestScore = score;
            }
            if (best is null) return;
            foreach (var cell in best) _map.SetRoad(cell.X, cell.Y, _highway);
            _trunks.Add(best);
        }

        private void AddWaterfrontStreetStubs()
        {
            var distance = new int[_width * _height];
            Array.Fill(distance, int.MaxValue);
            var queue = new Queue<Pos>();
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    if (_map.TerrainAt(x, y).Name == DefaultTerrains.WaterName)
                    {
                        distance[Index(x, y)] = 0;
                        queue.Enqueue(new(x, y));
                    }
            if (queue.Count == 0) return;
            while (queue.TryDequeue(out var cell))
                foreach (var (dx, dy) in Dirs)
                {
                    int x = cell.X + dx, y = cell.Y + dy;
                    if (!InBounds(x, y) || distance[Index(x, y)] != int.MaxValue) continue;
                    distance[Index(x, y)] = distance[Index(cell.X, cell.Y)] + 1;
                    queue.Enqueue(new(x, y));
                }
            var geometry = new RoadGeometry(_map, RoadCurves.Extract(_map, (_, _) => true));
            var junctions = _map.RoadCells.Select(_map.PosOf).Where(p => RoadNeighbors(_map, p) >= 3).ToList();
            int desired = Math.Clamp((int)Math.Ceiling(Math.Sqrt(_width * (double)_height) / 100) + 1, 3, 6);
            int spacing = Math.Clamp(Math.Min(_width, _height) / 12, 10, 24);
            var spurs = _map.RoadCells.Select(_map.PosOf).Where(p => _map.RoadTypeAt(p.X, p.Y) == _street &&
                RoadNeighbors(_map, p) == 1 && distance[Index(p.X, p.Y)] <= 6).ToList();
            bool StartAllowed(Pos start) => !junctions.Any(p => p != start &&
                Cheb(p.X, p.Y, start.X, start.Y) <= 3);
            bool RoomToSettle(Pos end, RoadGeometry? roads = null)
            {
                foreach (int dx in new[] { -4, 1 })
                    foreach (int dy in new[] { -4, 1 })
                        if (new CellRect(end.X + dx, end.Y + dy, 4, 4).Cells().All(p =>
                            InBounds(p.X, p.Y) && Open(p.X, p.Y) &&
                            _map.TerrainAt(p.X, p.Y).Name != DefaultTerrains.HillName &&
                            !(roads ?? geometry).OverlapsCell(p.X, p.Y)))
                            return true;
                return false;
            }
            bool CommitWaterfront(IReadOnlyList<Pos> stroke)
            {
                var end = stroke[^1];
                if (_map.RoadTypeAt(stroke[0].X, stroke[0].Y) != _highway ||
                    !StartAllowed(stroke[0]) || spurs.Any(p => Cheb(p.X, p.Y, end.X, end.Y) < spacing) ||
                    Dirs.Any(d => _map.HasRoad(end.X + d.Dx, end.Y + d.Dy))) return false;
                var plan = RoadRules.Plan(_map, stroke, _street.Rank, Open);
                if (plan.Count != stroke.Count - 1) return false;
                var planned = plan.ToDictionary(p => Index(p.X, p.Y), _ => _street);
                var proposed = new RoadGeometry(_map, RoadCurves.Extract(_map, (_, _) => true, planned));
                if (!RoomToSettle(end, proposed) || spurs.Any(spur => !RoomToSettle(spur, proposed))) return false;
                foreach (var p in plan) _map.SetRoad(p.X, p.Y, _street);
                spurs.Add(end);
                junctions.Add(stroke[0]);
                geometry = proposed;
                return true;
            }
            if (spurs.Count >= desired) return;

            var candidates = new List<(int Score, Pos[] Stroke)>();
            int maximumLength = Math.Clamp(Math.Min(_width, _height) / 3, 12, 64);
            foreach (var start in _map.RoadCells.Select(_map.PosOf).Where(p =>
                _map.RoadTypeAt(p.X, p.Y) == _highway && Open(p.X, p.Y) && StartAllowed(p)))
                foreach (var (dx, dy) in Dirs)
                {
                    var stroke = new List<Pos> { start };
                    for (int length = 1; length <= maximumLength; length++)
                    {
                        var end = new Pos(start.X + dx * length, start.Y + dy * length);
                        if (!InBounds(end.X, end.Y) || !Open(end.X, end.Y) || _map.HasRoad(end.X, end.Y)) break;
                        stroke.Add(end);
                        int waterDistance = distance[Index(end.X, end.Y)];
                        if (length < 6 || OnBoundary(end.X, end.Y) || waterDistance > 6 || !RoomToSettle(end)) continue;
                        int centerDistance = Math.Abs(end.X - _width / 2) + Math.Abs(end.Y - _height / 2);
                        int score = length * 2 + centerDistance + Math.Abs(waterDistance - 4) * 2;
                        candidates.Add((score, stroke.ToArray()));
                    }
                }
            foreach (var (_, stroke) in candidates.OrderBy(c => c.Score).ThenBy(c => c.Stroke[0].Y)
                .ThenBy(c => c.Stroke[0].X))
                if (CommitWaterfront(stroke) && spurs.Count >= desired) return;
            var parents = new int[distance.Length];
            var steps = new int[distance.Length];
            Array.Fill(parents, -1);
            foreach (var start in _map.RoadCells.Select(_map.PosOf).Where(p =>
                _map.RoadTypeAt(p.X, p.Y) == _highway && Open(p.X, p.Y) && StartAllowed(p)).OrderBy(p => p.Y).ThenBy(p => p.X))
                foreach (var (dx, dy) in Dirs)
                {
                    var next = new Pos(start.X + dx, start.Y + dy);
                    if (!InBounds(next.X, next.Y) || !Open(next.X, next.Y) || _map.HasRoad(next.X, next.Y) ||
                        parents[Index(next.X, next.Y)] >= 0 ||
                        RoadRules.Plan(_map, new[] { start, next }, _street.Rank, Open).Count != 1) continue;
                    parents[Index(next.X, next.Y)] = Index(start.X, start.Y);
                    steps[Index(next.X, next.Y)] = 1;
                    queue.Enqueue(next);
                }
            int maximumReach = Math.Clamp(Math.Min(_width, _height) / 2, 24, 192);
            while (queue.TryDequeue(out var cell))
            {
                if (_map.HasRoad(cell.X, cell.Y)) continue;
                int index = Index(cell.X, cell.Y);
                if (steps[index] >= 3 && distance[index] <= 6 && RoomToSettle(cell))
                {
                    var stroke = new List<Pos> { cell };
                    int parent = parents[index];
                    while (parent >= 0)
                    {
                        stroke.Add(_map.PosOf(parent));
                        if (_map.HasRoad(parent % _width, parent / _width)) break;
                        parent = parents[parent];
                    }
                    stroke.Reverse();
                    if (CommitWaterfront(stroke) && spurs.Count >= desired) return;
                }
                if (steps[index] >= maximumReach) continue;
                foreach (var (dx, dy) in Dirs)
                {
                    int x = cell.X + dx, y = cell.Y + dy;
                    if (!InBounds(x, y) || !Open(x, y) || parents[Index(x, y)] >= 0 ||
                        _map.HasRoad(x, y) || Dirs.Any(d => _map.HasRoad(x + d.Dx, y + d.Dy))) continue;
                    parents[Index(x, y)] = index;
                    steps[Index(x, y)] = steps[index] + 1;
                    queue.Enqueue(new(x, y));
                }
            }
        }

        private void AddFallbackStreetStub()
        {
            var junctions = _map.RoadCells.Select(_map.PosOf).Where(p =>
                Dirs.Count(d => _map.HasRoad(p.X + d.Dx, p.Y + d.Dy)) >= 3).ToArray();
            var starts = _map.RoadCells.OrderBy(i =>
                Math.Abs(i % _width - _width / 2) + 2 * Math.Abs(i / _width - _height / 2)).ToArray();
            foreach (int index in starts)
            {
                var start = _map.PosOf(index);
                if (junctions.Any(p => Math.Abs(start.X - p.X) <= 2 * ClearanceX &&
                    Math.Abs(start.Y - p.Y) <= 2 * ClearanceY)) continue;
                foreach (var (dx, dy) in Dirs)
                {
                    var stroke = Enumerable.Range(0, 7).Select(step => new Pos(start.X + dx * step, start.Y + dy * step)).ToArray();
                    if (stroke.Any(p => !InBounds(p.X, p.Y) || !Open(p.X, p.Y)) ||
                        OnBoundary(stroke[^1].X, stroke[^1].Y)) continue;
                    var plan = RoadRules.Plan(_map, stroke, _street.Rank, Open);
                    if (plan.Count != stroke.Length - 1) continue;
                    foreach (var p in plan) _map.SetRoad(p.X, p.Y, _street);
                    return;
                }
            }
        }
    }
}
