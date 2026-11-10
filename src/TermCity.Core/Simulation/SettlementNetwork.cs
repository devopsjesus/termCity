using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    private WallNetwork? _network;
    private WallNetwork? _artworkNetwork;
    private readonly Dictionary<int, float> _wallExteriorSides = [];
    private readonly Dictionary<int, (Vector2 Position, Vector2 Inward)?> _groundLadders = [];
    private readonly Dictionary<int, IReadOnlyList<TowerWalkwayLadder>> _walkwayLadders = [];
    internal IReadOnlyList<TowerWalkwayLadder> TowerWalkwayLadders(WallComponent tower)
    {
        if (!_walkwayLadders.TryGetValue(tower.Id, out var ladders))
            _walkwayLadders[tower.Id] = ladders = WallDamageLayout.ComputeWalkwayLadders(tower);
        return ladders;
    }
    private int _topologyRevision, _territoryRevision = -1, _territoryMapVersion = -1;
    private readonly HashSet<Pos> _eligibleLand = [];
    internal readonly HashSet<Pos> EstablishedLand = [];
    internal int NextWallId = 1;
    internal GameMap? AttachedMap;
    private Dictionary<Pos, List<WallComponent>>? _wallSpatial;
    private readonly Dictionary<(Pos A, Pos B), bool> _walkLinks = [];
    private readonly Dictionary<Pos, bool> _reserved = [];
    private readonly Dictionary<Pos, float> _wallDistances = [];
    private int _eligibleArea;
    private CellRect _growthBounds;
    private bool? _onlyOriginalWalls;
    private bool _changingWalls, _wallTopologyPending, _wallStructurePending;
    public bool UsesFoundingLayout => _onlyOriginalWalls ??=
        WallList.Count == 2 * Bounds.Width + 2 * Bounds.Height - 4 && WallList.All(w => w.IsOriginal);
    public WallNetwork WallNetwork => _network ??= new WallNetwork(NetworkSpans(),
        new(Bounds.X + Bounds.Width / 2f, Bounds.Y + Bounds.Height / 2f),
        NetworkSpans().Where(s => WallList.Any(w => w.Id == s.Id / 4 && w.BelongsToFoundingWall)).Select(s => s.Id).ToHashSet());
    public CellRect WallBounds
    {
        get
        {
            var points = WallList.SelectMany(w => w.Start is { } a && w.End is { } b ? new[] { a, b } : new[] { w.Center }).ToArray();
            if (points.Length == 0) return Bounds;
            int x = (int)MathF.Floor(points.Min(p => p.X)), y = (int)MathF.Floor(points.Min(p => p.Y));
            return new(x, y, (int)MathF.Ceiling(points.Max(p => p.X)) - x + 1,
                (int)MathF.Ceiling(points.Max(p => p.Y)) - y + 1);
        }
    }

    internal void InvalidateWallTopology(bool structureChanged = true)
    {
        if (_changingWalls)
        {
            _wallTopologyPending = true;
            _wallStructurePending |= structureChanged;
            return;
        }
        if (structureChanged)
        {
            _artworkNetwork = null;
            _wallExteriorSides.Clear();
            _groundLadders.Clear();
            InvalidateAttackGeometry();
        }
        _network = null;
        _walkwayLadders.Clear();
        _wallSpatial = null;
        _walkLinks.Clear();
        _reserved.Clear();
        _wallDistances.Clear();
        _defenderPathCells.Clear();
        _defenderPathEdges.Clear();
        _defenderInteriorCells.Clear();
        _defenderWalkHeights.Clear();
        _archerRangePosts.Clear();
        _archerReachability.Clear();
        _defenderPathNavigation = -1;
        _garrisonNavigationVersion = -1;
        _onlyOriginalWalls = null;
        _topologyRevision++;
        _territoryRevision = -1;
        AttachedMap?.InvalidateTravel();
        foreach (var defender in DefenderList)
        {
            defender.Path.Clear();
            defender.PathIndex = 0;
            defender.PathTarget = null;
        }
    }

    internal void ChangeWalls(Action change)
    {
        if (_changingWalls) throw new InvalidOperationException("Wall changes cannot be nested.");
        // Mutation callbacks must not query topology caches before the commit.
        _changingWalls = true;
        try { change(); }
        finally
        {
            _changingWalls = false;
            if (_wallTopologyPending)
            {
                bool structureChanged = _wallStructurePending;
                _wallTopologyPending = _wallStructurePending = false;
                InvalidateWallTopology(structureChanged);
            }
        }
    }

    private bool StructuralInterior(Vector2 point)
    {
        var root = new Vector2(Bounds.X + Bounds.Width / 2f, Bounds.Y + Bounds.Height / 2f);
        _artworkNetwork ??= new WallNetwork(NetworkSpans().Select(span => span with { Intact = true }), root);
        return _artworkNetwork.ContainsAny(point);
    }

    public (Vector2 Position, Vector2 Inward)? GroundLadder(WallComponent tower)
    {
        if (_groundLadders.TryGetValue(tower.Id, out var cached)) return cached;
        var root = new Vector2(Bounds.X + Bounds.Width / 2f, Bounds.Y + Bounds.Height / 2f);
        var preferred = new Vector2(tower.Tangent.Y, -tower.Tangent.X) * WallDamageLayout.ExteriorSide(tower);
        var candidates = new List<(Vector2 Position, Vector2 Inward)>();
        foreach (var direction in new[] { preferred, -preferred, tower.Tangent, -tower.Tangent })
            foreach (float shift in new[] { 0f, .8f, -.8f })
            {
                var across = new Vector2(-direction.Y, direction.X);
                var position = tower.Center + direction * (WallDamageLayout.Depth(tower) / 2 +
                    WallDamageLayout.LadderWallOffset) + across * shift;
                var landing = position + direction * WallDamageLayout.LadderClimberOffset;
                if (new[] { -.34f, 0, .34f }.All(side => StructuralInterior(landing + across * side)) &&
                    (AttachedMap is null || AttachedMap.InBounds((int)landing.X, (int)landing.Y) &&
                        AttachedMap.TerrainAt((int)landing.X, (int)landing.Y).Buildable) &&
                    !GroundBlocked(landing))
                    candidates.Add((position, direction));
            }
        var best = candidates.OrderBy(candidate =>
            Vector2.DistanceSquared(candidate.Position, root)).Select(candidate =>
                ((Vector2 Position, Vector2 Inward)?)candidate).FirstOrDefault();
        _groundLadders[tower.Id] = best;
        return best;
    }

    public float WallExteriorSide(WallComponent wall, IReadOnlyList<IReadOnlyList<Vector2>>? proposedEnclosures = null)
    {
        bool owned = ReferenceEquals(wall.Settlement, this) && proposedEnclosures is null;
        if (owned && _wallExteriorSides.TryGetValue(wall.Id, out float cached)) return cached;
        var root = new Vector2(Bounds.X + Bounds.Width / 2f, Bounds.Y + Bounds.Height / 2f);
        _artworkNetwork ??= new WallNetwork(NetworkSpans().Select(span => span with { Intact = true }), root);
        bool Inside(Vector2 point) => _artworkNetwork.ContainsAny(point) ||
            proposedEnclosures?.Any(polygon => World.WallNetwork.PointInside(polygon, point)) == true;
        var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
        bool positive = Inside(wall.Center + normal * .05f);
        bool negative = Inside(wall.Center - normal * .05f);
        float side;
        if (positive != negative) side = positive ? -1 : 1;
        else
        {
            float towardRoot = Vector2.Dot(root - wall.Center, normal);
            if (Math.Abs(towardRoot) < World.WallNetwork.Tolerance)
                towardRoot = Math.Abs(normal.Y) > World.WallNetwork.Tolerance ? normal.Y : normal.X;
            side = towardRoot > 0 ? -1 : 1;
        }
        if (owned) _wallExteriorSides[wall.Id] = side;
        return side;
    }

    public IEnumerable<WallNetworkSpan> NetworkSpans()
    {
        foreach (var wall in WallList)
        {
            if (wall.JunctionTower) continue;
            bool gate = wall.IsGate || wall.IsGateWing;
            if (wall.Start is { } a && wall.End is { } b)
            {
                yield return new(wall.Id * 4, a, b, gate, wall.BlocksEnclosure);
                continue;
            }
            var center = wall.Center;
            bool corner = (wall.Cell.X == Bounds.X || wall.Cell.X == Bounds.Right) &&
                (wall.Cell.Y == Bounds.Y || wall.Cell.Y == Bounds.Bottom);
            if (corner)
            {
                yield return new(wall.Id * 4, center, center + new Vector2(wall.Cell.X == Bounds.X ? .5f : -.5f, 0), gate, wall.BlocksEnclosure);
                yield return new(wall.Id * 4 + 1, center, center + new Vector2(0, wall.Cell.Y == Bounds.Y ? .5f : -.5f), gate, wall.BlocksEnclosure);
            }
            else yield return new(wall.Id * 4, center - wall.Tangent * .5f, center + wall.Tangent * .5f, gate, wall.BlocksEnclosure);
        }
    }

    private void EnsureWallSpatial()
    {
        if (_wallSpatial is null)
        {
            _wallSpatial = [];
            foreach (var wall in WallList)
            {
                var half = WallDamageLayout.HalfSize(wall);
                for (int y = (int)MathF.Floor(wall.Center.Y - half.Y); y <= (int)MathF.Floor(wall.Center.Y + half.Y); y++)
                    for (int x = (int)MathF.Floor(wall.Center.X - half.X); x <= (int)MathF.Floor(wall.Center.X + half.X); x++)
                    {
                        var cell = new Pos(x, y);
                        if (!_wallSpatial.TryGetValue(cell, out var values)) _wallSpatial.Add(cell, values = []);
                        values.Add(wall);
                    }
            }
        }
    }

    internal IEnumerable<WallComponent> WallsNear(Vector2 point, float reach = 0)
    {
        EnsureWallSpatial();
        var seen = new HashSet<int>();
        for (int y = (int)MathF.Floor(point.Y - reach); y <= (int)MathF.Floor(point.Y + reach); y++)
            for (int x = (int)MathF.Floor(point.X - reach); x <= (int)MathF.Floor(point.X + reach); x++)
                if (_wallSpatial!.TryGetValue(new(x, y), out var values))
                    foreach (var wall in values)
                        if (seen.Add(wall.Id)) yield return wall;
    }

    public bool FortificationReserved(Pos p, float clearance = WorldScale.FortificationClearance)
    {
        if (UsesFoundingLayout && clearance == WorldScale.FortificationClearance)
            return WorldScale.FortificationReserved(Bounds, p);
        if (clearance == WorldScale.FortificationClearance && _reserved.TryGetValue(p, out bool cached)) return cached;
        bool result = WallsNear(new(p.X + .5f, p.Y + .5f), clearance + 1).Any(w =>
            WallDamageLayout.ContainsFootprint(w, p.X + .5, p.Y + .5,
                w.IsOriginal && clearance == WorldScale.FortificationClearance ? w.IsTower ? 1.25f : 2 : clearance + .5f));
        if (clearance == WorldScale.FortificationClearance) _reserved[p] = result;
        return result;
    }

    public bool GroundBlocked(Vector2 point, double radius = WorldScale.HumanRadius, bool allowGates = true)
    {
        EnsureWallSpatial();
        var first = new Pos((int)MathF.Floor(point.X - (float)radius), (int)MathF.Floor(point.Y - (float)radius));
        var last = new Pos((int)MathF.Floor(point.X + (float)radius), (int)MathF.Floor(point.Y + (float)radius));
        if (first == last)
        {
            if (!_wallSpatial!.TryGetValue(first, out var walls)) return false;
            foreach (var wall in walls)
                if (BlocksGroundAt(wall, point, radius, allowGates)) return true;
            return false;
        }
        foreach (var wall in WallsNear(point, (float)radius))
            if (BlocksGroundAt(wall, point, radius, allowGates)) return true;
        return false;
    }

    private static bool BlocksGroundAt(WallComponent wall, Vector2 point, double radius, bool allowGates) =>
        Math.Abs(wall.Center.X - point.X) <= WallDamageLayout.HalfSize(wall).X + radius &&
        Math.Abs(wall.Center.Y - point.Y) <= WallDamageLayout.HalfSize(wall).Y + radius &&
        WallDamageLayout.BlocksGround(wall, point.X, point.Y, radius, allowGates);

    public bool CanWalk(Vector2 a, Vector2 b)
    {
        EnsureWallSpatial();
        var first = new Pos((int)MathF.Floor(a.X), (int)MathF.Floor(a.Y));
        var last = new Pos((int)MathF.Floor(b.X), (int)MathF.Floor(b.Y));
        bool gridStep = Math.Abs(a.X - first.X - .5f) < .00001f && Math.Abs(a.Y - first.Y - .5f) < .00001f &&
            Math.Abs(b.X - last.X - .5f) < .00001f && Math.Abs(b.Y - last.Y - .5f) < .00001f &&
            Math.Abs(first.X - last.X) + Math.Abs(first.Y - last.Y) <= 1;
        if (gridStep && !_wallSpatial!.ContainsKey(first) && !_wallSpatial.ContainsKey(last)) return true;
        if (gridStep && _walkLinks.TryGetValue((first, last), out bool cached)) return cached;
        bool result = !GroundBlocked(b) && !WallNetwork.SegmentBlocked(a, b);
        if (gridStep) _walkLinks[(first, last)] = result;
        return result;
    }

    public bool EstablishedAt(Pos p) => EstablishedLand.Contains(p) ||
        p.X > Bounds.X && p.X < Bounds.Right && p.Y > Bounds.Y && p.Y < Bounds.Bottom;

    internal int TopologyRevision => _topologyRevision;

    public float DistanceToWall(Pos p)
    {
        if (_wallDistances.TryGetValue(p, out float value)) return value;
        var point = new Vector2(p.X + .5f, p.Y + .5f);
        float distance = float.PositiveInfinity;
        foreach (var wall in WallList)
            distance = Math.Min(distance, World.WallNetwork.PointSegmentDistance(point,
                wall.Start ?? wall.Center - wall.Tangent * .5f, wall.End ?? wall.Center + wall.Tangent * .5f));
        _wallDistances.Add(p, distance);
        return distance;
    }

    internal bool OuterBarrier(WallComponent wall)
    {
        var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
        var a = wall.Center + normal * 2;
        var b = wall.Center - normal * 2;
        return EstablishedAt(new((int)MathF.Floor(a.X), (int)MathF.Floor(a.Y))) !=
            EstablishedAt(new((int)MathF.Floor(b.X), (int)MathF.Floor(b.Y)));
    }

    public bool AcceptsArrivals(CityGame game, Pos p)
    {
        RefreshTerritory(game);
        return _eligibleLand.Contains(p);
    }

    public int EligibleArea(CityGame game) { RefreshTerritory(game); return _eligibleArea; }
    public CellRect GrowthBounds(CityGame game) { RefreshTerritory(game); return _growthBounds; }

    internal void RefreshTerritory(CityGame game)
    {
        if (_territoryRevision == _topologyRevision && _territoryMapVersion == game.MapVersion) return;
        _territoryRevision = _topologyRevision;
        _territoryMapVersion = game.MapVersion;
        _eligibleLand.Clear();
        var bounds = WallBounds;
        var reached = ReachableLand(game);
        bool gateAccess = WallList.Where(w => w.IsGate && w.BlocksEnclosure).Any(gate =>
            reached.Any(p => Vector2.DistanceSquared(new(p.X + .5f, p.Y + .5f), gate.Center) < 1));
        var accessibleRegions = WallNetwork.Regions.Where(region => gateAccess && region.ConnectedToRoot &&
            reached.Any(p => region.Contains(new(p.X + .5f, p.Y + .5f)))).ToArray();
        int left = Math.Max(0, bounds.X - 4), top = Math.Max(0, bounds.Y - 4);
        int right = Math.Min(game.Map.Width - 1, bounds.Right + 4), bottom = Math.Min(game.Map.Height - 1, bounds.Bottom + 4);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                var p = new Pos(x, y);
                if (!game.Map.TerrainAt(x, y).Buildable ||
                    !accessibleRegions.Any(region => region.Contains(new(x + .5f, y + .5f)))) continue;
                if (!reached.Contains(p) && (game.Map.BuildingAt(x, y) is null ||
                    !game.Map.BuildingFootprintAt(x, y).Cells().SelectMany(Neighbors).Any(reached.Contains))) continue;
                _eligibleLand.Add(p);
                EstablishedLand.Add(p);
            }
        _eligibleArea = _eligibleLand.Count(p => !FortificationReserved(p));
        if (_eligibleLand.Count == 0) _growthBounds = Bounds;
        else
        {
            int x = _eligibleLand.Min(p => p.X), y = _eligibleLand.Min(p => p.Y);
            _growthBounds = new(x, y, _eligibleLand.Max(p => p.X) - x + 1, _eligibleLand.Max(p => p.Y) - y + 1);
        }
    }

    internal HashSet<Pos> ReachableLand(CityGame game, CellRect? proposed = null)
        => ReachableLand(game.Map, proposed);

    internal HashSet<Pos> ReachableLand(GameMap map, CellRect? proposed = null)
    {
        var bounds = WallBounds;
        bool Passable(Pos p) => map.InBounds(p) && TravelRoutes.Walkable(map, map.Index(p.X, p.Y)) &&
            proposed?.Contains(p) != true && map.BuildingAt(p.X, p.Y) is null &&
            !GroundBlocked(new(p.X + .5f, p.Y + .5f));
        var starts = WallList.Where(w => w.IsGate && w.Health > 0).Select(w => w.Cell)
            .Where(Passable).Distinct().ToArray();
        var reached = new HashSet<Pos>(starts);
        var queue = new Queue<Pos>(starts);
        int left = Math.Max(0, bounds.X - 4), top = Math.Max(0, bounds.Y - 4);
        int right = Math.Min(map.Width - 1, bounds.Right + 4), bottom = Math.Min(map.Height - 1, bounds.Bottom + 4);
        while (queue.TryDequeue(out var p))
            foreach (var next in Neighbors(p))
                if (next.X >= left && next.X <= right && next.Y >= top && next.Y <= bottom &&
                    !reached.Contains(next) && Passable(next) && CanWalk(new(p.X + .5f, p.Y + .5f), new(next.X + .5f, next.Y + .5f)))
                {
                    reached.Add(next);
                    queue.Enqueue(next);
                }
        return reached;
    }

    internal void AddWall(WallComponent wall)
    {
        wall.Settlement = this;
        NextWallId = Math.Max(NextWallId, wall.Id + 1);
        WallList.Add(wall);
        _wallAt.TryAdd(wall.Cell, wall);
        InvalidateWallTopology();
        Revision++;
    }

    internal void ClearWallNetwork()
    {
        WallList.Clear();
        _wallAt.Clear();
        InvalidateWallTopology();
    }

    internal void RemoveWall(WallComponent wall)
    {
        WallList.Remove(wall);
        _attackFragments.Remove(wall.Id);
        foreach (var defender in DefenderList.Where(d => d.PatrolWallId == wall.Id)) defender.PatrolWallId = null;
        ProjectileList.RemoveAll(shot => shot.Id == 0 && (int)shot.StartX == wall.Cell.X && (int)shot.StartY == wall.Cell.Y);
        _wallAt.Remove(wall.Cell);
        if (WallList.FirstOrDefault(w => w.Cell == wall.Cell) is { } replacement) _wallAt.Add(wall.Cell, replacement);
        InvalidateWallTopology();
        _upgradeSectionIndices = null;
        Revision++;
    }

    internal void DetachHealthSection(WallComponent wall)
    {
        var group = WallList.Where(w => w.SectionAnchor == wall.SectionAnchor).ToArray();
        double fraction = wall.Health / wall.MaxHealth;
        bool animation = wall.RepairAnimationActive;
        foreach (var member in group)
        {
            var material = MaterialFor(member);
            member.SharedSection = null;
            member.SectionBounds = CellRect.Single(member.Cell);
            member.BuiltMaterial = material;
            member.MaxHealth = MaterialHealth(material) * member.HealthUnits;
            member.Health = fraction * member.MaxHealth;
            member.RepairAnimationActive = animation;
        }
        _upgradeSectionIndices = null;
    }
}
