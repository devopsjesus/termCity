using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    internal Pos? RepairTarget;
    internal int? RepairTargetId;
    internal double RepairPhase;

    public Pos? RepairingWall => DamagedRepairTarget?.Cell;
    public WallComponent? RepairingSection => DamagedRepairTarget;
    public WallComponent? VisibleRepairingSection => EnemyList.Count == 0 &&
        DamagedRepairTarget is { RepairAnimationActive: true } wall ? wall : null;
    public Pos? VisibleRepairingWall => VisibleRepairingSection?.Cell;
    public double RepairAnimationPhase => RepairingWall is not null ? RepairPhase : 0;

    private WallComponent? DamagedRepairTarget
    {
        get
        {
            var wall = RepairTargetId is { } id ? WallList.FirstOrDefault(w => w.Id == id) :
                RepairTarget is { } cell ? WallAt(cell) : null;
            return wall is not null && wall.Health < wall.MaxHealth ? wall : null;
        }
    }

    /// <summary>Civilian wall-health restored each simulation second, without a treasury cost.</summary>
    public double RepairRatePerSecond(CityGame game) => game.Stats.Population <= 0 ? 0 :
        12 + 0.24 * game.Stats.Population + 9 * game.Stats.Industrial.Occupied +
        3 * game.Services.ActiveCounts.Sum();

    internal void AdvanceRepairs(CityGame game, double seconds)
    {
        if (game.Paused) return;
        if (WaveNumber == 0 || game.Stats.Population <= 0)
        {
            ClearRepairCrew();
            return;
        }
        if (!double.IsFinite(seconds) || seconds <= 0) return;

        double work = RepairRatePerSecond(game) * seconds;
        var target = DamagedRepairTarget;
        if (EnemyList.Count > 0)
        {
            var urgent = WallSections.Where(w => w.Health < w.MaxHealth)
                .OrderBy(w => w.Health == 0 ? 0 : 1)
                .ThenBy(w => w.Health / w.MaxHealth).ThenBy(w => w.Cell.Y).ThenBy(w => w.Cell.X).FirstOrDefault();
            if (urgent is not null && (target is null ||
                urgent.Health / urgent.MaxHealth + 0.10 < target.Health / target.MaxHealth))
            {
                target = urgent;
                RepairTarget = urgent.Cell;
                RepairTargetId = urgent.Id;
                RepairPhase = 0;
            }
        }
        if (target is null) ClearRepairCrew();
        while (work > 0)
        {
            if (target is null)
            {
                target = WallSections.Where(w => w.Health < w.MaxHealth)
                    .OrderBy(w => w.Health == 0 ? 0 : 1)
                    .ThenBy(w => w.Health / w.MaxHealth).FirstOrDefault();
                if (target is null)
                {
                    ClearRepairCrew();
                    return;
                }
                RepairTarget = target.Cell;
                RepairTargetId = target.Id;
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
        RepairTargetId = null;
        RepairPhase = 0;
    }
}
