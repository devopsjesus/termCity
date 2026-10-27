using System.Text.Json.Serialization;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

internal sealed record SavedWall(
    [property: JsonRequired] Pos Cell,
    [property: JsonRequired] double Health,
    [property: JsonRequired] double MaxHealth,
    [property: JsonRequired] bool IsTower,
    [property: JsonRequired] bool IsGate,
    [property: JsonRequired] WallDefenseKind Defense,
    [property: JsonRequired] double ArrowCooldown,
    [property: JsonRequired] double DefenseCooldown);
internal sealed record SavedGoblin(
    [property: JsonRequired] int Id,
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Health,
    [property: JsonRequired] double MaxHealth,
    [property: JsonRequired] int Tier,
    [property: JsonRequired] List<Pos> Route,
    [property: JsonRequired] int RouteIndex,
    [property: JsonRequired] double AttackCooldown,
    double BlockedSeconds = 0);
internal sealed class SavedSettlement
{
    [JsonRequired] public CellRect Bounds { get; set; }
    [JsonRequired] public WallMaterial Material { get; set; }
    [JsonRequired] public BaileyGateSide GateSide { get; set; }
    [JsonRequired] public List<SavedWall> Walls { get; set; } = [];
    [JsonRequired] public List<SavedGoblin> Goblins { get; set; } = [];
    [JsonRequired] public int WaveNumber { get; set; }
    [JsonRequired] public double NextWaveSeconds { get; set; }
    [JsonRequired] public int Revision { get; set; }
    [JsonRequired] public int NextGoblinId { get; set; }
    [JsonRequired] public double Accumulator { get; set; }
    public Pos? RepairTarget { get; set; }
    public double RepairPhase { get; set; }
    public bool GroupedWallSections { get; set; }
    public WallMaterial? UpgradeTargetMaterial { get; set; }
    public int UpgradedSections { get; set; }
    public double UpgradeSectionSeconds { get; set; }

    public static SavedSettlement? Capture(SettlementState? state) => state is null ? null : new()
    {
        Bounds = state.Bounds, Material = state.Material, GateSide = state.GateSide, WaveNumber = state.WaveNumber,
        NextWaveSeconds = state.NextWaveSeconds, Revision = state.Revision, NextGoblinId = state.NextGoblinId,
        Accumulator = state.Accumulator,
        GroupedWallSections = true,
        UpgradeTargetMaterial = state.UpgradeTargetMaterial,
        UpgradedSections = state.UpgradedSections,
        UpgradeSectionSeconds = state.UpgradeSectionSeconds,
        RepairTarget = state.RepairTarget is { } target && state.WallAt(target) is { } wall &&
            wall.Health < wall.MaxHealth ? target : null,
        RepairPhase = state.RepairTarget is { } phaseTarget && state.WallAt(phaseTarget) is { } phaseWall &&
            phaseWall.Health < phaseWall.MaxHealth ? state.RepairPhase : 0,
        Walls = state.Walls.Select(w => new SavedWall(w.Cell, w.Health, w.MaxHealth, w.IsTower, w.IsGate,
            w.Defense, w.ArrowCooldown, w.DefenseCooldown)).ToList(),
        Goblins = state.Goblins.Select(g => new SavedGoblin(g.Id, g.X, g.Y, g.Health, g.MaxHealth,
            g.Tier, new(g.Route), g.RouteIndex, g.AttackCooldown, g.BlockedSeconds)).ToList(),
    };

    public SettlementState Restore(CityGame game)
    {
        bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        void Require(bool valid)
        {
            if (!valid) throw new InvalidDataException("Invalid settlement or siege state.");
        }
        Require(Bounds.Width is >= 12 and <= 160 && Bounds.Height is >= 12 and <= 160 &&
            Bounds.X >= 0 && Bounds.Y >= 0 && (long)Bounds.X + Bounds.Width <= game.Map.Width &&
            (long)Bounds.Y + Bounds.Height <= game.Map.Height && Enum.IsDefined(Material) && Enum.IsDefined(GateSide) &&
            WaveNumber is >= 0 and <= 1_000_000 && Revision >= 0 && NextGoblinId > 0 &&
            Range(NextWaveSeconds, 0, 120) && Range(Accumulator, 0, SettlementState.StepSeconds) &&
            Walls is not null && Goblins is not null && Goblins.Count <= SettlementState.MaximumGoblinSwarm);
        Require(Material == WallMaterial.Wood || game.HighestPopulation >= (Material == WallMaterial.Stone ? 250 : 1_000));
        Require(WaveNumber == 0 ? game.HighestPopulation < 100 && Goblins!.Count == 0 : game.HighestPopulation >= 100);
        Require(WaveNumber != 0 || NextWaveSeconds == 120 && Accumulator == 0 && NextGoblinId == 1);
        Require(UpgradeTargetMaterial is null
            ? UpgradedSections == 0 && UpgradeSectionSeconds == 0
            : GroupedWallSections && Enum.IsDefined(UpgradeTargetMaterial.Value) &&
                Material != WallMaterial.ReinforcedStone && UpgradeTargetMaterial.Value == Material + 1 &&
                game.HighestPopulation >= (UpgradeTargetMaterial == WallMaterial.Stone ? 250 : 1_000) &&
                UpgradedSections >= 0 && Range(UpgradeSectionSeconds, 0, SettlementState.UpgradeSecondsPerSection) &&
                UpgradeSectionSeconds < SettlementState.UpgradeSecondsPerSection);
        var state = new SettlementState(Bounds, GateSide, groupWallSections: false) { Material = Material, WaveNumber = WaveNumber,
            NextWaveSeconds = NextWaveSeconds, Revision = Revision, NextGoblinId = NextGoblinId,
            Accumulator = Accumulator, UpgradeTargetMaterial = UpgradeTargetMaterial,
            UpgradedSections = UpgradedSections, UpgradeSectionSeconds = UpgradeSectionSeconds };
        Require(Bounds.Cells().All(p => game.Map.TerrainAt(p.X, p.Y).Buildable));
        Require(Walls!.Count == state.Walls.Count);
        var groupedTemplate = new SettlementState(Bounds, GateSide);
        double maxHealth = Material switch { WallMaterial.Wood => 200, WallMaterial.Stone => 450, _ => 800 };
        for (int i = 0; i < Walls.Count; i++)
        {
            var saved = Walls[i];
            var wall = state.WallList[i];
            int sectionLength = GroupedWallSections ? groupedTemplate.WallList[i].SectionLength : 1;
            Require(saved is not null && saved.Cell == wall.Cell && saved.IsGate == wall.IsGate &&
                saved.IsTower == wall.IsTower &&
                (saved.MaxHealth == maxHealth * sectionLength || UpgradeTargetMaterial is { } target &&
                    saved.MaxHealth == SettlementState.MaterialHealth(target) * sectionLength) &&
                Range(saved.Health, 0, saved.MaxHealth) && Enum.IsDefined(saved.Defense) &&
                Range(saved.ArrowCooldown, 0, WallDefenses.Get(WallDefenseKind.None).ReloadSeconds) &&
                Range(saved.DefenseCooldown, 0, 8));
            Require(game.HighestPopulation >= WallDefenses.Get(saved!.Defense).RequiredPopulation);
            var p = wall.Cell;
            Require(game.Map.TerrainAt(p.X, p.Y).Buildable && game.Map.BuildingAt(p.X, p.Y) is null &&
                game.Map.ZoneAt(p.X, p.Y) == ZoneType.None && !game.Map.HasRoad(p.X, p.Y) &&
                !game.Network.RoadOverlapsCell(game.Map.Index(p.X, p.Y)));
            wall.Health = saved.Health;
            wall.MaxHealth = saved.MaxHealth;
            wall.Defense = saved.Defense;
            wall.ArrowCooldown = saved.ArrowCooldown;
            wall.DefenseCooldown = saved.DefenseCooldown;
        }
        state.GroupWallSections(legacy: !GroupedWallSections, savedGrouped: GroupedWallSections);
        Require(UpgradeTargetMaterial is null || UpgradedSections < state.UpgradeSections.Count());
        for (int i = 0; i < Walls.Count; i++)
        {
            var wall = state.WallList[i];
            Require(wall.MaxHealth == SettlementState.MaterialHealth(state.MaterialFor(wall)) * wall.SectionLength);
            if (GroupedWallSections)
                Require(Walls[i].Health == wall.Health && Walls[i].MaxHealth == wall.MaxHealth);
        }
        var ids = new HashSet<int>();
        int maximumTier = 1 + (game.HighestPopulation >= 250 ? 1 : 0) + (game.HighestPopulation >= 500 ? 1 : 0) +
            (game.HighestPopulation >= 1_000 ? 1 : 0) + (game.HighestPopulation >= 2_500 ? 1 : 0);
        foreach (var goblin in Goblins!)
        {
            Require(goblin is not null && goblin.Id > 0 && goblin.Id < NextGoblinId && ids.Add(goblin.Id) &&
                Range(goblin.X, 0.5, game.Map.Width - 0.5) && Range(goblin.Y, 0.5, game.Map.Height - 0.5) &&
                goblin.Tier >= 1 && goblin.Tier <= maximumTier &&
                (goblin.MaxHealth == SettlementState.GoblinMaxHealth(goblin.Tier, WaveNumber) ||
                    goblin.MaxHealth == 32 + 18 * (goblin.Tier - 1) + 3 * Math.Min(30, WaveNumber - 1)) &&
                Range(goblin.Health, double.Epsilon, goblin.MaxHealth) && Range(goblin.AttackCooldown, 0, 1) &&
                Range(goblin.BlockedSeconds, 0, 0.600000001) &&
                goblin.Route is { Count: > 0 } && goblin.Route.Count <= SettlementState.MaximumRouteLength && goblin.RouteIndex >= 0 &&
                goblin.RouteIndex < goblin.Route.Count);
            Require(state.WallAt(goblin!.Route[^1]) is not null);
            for (int i = 0; i < goblin.Route.Count; i++)
            {
                var p = goblin.Route[i];
                Require(game.Map.InBounds(p) && game.Map.TerrainAt(p.X, p.Y).Buildable &&
                    (i == goblin.Route.Count - 1 || !Bounds.Contains(p)));
                if (i > 0) Require(Math.Abs(p.X - goblin.Route[i - 1].X) + Math.Abs(p.Y - goblin.Route[i - 1].Y) == 1);
            }
            var cell = new Pos((int)Math.Floor(goblin.X), (int)Math.Floor(goblin.Y));
            var next = goblin.Route[goblin.RouteIndex];
            Require(game.Map.TerrainAt(cell.X, cell.Y).Buildable && !state.ContainsInterior(cell) &&
                Math.Abs(goblin.X - next.X - 0.5) + Math.Abs(goblin.Y - next.Y - 0.5) <= 1.000001 &&
                (Math.Abs(goblin.X - next.X - 0.5) < 1e-6 || Math.Abs(goblin.Y - next.Y - 0.5) < 1e-6));
            double goblinMaxHealth = SettlementState.GoblinMaxHealth(goblin.Tier, WaveNumber);
            // Preserve damage percentage in old saves and exact health in current-balance snapshots.
            double health = goblin.MaxHealth == goblinMaxHealth ? goblin.Health :
                goblin.Health * goblinMaxHealth / goblin.MaxHealth;
            state.GoblinList.Add(new Goblin { Id = goblin.Id, X = goblin.X, Y = goblin.Y, Health = health,
                MaxHealth = goblinMaxHealth, Tier = goblin.Tier, Route = new(goblin.Route), RouteIndex = goblin.RouteIndex,
                AttackCooldown = goblin.AttackCooldown, BlockedSeconds = goblin.BlockedSeconds });
        }
        Require(Range(RepairPhase, 0, 1) && RepairPhase < 1 &&
            (RepairTarget is { } repairTarget
                ? WaveNumber > 0 && state.WallAt(repairTarget) is { } repairWall && repairWall.Health < repairWall.MaxHealth
                : RepairPhase == 0));
        state.RepairTarget = RepairTarget;
        state.RepairPhase = RepairPhase;
        state.SeparateLoadedCrowd(game);
        return state;
    }
}
