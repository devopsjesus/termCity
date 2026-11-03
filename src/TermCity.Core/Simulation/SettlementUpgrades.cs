using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed partial class SettlementState
{
    public const double UpgradeSecondsPerSection = 3;
    public WallMaterial? UpgradeTargetMaterial { get; internal set; }
    internal int UpgradedSections;
    internal double UpgradeSectionSeconds;
    private Dictionary<int, int>? _upgradeSectionIndices;
    internal List<int>? UpgradeOrder;

    internal IEnumerable<WallComponent> UpgradeSections => UpgradeOrder is null ? WallSections :
        UpgradeOrder.Select(id => WallList.Single(w => w.Id == id));

    public WallComponent? UpgradingSection => UpgradeTargetMaterial is null ? null :
        UpgradeSections.Skip(UpgradedSections).FirstOrDefault();
    public Pos? UpgradingWall => UpgradingSection?.Cell;
    public double UpgradeAnimationPhase => UpgradeSectionSeconds % 1;
    public double OverallUpgradeProgress => UpgradeTargetMaterial is null ? 0 :
        (UpgradedSections + UpgradeSectionSeconds / UpgradeSecondsPerSection) / UpgradeSections.Count();

    public WallMaterial MaterialFor(WallComponent wall)
    {
        if ((wall.SharedSection ?? wall).BuiltMaterial is { } built) return built;
        if (UpgradeTargetMaterial is not { } target) return Material;
        int index = SectionIndex(wall);
        return index >= 0 && index < UpgradedSections ? target : Material;
    }

    public double ProgressFor(WallComponent wall)
    {
        if (UpgradeTargetMaterial is null) return 0;
        int index = SectionIndex(wall);
        return index < 0 || index > UpgradedSections ? 0 :
            index < UpgradedSections ? 1 : UpgradeSectionSeconds / UpgradeSecondsPerSection;
    }

    public WallMaterial? WallMaterialAt(Pos cell) => WallAt(cell) is { } wall ? MaterialFor(wall) : null;
    public double WallUpgradeProgressAt(Pos cell) => WallAt(cell) is { } wall ? ProgressFor(wall) : 0;

    private int SectionIndex(WallComponent wall)
    {
        _upgradeSectionIndices ??= UpgradeSections.Select((member, index) => (member.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        return _upgradeSectionIndices.GetValueOrDefault((wall.SharedSection ?? wall).Id, -1);
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
        UpgradeOrder = WallSections.Select(w => w.Id).ToList();
        _upgradeSectionIndices = null;
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
            wall.MaxHealth = MaterialHealth(target) * wall.HealthUnits;
            wall.Health = fraction * wall.MaxHealth;
            wall.BuiltMaterial = target;
            InvalidateWallTopology();
            if (UpgradedSections == sections.Length)
            {
                Material = target;
                UpgradeTargetMaterial = null;
                UpgradedSections = 0;
                UpgradeSectionSeconds = 0;
                UpgradeOrder = null;
                _upgradeSectionIndices = null;
                break;
            }
        }
        Revision++;
    }
}
