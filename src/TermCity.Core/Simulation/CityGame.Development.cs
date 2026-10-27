using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class CityGame
{
    public bool InsideSettlement(Pos p) => Settlement is { } settlement &&
        p.X > settlement.Bounds.X && p.X < settlement.Bounds.Right &&
        p.Y > settlement.Bounds.Y && p.Y < settlement.Bounds.Bottom;

    private static int WallDistance(CellRect bounds, CellRect area) =>
        Math.Min(Math.Min(area.X - bounds.X, bounds.Right - area.Right),
            Math.Min(area.Y - bounds.Y, bounds.Bottom - area.Bottom));

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
        if (wallDistance < 1 || area.X <= bounds.X || area.Y <= bounds.Y ||
            area.Right >= bounds.Right || area.Bottom >= bounds.Bottom) return false;
        // The inner repair alley stays open even when lean-tos occupy the wall face.
        if (wallHousing ? wallDistance != 1 : wallDistance < 3) return false;
        if (reserved is not null && reserved.Any(other => area.Cells().Any(p => other.Contains(p.X, p.Y))))
            return false;
        if (Settlement is { } settlement)
        {
            var gate = settlement.Walls.Single(w => w.IsGate).Cell;
            if (area.Cells().Any(p => Math.Abs(p.X - gate.X) + Math.Abs(p.Y - gate.Y) <= 3))
                return false;
            if (wallHousing && settlement.Walls.Any(w => (w.IsTower || w.IsGate) &&
                area.Cells().Any(p => Math.Abs(p.X - w.Cell.X) + Math.Abs(p.Y - w.Cell.Y) <= 1)))
                return false;
        }
        return area.Cells().All(p => Map.InBounds(p) && Map.TerrainAt(p.X, p.Y).Buildable &&
            !Map.HasRoad(p.X, p.Y) && !Network.RoadOverlapsCell(Map.Index(p.X, p.Y)) &&
            Map.PathWearAt(p.X, p.Y) < 3 &&
            (Map.BuildingAt(p.X, p.Y) is null || Map.BuildingAnchor(Map.Index(p.X, p.Y)) == owner));
    }

    private List<CellRect> SettlementSites(CellRect bounds, BuildingType type, CellRect[]? reserved = null, bool packed = true)
    {
        var result = new List<CellRect>();
        var claimed = new HashSet<int>();
        for (int y = bounds.Y + 1; y < bounds.Bottom; y++)
            for (int x = bounds.X + 1; x < bounds.Right; x++)
            {
                var area = type.FootprintAt(x, y);
                if (!SettlementSiteAvailable(bounds, area, type.Name == "Wall House", reserved: reserved) ||
                    area.Cells().Any(p => claimed.Contains(Map.Index(p.X, p.Y)))) continue;
                result.Add(area);
                if (packed)
                    foreach (var p in area.Cells()) claimed.Add(Map.Index(p.X, p.Y));
            }
        return result;
    }

    internal bool SettlementCrowded => Settlement is { } settlement &&
        Map.BuildingFootprints.Values.Sum(area => area.Cells().Count(p => InsideSettlement(p))) >=
            (settlement.Bounds.Width - 6) * (settlement.Bounds.Height - 6) * 0.6;

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
                    type.Capacity - Map.HouseholdLayer[i].Total >= 3 && CityAnalysis.Operating(this, i))
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
        if (Settlement is not { } settlement) return null;
        bool crowded = SettlementCrowded;
        var types = Map.Content.Buildings.Where(t => t.Zone == zone &&
            (t.SettlementOnly || t == Map.Content.Buildings.ForZone(zone)) &&
            (crowded || t.Name is not ("Wall House" or "Shanty Tower"))).ToArray();
        var gate = settlement.Walls.Single(w => w.IsGate).Cell;
        var existing = Map.BuildingFootprints.Select(pair => (Area: pair.Value,
            Type: Map.BuildingAt(pair.Value.X, pair.Value.Y)!)).ToArray();
        var candidates = new List<(BuildingType Type, CellRect Area, double Score)>();
        var siteScores = new Dictionary<Pos, (double Worth, double Home, double Building)>();
        var tradeCounts = existing.GroupBy(b => b.Type.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var type in types)
            foreach (var area in SettlementSites(settlement.Bounds, type, packed: false))
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
                if (type.Name == "Courtyard House" && worth < 47 ||
                    type.Name is "Shanty Tower" or "Wall House" && worth >= 47) continue;
                double distanceToGate = Math.Abs(p.X - gate.X) + Math.Abs(p.Y - gate.Y);
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
                if (type.Name is "Wall House" or "Shanty Tower") score += crowded ? 16 : -100;
                score += CellHash.Pick(p.X + Config.Seed + type.Id * 19, p.Y - type.Id * 7, 100) / 10.0;
                candidates.Add((type, area, score));
            }
        foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Area.Y)
            .ThenBy(c => c.Area.X).ThenBy(c => c.Type.Id))
        {
            if (!SettlementAccessSurvives(candidate.Area)) continue;
            Map.SetBuildingFootprint(candidate.Type, candidate.Area);
            Invalidate(servicesChanged: true);
            return Map.Index(candidate.Area.X, candidate.Area.Y);
        }
        return null;
    }

    private bool SettlementAccessSurvives(CellRect proposed, int owner = -1)
    {
        var settlement = Settlement!;
        var gate = settlement.Walls.Single(w => w.IsGate).Cell;
        var start = gate.Offset(gate.X == settlement.Bounds.X ? 1 : gate.X == settlement.Bounds.Right ? -1 : 0,
            gate.Y == settlement.Bounds.Y ? 1 : gate.Y == settlement.Bounds.Bottom ? -1 : 0);
        var reachable = new HashSet<Pos>();
        var queue = new Queue<Pos>();
        if (Open(start)) { reachable.Add(start); queue.Enqueue(start); }
        while (queue.TryDequeue(out var p))
            foreach (var (dx, dy) in RoadNetwork.Neighbors)
            {
                var next = p.Offset(dx, dy);
                if (Open(next) && reachable.Add(next)) queue.Enqueue(next);
            }
        bool Access(CellRect area) => area.Cells().Any(p => RoadNetwork.Neighbors
            .Any(d => reachable.Contains(p.Offset(d.Dx, d.Dy))));
        return Access(proposed) && Map.BuildingFootprints.All(pair => pair.Key == owner ||
            !InsideSettlement(new(pair.Value.X, pair.Value.Y)) || Access(pair.Value));

        bool Open(Pos p) => InsideSettlement(p) && !proposed.Contains(p.X, p.Y) &&
            Map.TerrainAt(p.X, p.Y).Buildable && Map.BuildingAt(p.X, p.Y) is null;
    }

    internal void RedevelopSettlement()
    {
        if (Settlement is not { } settlement) return;
        bool crowded = SettlementCrowded;
        foreach (int i in Map.ZoneCells(ZoneType.Residential).Where(Map.IsBuildingAnchor).Order().ToArray())
        {
            var p = Map.PosOf(i);
            var type = Map.BuildingAt(p.X, p.Y);
            if (type is null || !CityAnalysis.Operating(this, i)) continue;
            var household = Map.HouseholdLayer[i];
            var oldArea = Map.BuildingFootprintAt(p.X, p.Y);
            bool wealthy = SettlementLandValue(p) >= 47;
            string name = wealthy ? "Courtyard House" : "Shanty Tower";
            if (!wealthy && (!crowded || household.Total < type.Capacity * 0.7)) continue;
            if (type.Name == name || type.Name == "Wall House") continue;
            var next = Map.Content.Buildings.Find(name);
            if (next is null || next.Capacity < household.Total) continue;
            var area = new CellRect(p.X, p.Y, Math.Max(oldArea.Width, next.Width), Math.Max(oldArea.Height, next.Height));
            if (!SettlementSiteAvailable(settlement.Bounds, area, owner: i) ||
                !SettlementAccessSurvives(area, i)) continue;
            Map.SetBuildingFootprint(next, area);
            Map.SetHousehold(p.X, p.Y, household);
        }
    }
}
