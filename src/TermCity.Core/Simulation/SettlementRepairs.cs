using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    internal Pos? RepairTarget;
    internal double RepairPhase;

    public Pos? RepairingWall => GoblinList.Count == 0 ? DamagedRepairTarget?.Cell : null;
    public double RepairAnimationPhase => RepairingWall is not null ? RepairPhase : 0;

    private WallComponent? DamagedRepairTarget =>
        RepairTarget is { } cell && WallAt(cell) is { } wall && wall.Health < wall.MaxHealth ? wall : null;

    /// <summary>Civilian wall-health restored each simulation second, without a treasury cost.</summary>
    public double RepairRatePerSecond(CityGame game) => game.Stats.Population <= 0 ? 0 :
        1 + 0.02 * game.Stats.Population + 0.75 * game.Stats.Industrial.Occupied +
        0.25 * game.Services.ActiveCounts.Sum();

    internal void AdvanceRepairs(CityGame game, double seconds)
    {
        if (game.Paused || GoblinList.Count != 0) return;
        if (WaveNumber == 0 || game.Stats.Population <= 0)
        {
            ClearRepairCrew();
            return;
        }
        if (!double.IsFinite(seconds) || seconds <= 0) return;

        double work = RepairRatePerSecond(game) * seconds;
        var target = DamagedRepairTarget;
        if (target is null) ClearRepairCrew();
        while (work > 0)
        {
            if (target is null)
            {
                target = WallList.Where(w => w.Health < w.MaxHealth)
                    .OrderBy(w => w.Health == 0 ? 0 : 1)
                    .ThenBy(w => w.Health / w.MaxHealth).FirstOrDefault();
                if (target is null)
                {
                    ClearRepairCrew();
                    return;
                }
                RepairTarget = target.Cell;
                RepairPhase = 0;
            }

            double restored = Math.Min(work, target.MaxHealth - target.Health);
            target.Health = Math.Min(target.MaxHealth, target.Health + restored);
            work = Math.Max(0, work - restored);
            if (target.Health >= target.MaxHealth)
            {
                ClearRepairCrew();
                target = null;
            }
        }
        if (target is not null) RepairPhase = (RepairPhase + seconds) % 1;
    }

    internal void ClearRepairCrew()
    {
        RepairTarget = null;
        RepairPhase = 0;
    }
}
