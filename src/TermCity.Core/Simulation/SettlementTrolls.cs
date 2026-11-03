namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public bool TrollsIntroduced { get; internal set; }
    internal int PlannedTrollWave;
    public bool TrollApproachExpected => !TrollsIntroduced && PlannedTrollWave == WaveNumber + 1;

    private void PlanFirstTrollApproach(CityGame game)
    {
        if (TrollsIntroduced || PlannedTrollWave != 0 || game.HighestPopulation < 1_000) return;
        var (spawns, _) = BuildCrowdRoutes(game, SiegeEnemies.TreeTroll.CollisionRadius(4));
        if (spawns.Count == 0) return;
        PlannedTrollWave = WaveNumber + (NextWaveSeconds >= 20 ? 1 : 2);
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            "Scouts report tree, stone and mud trolls gathering for an assault. " +
            "These eight-to-eleven-foot attackers must batter a breach; reinforce the walls and prepare the garrison.",
            [], Bad: true));
        Revision++;
    }
}
