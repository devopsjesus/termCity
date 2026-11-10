using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public bool OrcsIntroduced { get; internal set; }
    internal int PlannedOrcWave;
    internal BaileyGateSide? PlannedOrcSide;
    internal BaileyGateSide? FirstAssaultSide;
    internal int FirstAssaultLastOrcId;
    internal BaileyGateSide? ActiveFirstAssaultSide => FirstAssaultSide is { } side &&
        EnemyList.Any(e => e.Kind == SiegeEnemyKind.Orc && e.Health > 0 && e.Id <= FirstAssaultLastOrcId) ? side : null;
    internal BaileyGateSide? DefenderStagingSide => FirstOrcApproach ?? ActiveFirstAssaultSide ??
        EnemyList.Where(e => e.Kind == SiegeEnemyKind.Orc && e.Health > 0)
            .GroupBy(e => SideOf(e.Route.Count > 0 ? e.Route[^1] : new((int)e.X, (int)e.Y)))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
            .Select(group => (BaileyGateSide?)group.Key).FirstOrDefault();
    private int _orcApproachMapVersion = -1, _orcApproachTopologyRevision = -1, _orcApproachWave = -1;
    public BaileyGateSide? FirstOrcApproach => !OrcsIntroduced && PlannedOrcWave == WaveNumber + 1
        ? PlannedOrcSide : null;

    private BaileyGateSide SideOf(Pos p) => p.X == Bounds.Right ? BaileyGateSide.Right :
        p.X == Bounds.X ? BaileyGateSide.Left : p.Y == Bounds.Bottom ? BaileyGateSide.Bottom : BaileyGateSide.Top;

    private BaileyGateSide ChooseOrcSide(List<Pos> spawns, Dictionary<Pos, Pos> previous) =>
        spawns.GroupBy(p => SideOf(CrowdRoute(p, previous)[^1]))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
            .Select(group => group.Key).FirstOrDefault();

    private List<Pos> OrcApproachSpawns(CityGame game, List<Pos> candidates, Dictionary<Pos, Pos> previous, int tier)
    {
        var reachable = new Dictionary<Pos, bool>();
        var probe = new SiegeEnemy { Kind = SiegeEnemyKind.Orc, Tier = tier };
        bool Reachable(Pos cell)
        {
            if (reachable.TryGetValue(cell, out bool cached)) return cached;
            var route = CrowdRoute(cell, previous);
            if (route.Count == 0)
                return reachable[cell] = WallList.Any(wall => wall.Cell == cell && wall.Health <= 0 && OuterBarrier(wall)) &&
                    EnemyGroundClear(game, cell.X + .5, cell.Y + .5, probe.CollisionRadius);
            var side = SideOf(route[^1]);
            double x = cell.X + .5, y = cell.Y + .5;
            bool Check(double px, double py, out bool contacted)
            {
                contacted = false;
                if (!EnemyGroundClear(game, px, py, probe.CollisionRadius)) return false;
                probe.X = px;
                probe.Y = py;
                if (AttackableWall(probe, checkCrowd: false) is not { } wall) return true;
                contacted = true;
                if (SideOf(wall.Cell) != side) return false;
                if (!wall.IsOriginal) return true;
                var half = Rendering.WallDamageLayout.HalfSize(wall);
                return side switch
                {
                    BaileyGateSide.Top => py <= wall.Center.Y - half.Y,
                    BaileyGateSide.Bottom => py >= wall.Center.Y + half.Y,
                    BaileyGateSide.Left => px <= wall.Center.X - half.X,
                    _ => px >= wall.Center.X + half.X,
                };
            }
            bool clear = Check(x, y, out bool contact);
            if (!clear || contact) return reachable[cell] = clear;
            var next = route[0];
            double dx = next.X + .5 - x, dy = next.Y + .5 - y;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / .125));
            for (int step = 1; step <= steps; step++)
            {
                clear = Check(x + dx * step / steps, y + dy * step / steps, out contact);
                if (!clear || contact) return reachable[cell] = clear;
            }
            return reachable[cell] = Reachable(next);
        }
        return candidates.Where(cell => CrowdRoute(cell, previous).Count >= CrowdSpawnDistance &&
            EnemySpawnClear(game, cell.X + .5, cell.Y + .5,
            probe.CollisionRadius) && Reachable(cell)).ToList();
    }

    private void PlanFirstOrcApproach(CityGame game)
    {
        if (OrcsIntroduced || game.HighestPopulation < 250) return;
        if (PlannedOrcWave > WaveNumber + 1)
        {
            PlannedOrcWave = WaveNumber + 1;
            Revision++;
        }
        if (_orcApproachMapVersion == game.MapVersion && _orcApproachTopologyRevision == TopologyRevision &&
            _orcApproachWave == WaveNumber) return;
        _orcApproachMapVersion = game.MapVersion;
        _orcApproachTopologyRevision = TopologyRevision;
        _orcApproachWave = WaveNumber;
        var (spawns, previous) = BuildCrowdRoutes(game, SiegeEnemies.Orc.CollisionRadius(2));
        spawns = OrcApproachSpawns(game, spawns, previous, 2);
        if (spawns.Count == 0)
        {
            if (PlannedOrcWave > 0)
            {
                PlannedOrcSide = null;
                PlannedOrcWave = 0;
                game.Report(new CityEvent(game.Week, EventKind.Raid,
                    "Scouts withdraw the first Orc approach warning: no reachable exterior ground fits their size.", [], Bad: false));
                Revision++;
            }
            return;
        }
        if (PlannedOrcSide is { } planned && spawns.Any(p => SideOf(CrowdRoute(p, previous)[^1]) == planned)) return;
        bool revised = PlannedOrcSide is not null;
        PlannedOrcSide = ChooseOrcSide(spawns, previous);
        if (PlannedOrcWave == 0) PlannedOrcWave = WaveNumber + 1;
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"{(revised ? "Scouts revise their report:" : "Scouts report")} the first Orc assault will approach " +
            $"the {PlannedOrcSide.ToString()!.ToLowerInvariant()} wall. " +
            "Prepare archers and soldiers from Garrison.", [], Bad: true));
        Revision++;
    }
}
