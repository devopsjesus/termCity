using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public bool OrcsIntroduced { get; internal set; }
    internal int PlannedOrcWave;
    internal BaileyGateSide? PlannedOrcSide;
    public BaileyGateSide? FirstOrcApproach => !OrcsIntroduced && PlannedOrcWave == WaveNumber + 1
        ? PlannedOrcSide : null;

    private BaileyGateSide SideOf(Pos p) => p.X == Bounds.Right ? BaileyGateSide.Right :
        p.X == Bounds.X ? BaileyGateSide.Left : p.Y == Bounds.Bottom ? BaileyGateSide.Bottom : BaileyGateSide.Top;

    private BaileyGateSide ChooseOrcSide(List<Pos> spawns, Dictionary<Pos, Pos> previous) =>
        spawns.GroupBy(p => SideOf(CrowdRoute(p, previous)[^1]))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
            .Select(group => group.Key).FirstOrDefault();

    private void PlanFirstOrcApproach(CityGame game)
    {
        if (OrcsIntroduced || PlannedOrcWave > 0 || game.HighestPopulation < 250) return;
        var (spawns, previous) = BuildCrowdRoutes(game, SiegeEnemies.Orc.CollisionRadius(2));
        if (spawns.Count == 0) return;
        PlannedOrcSide = ChooseOrcSide(spawns, previous);
        PlannedOrcWave = WaveNumber + (NextWaveSeconds >= 20 ? 1 : 2);
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"Scouts report the first Orc assault will approach the {PlannedOrcSide.ToString()!.ToLowerInvariant()} wall. " +
            "Prepare archers and soldiers from Garrison.", [], Bad: true));
        Revision++;
    }
}
