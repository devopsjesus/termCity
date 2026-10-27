using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public const double UpgradeSecondsPerSection = 3;
    public WallMaterial? UpgradeTargetMaterial { get; internal set; }
    internal int UpgradedSections;
    internal double UpgradeSectionSeconds;
    private Dictionary<Pos, int>? _upgradeSectionIndices;

    internal IEnumerable<WallComponent> UpgradeSections => WallSections;

    public Pos? UpgradingWall => UpgradeTargetMaterial is null ? null :
        UpgradeSections.Skip(UpgradedSections).FirstOrDefault()?.Cell;
    public double UpgradeAnimationPhase => UpgradeSectionSeconds % 1;
    public double OverallUpgradeProgress => UpgradeTargetMaterial is null ? 0 :
        (UpgradedSections + UpgradeSectionSeconds / UpgradeSecondsPerSection) / UpgradeSections.Count();

    public WallMaterial MaterialFor(WallComponent wall)
    {
        if (UpgradeTargetMaterial is not { } target) return Material;
        int index = SectionIndex(wall.SectionAnchor);
        return index >= 0 && index < UpgradedSections ? target : Material;
    }

    public double ProgressFor(WallComponent wall)
    {
        if (UpgradeTargetMaterial is null) return 0;
        int index = SectionIndex(wall.SectionAnchor);
        return index < 0 || index > UpgradedSections ? 0 :
            index < UpgradedSections ? 1 : UpgradeSectionSeconds / UpgradeSecondsPerSection;
    }

    public WallMaterial? WallMaterialAt(Pos cell) => WallAt(cell) is { } wall ? MaterialFor(wall) : null;
    public double WallUpgradeProgressAt(Pos cell) => WallAt(cell) is { } wall ? ProgressFor(wall) : 0;

    private int SectionIndex(Pos anchor)
    {
        _upgradeSectionIndices ??= UpgradeSections.Select((wall, index) => (wall.Cell, index))
            .ToDictionary(item => item.Cell, item => item.index);
        return _upgradeSectionIndices.GetValueOrDefault(anchor, -1);
    }

    internal static double MaterialHealth(WallMaterial material) => material switch
    {
        WallMaterial.Wood => 200,
        WallMaterial.Stone => 450,
        _ => 800,
    };

    internal void BeginUpgrade()
    {
        UpgradeTargetMaterial = Material + 1;
        UpgradedSections = 0;
        UpgradeSectionSeconds = 0;
        Revision++;
    }

    internal void AdvanceUpgrades(CityGame game, double seconds)
    {
        if (game.Paused || UpgradeTargetMaterial is not { } target ||
            !double.IsFinite(seconds) || seconds <= 0) return;
        var sections = UpgradeSections.ToArray();
        UpgradeSectionSeconds += seconds;
        while (UpgradeSectionSeconds + 1e-9 >= UpgradeSecondsPerSection)
        {
            UpgradeSectionSeconds = Math.Max(0, UpgradeSectionSeconds - UpgradeSecondsPerSection);
            var wall = sections[UpgradedSections++];
            // Health proxies share one pool: only the section anchor receives this conversion.
            double fraction = wall.Health / wall.MaxHealth;
            wall.MaxHealth = MaterialHealth(target) * wall.SectionLength;
            wall.Health = fraction * wall.MaxHealth;
            if (UpgradedSections == sections.Length)
            {
                Material = target;
                UpgradeTargetMaterial = null;
                UpgradedSections = 0;
                UpgradeSectionSeconds = 0;
                break;
            }
        }
        Revision++;
    }
}
