using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class CityGame
{
    private (int Version, int Topology, int Navigation, int Paths) _settlementGrowthVersion = (-1, -1, -1, -1);
    private readonly Dictionary<ZoneType, List<(BuildingType Type, CellRect Area, double Score)>> _settlementCandidates = [];
    private readonly Dictionary<(CellRect Area, int Owner), string?> _settlementAccessErrors = [];
    private readonly Dictionary<ZoneType, IReadOnlyList<CellRect>> _settlementGrowthSites = [];
    private readonly Dictionary<(Pos? Cell, ZoneType Zone), (bool Candidate, string? Error)> _settlementSiteChecks = [];
    private HashSet<Pos>? _settlementReachable;
    private Dictionary<Pos, Pos[]>? _settlementAccessLinks;
    private readonly Dictionary<CellRect, int[,]> _settlementFreeCells = [];

    private void RefreshSettlementGrowthCache()
    {
        var version = (_version, Settlement?.TopologyRevision ?? -1, Map.NavigationVersion, Map.FootpathVersion);
        if (_settlementGrowthVersion == version) return;
        _settlementGrowthVersion = version;
        _settlementCandidates.Clear();
        _settlementAccessErrors.Clear();
        _settlementGrowthSites.Clear();
        _settlementSiteChecks.Clear();
        _settlementReachable = null;
        _settlementAccessLinks = null;
        _settlementFreeCells.Clear();
    }

    public bool InsideSettlement(Pos p) => Settlement?.EstablishedAt(p) == true;

    private int WallDistance(CellRect bounds, CellRect area)
    {
        if (Settlement is not { } state || state.UsesFoundingLayout)
        {
            bounds = Settlement?.Bounds ?? bounds;
            return Math.Min(Math.Min(area.X - bounds.X, bounds.Right - area.Right),
                Math.Min(area.Y - bounds.Y, bounds.Bottom - area.Bottom));
        }
        if (state.Walls.Count == 0) return int.MaxValue;
        return (int)Math.Floor(area.Cells().Min(state.DistanceToWall));
    }

    public double SettlementLandValue(Pos p)
    {
        if (Settlement is not { } settlement) return CityAnalysis.LandValue(this, Map.Index(p.X, p.Y));
        var bounds = settlement.Bounds;
        double centerDistance = Math.Abs(p.X - (bounds.X + bounds.Width / 2.0)) +
            Math.Abs(p.Y - (bounds.Y + bounds.Height / 2.0));
        double prestige = 22 * Math.Max(0, 1 - centerDistance / ((bounds.Width + bounds.Height) / 3.0));
        return Math.Clamp(CityAnalysis.LandValue(this, Map.Index(p.X, p.Y)) + prestige -
            (WallDistance(bounds, CellRect.Single(p)) <= 3 ? 12 : 0), 0, 100);
    }

    private bool SettlementSiteAvailable(CellRect bounds, CellRect area, bool wallHousing = false,
        int owner = -1, CellRect[]? reserved = null)
    {
        int wallDistance = WallDistance(bounds, area);
        if (Settlement?.UsesFoundingLayout != false && (wallDistance < 1 || area.X <= bounds.X || area.Y <= bounds.Y ||
            area.Right >= bounds.Right || area.Bottom >= bounds.Bottom)) return false;
        // The inner repair alley stays open even when lean-tos occupy the wall face.
        if (wallHousing ? wallDistance != 3 : wallDistance < 3) return false;
        if (area.Cells().Any(p => Settlement?.FortificationReserved(p) ?? WorldScale.FortificationReserved(bounds, p))) return false;
        if (reserved is not null && reserved.Any(other => area.Cells().Any(p => other.Contains(p.X, p.Y))))
            return false;
        if (Settlement is { } settlement)
        {
            if (settlement.Walls.Where(w => w.IsGate).Any(gate =>
                area.Cells().Any(p => Math.Abs(p.X - gate.Cell.X) + Math.Abs(p.Y - gate.Cell.Y) <= 3)))
                return false;
            if (wallHousing && settlement.Walls.Any(w => (w.IsTower || w.IsGate) &&
                area.Cells().Any(p => Math.Abs(p.X - w.Cell.X) + Math.Abs(p.Y - w.Cell.Y) <= 1)))
                return false;
        }
        return area.Cells().All(p => Map.InBounds(p) && Map.TerrainAt(p.X, p.Y).Buildable &&
            !Map.Footbridges.Any(bridge => bridge.Cells().Contains(p)) &&
            !Map.HasRoad(p.X, p.Y) && !Network.RoadOverlapsCell(Map.Index(p.X, p.Y)) &&
            Map.PathWearAt(p.X, p.Y) < 3 &&
            (Map.BuildingAt(p.X, p.Y) is null || Map.BuildingAnchor(Map.Index(p.X, p.Y)) == owner)) &&
            (Settlement is not { } current || area.Cells().All(p => current.AcceptsArrivals(this, p)));
    }

    private List<CellRect> SettlementSites(CellRect bounds, BuildingType type, CellRect[]? reserved = null, bool packed = true,
        IReadOnlySet<Pos>? unavailable = null)
    {
        RefreshSettlementGrowthCache();
        if (reserved is not null || !_settlementFreeCells.TryGetValue(bounds, out var blocked))
        {
            blocked = new int[bounds.Height + 1, bounds.Width + 1];
            var state = Settlement;
            var network = Network;
            var gates = state?.Walls.Where(wall => wall.IsGate).Select(wall => wall.Cell).ToArray() ?? [];
            var bridges = Map.Footbridges.SelectMany(bridge => bridge.Cells()).ToHashSet();
            for (int row = 0; row < bounds.Height; row++)
                for (int column = 0; column < bounds.Width; column++)
                {
                    var cell = new Pos(bounds.X + column, bounds.Y + row);
                    bool free = Map.InBounds(cell) && Map.BuildingAt(cell.X, cell.Y) is null &&
                        Map.TerrainAt(cell.X, cell.Y).Buildable && !bridges.Contains(cell) &&
                        !Map.HasRoad(cell.X, cell.Y) && Map.PathWearAt(cell.X, cell.Y) < 3 &&
                        WallDistance(bounds, CellRect.Single(cell)) >= 3 &&
                        (state?.UsesFoundingLayout == false || cell.X > bounds.X && cell.Y > bounds.Y &&
                            cell.X < bounds.Right && cell.Y < bounds.Bottom) &&
                        !(state?.FortificationReserved(cell) ?? WorldScale.FortificationReserved(bounds, cell)) &&
                        !network.RoadOverlapsCell(Map.Index(cell.X, cell.Y));
                    if (free)
                        foreach (var gate in gates)
                            if (Math.Abs(cell.X - gate.X) + Math.Abs(cell.Y - gate.Y) <= 3) { free = false; break; }
                    if (free && reserved is not null)
                        foreach (var area in reserved)
                            if (area.Contains(cell.X, cell.Y)) { free = false; break; }
                    if (free && state is not null) free = state.AcceptsArrivals(this, cell);
                    int occupied = free ? 0 : 1;
                    blocked[row + 1, column + 1] = occupied + blocked[row, column + 1] +
                        blocked[row + 1, column] - blocked[row, column];
                }
            if (reserved is null) _settlementFreeCells[bounds] = blocked;
        }
        var result = new List<CellRect>();
        var claimed = new HashSet<int>();
        for (int y = bounds.Y + 1; y < bounds.Bottom; y++)
            for (int x = bounds.X + 1; x < bounds.Right; x++)
            {
                var area = type.FootprintAt(x, y);
                int left = area.X - bounds.X, top = area.Y - bounds.Y;
                int right = left + area.Width, bottom = top + area.Height;
                if (right > bounds.Width || bottom > bounds.Height ||
                    blocked[bottom, right] - blocked[top, right] - blocked[bottom, left] + blocked[top, left] != 0 ||
                    type.Name == "Wall House" && WallDistance(bounds, area) != 3 ||
                    (claimed.Count > 0 || unavailable is not null) &&
                        area.Cells().Any(p => claimed.Contains(Map.Index(p.X, p.Y)) || unavailable?.Contains(p) == true))
                    continue;
                result.Add(area);
                if (packed)
                    foreach (var p in area.Cells()) claimed.Add(Map.Index(p.X, p.Y));
            }
        return result;
    }

    internal bool SettlementCrowded => Settlement is { } settlement &&
        Map.BuildingFootprints.Values.Sum(area => area.Cells().Count(p => InsideSettlement(p))) >=
            settlement.EligibleArea(this) * 0.6;

    private int GrowSettlement(ZoneType zone, int count)
    {
        int filled = 0;
        while (filled < count)
        {
            int? index = DevelopSettlementBuilding(zone);
            if (index is null && zone == ZoneType.Residential && SettlementCrowded)
            {
                RedevelopSettlement();
                // Redevelopment preserves households; arrivals use its new room, not invented residents.
                index = Map.ZoneCells(zone).Order().Where(i => Map.IsBuildingAnchor(i) &&
                    Map.BuildingAt(i % Map.Width, i / Map.Width) is { } type &&
                    type.Capacity - Map.HouseholdLayer[i].Total >= 3 && CityAnalysis.Operating(this, i) &&
                    Map.BuildingFootprintAt(i % Map.Width, i / Map.Width).Cells().All(p => Settlement!.AcceptsArrivals(this, p)))
                    .Select(i => (int?)i).FirstOrDefault();
            }
            if (index is not { } cell) break;
            if (zone == ZoneType.Residential)
            {
                var p = Map.PosOf(cell);
                var family = Config.FullRules ? Household.Migrant(Rng) : Household.Random(Rng);
                int room = Map.BuildingAt(p.X, p.Y)!.Capacity - Map.HouseholdLayer[cell].Total;
                if (family.Total > room) family = family.Scaled((double)room / family.Total);
                Map.SetHousehold(p.X, p.Y, Map.HouseholdLayer[cell].Plus(family));
                Tally.MovedIn += family.Total;
            }
            filled++;
            Invalidate(servicesChanged: true);
        }
        return filled;
    }

    internal int? DevelopSettlementBuilding(ZoneType zone)
    {
        foreach (var candidate in SettlementCandidates(zone))
        {
            if (!SettlementAccessSurvives(candidate.Area)) continue;
            Map.SetBuildingFootprint(candidate.Type, candidate.Area);
            Invalidate(servicesChanged: true);
            return Map.Index(candidate.Area.X, candidate.Area.Y);
        }
        return null;
    }

    private List<(BuildingType Type, CellRect Area, double Score)> SettlementCandidates(ZoneType zone)
    {
        RefreshSettlementGrowthCache();
        if (_settlementCandidates.TryGetValue(zone, out var cached)) return cached;
        if (Settlement is not { } settlement) return [];
        bool crowded = SettlementCrowded;
        var types = SettlementGrowthTypes(zone);
        var gates = settlement.Walls.Where(w => w.IsGate).ToArray();
        var existing = Map.BuildingFootprints.Select(pair => (Area: pair.Value,
            Type: Map.BuildingAt(pair.Value.X, pair.Value.Y)!)).ToArray();
        var candidates = new List<(BuildingType Type, CellRect Area, double Score)>();
        var siteScores = new Dictionary<Pos, (double Worth, double Home, double Building)>();
        var tradeCounts = existing.GroupBy(b => b.Type.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var type in types)
            foreach (var area in SettlementSites(settlement.GrowthBounds(this), type, packed: false))
            {
                var p = new Pos(area.X, area.Y);
                if (!siteScores.TryGetValue(p, out var site))
                {
                    site = (SettlementLandValue(p),
                        existing.Where(b => b.Type.Zone == ZoneType.Residential)
                            .Select(b => (double)(Math.Abs(b.Area.X - p.X) + Math.Abs(b.Area.Y - p.Y)))
                            .DefaultIfEmpty(6).Min(),
                        existing.Select(b => (double)(Math.Abs(b.Area.X - p.X) + Math.Abs(b.Area.Y - p.Y)))
                            .DefaultIfEmpty(6).Min());
                    siteScores.Add(p, site);
                }
                double worth = site.Worth;
                if (!SettlementTypeAllowsValue(type, worth)) continue;
                double distanceToGate = gates.Select(g => Math.Abs(p.X - g.Center.X) + Math.Abs(p.Y - g.Center.Y))
                    .DefaultIfEmpty(0).Min();
                double score = zone switch
                {
                    ZoneType.Residential => worth - Math.Abs(site.Building - 3) * 2,
                    ZoneType.Commercial => 40 - distanceToGate * 0.9 - site.Home * 1.5,
                    _ => 40 - WallDistance(settlement.Bounds, area) * 2 - distanceToGate * 0.3 + site.Home,
                };
                int sameTrade = tradeCounts.GetValueOrDefault(type.Name);
                score -= sameTrade * 4;
                if (type.Name == "Courtyard House") score += 12;
                if (type.Name == "Armory") score += 8 - WallDistance(settlement.Bounds, area);
                if (type.Name is "Wall House" or "Shanty Tower") score += crowded ? 40 : -100;
                score += CellHash.Pick(p.X + Config.Seed + type.Id * 19, p.Y - type.Id * 7, 100) / 10.0;
                candidates.Add((type, area, score));
            }
        return _settlementCandidates[zone] = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Area.Y)
            .ThenBy(c => c.Area.X).ThenBy(c => c.Type.Id).ToList();
    }

    private IEnumerable<BuildingType> SettlementGrowthTypes(ZoneType zone) =>
        Map.Content.Buildings.Where(t => t.Zone == zone &&
            (t.SettlementOnly || t == Map.Content.Buildings.ForZone(zone)) &&
            (SettlementCrowded || t.Name is not ("Wall House" or "Shanty Tower")));

    private static bool SettlementTypeAllowsValue(BuildingType type, double worth) =>
        !(type.Name == "Courtyard House" && worth < 47 ||
            type.Name is "Shanty Tower" or "Wall House" && worth >= 47);

    private IEnumerable<CellRect> SettlementDiagnosticSites(ZoneType zone, Pos? cell, bool packed,
        IReadOnlySet<Pos>? unavailable = null)
    {
        var bounds = Settlement!.GrowthBounds(this);
        var shapes = SettlementGrowthTypes(zone).GroupBy(type =>
            (type.Width, type.Height, WallHousing: type.Name == "Wall House",
             Value: type.Name == "Courtyard House" ? 1 : type.Name is "Shanty Tower" or "Wall House" ? -1 : 0));
        foreach (var shape in shapes)
        {
            var type = shape.First();
            var areas = cell is { } p
                ? new CellRect(p.X - type.Width + 1, p.Y - type.Height + 1, type.Width, type.Height).Cells()
                    .Where(anchor => anchor.X > bounds.X && anchor.Y > bounds.Y &&
                        anchor.X < bounds.Right && anchor.Y < bounds.Bottom)
                    .Select(anchor => type.FootprintAt(anchor.X, anchor.Y))
                    .Where(area => SettlementSiteAvailable(bounds, area, shape.Key.WallHousing))
                : SettlementSites(bounds, type, packed: packed, unavailable: unavailable);
            foreach (var area in areas)
                if (shape.Key.Value == 0 || SettlementTypeAllowsValue(type, SettlementLandValue(new(area.X, area.Y))))
                    yield return area;
        }
    }

    internal IReadOnlyList<CellRect> SettlementGrowthSites(ZoneType zone)
    {
        RefreshSettlementGrowthCache();
        if (_settlementGrowthSites.TryGetValue(zone, out var sites)) return sites;
        var available = new List<CellRect>();
        var occupied = new HashSet<Pos>();
        foreach (var area in SettlementDiagnosticSites(zone, null, packed: true, unavailable: occupied))
        {
            if (area.Cells().Any(occupied.Contains) || !SettlementAccessSurvives(area)) continue;
            available.Add(area);
            occupied.UnionWith(area.Cells());
        }
        if (available.Count == 0)
            foreach (var area in SettlementDiagnosticSites(zone, null, packed: false))
                if (SettlementAccessSurvives(area)) { available.Add(area); break; }
        return _settlementGrowthSites[zone] = available;
    }

    internal (bool Candidate, string? Error) SettlementGrowthSite(Pos? cell, ZoneType zone)
    {
        RefreshSettlementGrowthCache();
        if (_settlementSiteChecks.TryGetValue((cell, zone), out var cached)) return cached;
        var candidates = SettlementDiagnosticSites(zone, cell, packed: false);
        string? firstError = null;
        bool any = false;
        foreach (var candidate in candidates)
        {
            any = true;
            var error = SettlementAccessError(candidate);
            if (error is null) return _settlementSiteChecks[(cell, zone)] = (true, null);
            firstError ??= error;
        }
        return _settlementSiteChecks[(cell, zone)] = (any, firstError);
    }

    private bool SettlementAccessSurvives(CellRect proposed, int owner = -1)
        => SettlementAccessError(proposed, owner) is null;

    internal string? SettlementAccessError(CellRect proposed, int owner = -1)
    {
        RefreshSettlementGrowthCache();
        if (_settlementAccessErrors.TryGetValue((proposed, owner), out var cached)) return cached;
        _settlementReachable ??= SettlementReachable(null);
        string? ExistingError(HashSet<Pos> reached, bool alreadyBlocked) => Map.BuildingFootprints
            .Where(pair => pair.Key != owner && InsideSettlement(new(pair.Value.X, pair.Value.Y)) &&
                !BuildingAccess(pair.Value, reached))
            .Select(pair => alreadyBlocked
                ? $"{Map.BuildingAt(pair.Value.X, pair.Value.Y)!.Name} at ({pair.Value.X},{pair.Value.Y}) " +
                    "has no walkable gate route; restore access before new development."
                : $"Keep a walkable gate route to {Map.BuildingAt(pair.Value.X, pair.Value.Y)!.Name} " +
                    $"at ({pair.Value.X},{pair.Value.Y}); this site would leave it unreachable.")
            .FirstOrDefault();
        var error = ExistingError(_settlementReachable, true);
        if (error is null)
        {
            var reachable = SettlementReachable(proposed);
            error = !BuildingAccess(proposed, reachable) ? "No walkable gate route reaches this plot." : ExistingError(reachable, false);
        }
        _settlementAccessErrors[(proposed, owner)] = error;
        return error;
    }

    private static bool BuildingAccess(CellRect area, HashSet<Pos> reachable) => area.Cells().Any(p => RoadNetwork.Neighbors
        .Any(d => reachable.Contains(p.Offset(d.Dx, d.Dy))));

    private HashSet<Pos> SettlementReachable(CellRect? proposed)
    {
        if (proposed is null) return Settlement!.ReachableLand(this);
        var baseline = _settlementReachable ??= Settlement!.ReachableLand(this);
        _settlementAccessLinks ??= baseline.ToDictionary(cell => cell, cell => RoadNetwork.Neighbors
            .Select(direction => cell.Offset(direction.Dx, direction.Dy))
            .Where(next => baseline.Contains(next) && Settlement!.CanWalk(
                new(cell.X + .5f, cell.Y + .5f), new(next.X + .5f, next.Y + .5f))).ToArray());
        var starts = Settlement!.Walls.Where(w => w.IsGate && w.Health > 0).Select(w => w.Cell)
            .Where(p => baseline.Contains(p) && !proposed.Value.Contains(p)).Distinct().ToArray();
        var reached = new HashSet<Pos>(starts);
        var queue = new Queue<Pos>(starts);
        while (queue.TryDequeue(out var p))
            foreach (var next in _settlementAccessLinks[p])
                if (!proposed.Value.Contains(next) && reached.Add(next)) queue.Enqueue(next);
        return reached;
    }

    internal void RedevelopSettlement()
    {
        if (Settlement is not { } settlement) return;
        foreach (int i in Map.ZoneCells(ZoneType.Residential).Where(Map.IsBuildingAnchor).Order().ToArray())
        {
            var p = Map.PosOf(i);
            var type = Map.BuildingAt(p.X, p.Y);
            if (type is null || !CityAnalysis.Operating(this, i)) continue;
            var household = Map.HouseholdLayer[i];
            var oldArea = Map.BuildingFootprintAt(p.X, p.Y);
            if (SettlementLandValue(p) < 47) continue;
            string name = "Courtyard House";
            if (type.Name == name || type.Name == "Wall House") continue;
            var next = Map.Content.Buildings.Find(name);
            if (next is null || next.Capacity < household.Total) continue;
            var area = new CellRect(p.X, p.Y, Math.Max(oldArea.Width, next.Width), Math.Max(oldArea.Height, next.Height));
            if (!SettlementSiteAvailable(settlement.GrowthBounds(this), area, owner: i) ||
                !SettlementAccessSurvives(area, i)) continue;
            Map.SetBuildingFootprint(next, area);
            Map.SetHousehold(p.X, p.Y, household);
        }
    }
}
