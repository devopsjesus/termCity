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
    [property: JsonRequired] double DefenseCooldown,
    bool? RepairAnimationActive = null, int Id = 0, float? StartX = null, float? StartY = null,
    float? EndX = null, float? EndY = null, WallMaterial? BuiltMaterial = null,
    int? SharedSectionId = null, CellRect SectionBounds = default, bool IsGateWing = false,
    bool BelongsToFoundingWall = false, int DefenseFacing = 1, bool JunctionTower = false);
internal sealed record SavedSiegeEnemy(
    [property: JsonRequired] int Id,
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Health,
    [property: JsonRequired] double MaxHealth,
    [property: JsonRequired] int Tier,
    [property: JsonRequired] List<Pos> Route,
    [property: JsonRequired] int RouteIndex,
    [property: JsonRequired] double AttackCooldown,
    double BlockedSeconds = 0,
    SiegeEnemyKind Kind = SiegeEnemyKind.Goblin,
    int AppearanceSeed = 0, bool OrcAttacking = false, double OrcAttackSeconds = 0);
internal sealed class SavedSettlement
{
    public int WallNetworkVersion { get; set; }
    public int NextWallId { get; set; }
    public int NextAttackId { get; set; } = 1;
    public List<SavedAttack> Attacks { get; set; } = [];
    public List<Pos> EstablishedLand { get; set; } = [];
    public List<int>? UpgradeOrder { get; set; }
    [JsonRequired] public CellRect Bounds { get; set; }
    [JsonRequired] public WallMaterial Material { get; set; }
    [JsonRequired] public BaileyGateSide GateSide { get; set; }
    [JsonRequired] public List<SavedWall> Walls { get; set; } = [];
    [JsonRequired, JsonPropertyName("Goblins")] public List<SavedSiegeEnemy> Enemies { get; set; } = [];
    [JsonRequired] public int WaveNumber { get; set; }
    [JsonRequired] public double NextWaveSeconds { get; set; }
    [JsonRequired] public int Revision { get; set; }
    [JsonRequired, JsonPropertyName("NextGoblinId")] public int NextEnemyId { get; set; }
    [JsonRequired] public double Accumulator { get; set; }
    public Pos? RepairTarget { get; set; }
    public int? RepairTargetId { get; set; }
    public double RepairPhase { get; set; }
    public bool GroupedWallSections { get; set; }
    public WallMaterial? UpgradeTargetMaterial { get; set; }
    public int UpgradedSections { get; set; }
    public double UpgradeSectionSeconds { get; set; }
    public bool OrcsIntroduced { get; set; }
    public int PlannedOrcWave { get; set; }
    public BaileyGateSide? PlannedOrcSide { get; set; }
    public BaileyGateSide? FirstAssaultSide { get; set; }
    public int FirstAssaultLastOrcId { get; set; }
    public bool TrollsIntroduced { get; set; }
    public int PlannedTrollWave { get; set; }
    public SavedGarrison? Garrison { get; set; }

    public static SavedSettlement? Capture(SettlementState? state) => state is null ? null : new()
    {
        WallNetworkVersion = 1,
        NextWallId = state.NextWallId,
        NextAttackId = state.NextAttackId,
        Attacks = state.Projectiles.Where(shot => shot.Id > 0).Select(SavedAttack.Capture).ToList(),
        EstablishedLand = state.EstablishedLand.OrderBy(p => p.Y).ThenBy(p => p.X).ToList(),
        UpgradeOrder = state.UpgradeOrder is null ? null : new(state.UpgradeOrder),
        Bounds = state.Bounds, Material = state.Material, GateSide = state.GateSide, WaveNumber = state.WaveNumber,
        NextWaveSeconds = state.NextWaveSeconds, Revision = state.Revision, NextEnemyId = state.NextEnemyId,
        Accumulator = state.Accumulator,
        OrcsIntroduced = state.OrcsIntroduced,
        PlannedOrcWave = state.PlannedOrcWave,
        PlannedOrcSide = state.PlannedOrcSide,
        FirstAssaultSide = state.ActiveFirstAssaultSide,
        FirstAssaultLastOrcId = state.ActiveFirstAssaultSide is null ? 0 : state.FirstAssaultLastOrcId,
        TrollsIntroduced = state.TrollsIntroduced,
        PlannedTrollWave = state.PlannedTrollWave,
        Garrison = SavedGarrison.Capture(state),
        GroupedWallSections = true,
        UpgradeTargetMaterial = state.UpgradeTargetMaterial,
        UpgradedSections = state.UpgradedSections,
        UpgradeSectionSeconds = state.UpgradeSectionSeconds,
        RepairTarget = state.RepairingSection?.Cell,
        RepairTargetId = state.RepairingSection?.Id,
        RepairPhase = state.RepairingSection is not null ? state.RepairPhase : 0,
        Walls = state.Walls.Select(w => new SavedWall(w.Cell, w.Health, w.MaxHealth, w.IsTower, w.IsGate,
            w.Defense, w.ArrowCooldown, w.DefenseCooldown, w.RepairAnimationActive, w.Id,
            w.Start?.X, w.Start?.Y, w.End?.X, w.End?.Y, w.BuiltMaterial, w.SharedSection?.Id,
            w.SectionBounds, w.IsGateWing, w.BelongsToFoundingWall, w.DefenseFacing, w.JunctionTower)).ToList(),
        Enemies = state.Enemies.Select(g => new SavedSiegeEnemy(g.Id, g.X, g.Y, g.Health, g.MaxHealth,
            g.Tier, new(g.Route), g.RouteIndex, g.AttackCooldown, g.BlockedSeconds, g.Kind, g.AppearanceSeed,
            g.OrcAttacking, g.OrcAttackSeconds)).ToList(),
    };

    public SettlementState Restore(CityGame game)
    {
        if (WallNetworkVersion != 1)
            throw new InvalidDataException("This settlement uses the retired rectangular-wall save format. Start a new bailey; the original save has not been changed.");
        bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        void Require(bool valid)
        {
            if (!valid) throw new InvalidDataException("Invalid settlement or siege state.");
        }
        Require(Bounds.Width is >= 12 and <= 160 && Bounds.Height is >= 12 and <= 160 &&
            Bounds.X >= 0 && Bounds.Y >= 0 && (long)Bounds.X + Bounds.Width <= game.Map.Width &&
            (long)Bounds.Y + Bounds.Height <= game.Map.Height && Enum.IsDefined(Material) && Enum.IsDefined(GateSide) &&
            WaveNumber is >= 0 and <= 1_000_000 && Revision >= 0 && NextEnemyId > 0 &&
            Range(NextWaveSeconds, 0, 120) && Range(Accumulator, 0, SettlementState.StepSeconds) &&
            Walls is not null && Enemies is not null && Enemies.Count <= SettlementState.MaximumEnemySwarm);
        Require(Material == WallMaterial.Wood || game.HighestPopulation >= (Material == WallMaterial.Stone ? 250 : 1_000));
        Require(PlannedTrollWave >= 0 && PlannedTrollWave <= WaveNumber + 2 &&
            (!TrollsIntroduced || game.HighestPopulation >= 1_000) &&
            (PlannedTrollWave == 0 || game.HighestPopulation >= 1_000 &&
                (TrollsIntroduced || PlannedTrollWave > WaveNumber)));
        Require(PlannedOrcWave >= 0 && PlannedOrcWave <= WaveNumber + 2 &&
            (PlannedOrcSide is null || Enum.IsDefined(PlannedOrcSide.Value)) &&
            (OrcsIntroduced ? game.HighestPopulation >= 250 && PlannedOrcSide is null :
                PlannedOrcWave == 0 ? PlannedOrcSide is null :
                    game.HighestPopulation >= 250 && PlannedOrcWave > WaveNumber && PlannedOrcSide is not null));
        Require(FirstAssaultSide is null ? FirstAssaultLastOrcId == 0 :
            Enum.IsDefined(FirstAssaultSide.Value) && OrcsIntroduced && FirstAssaultLastOrcId > 0 &&
                FirstAssaultLastOrcId < NextEnemyId &&
                Enemies!.Any(e => e is not null && e.Kind == SiegeEnemyKind.Orc &&
                    e.Id <= FirstAssaultLastOrcId && e.Health > 0));
        Require(WaveNumber == 0 ? game.HighestPopulation < 100 && Enemies!.Count == 0 : game.HighestPopulation >= 100);
        Require(WaveNumber != 0 || NextWaveSeconds == 120 && Accumulator == 0 && NextEnemyId == 1);
        Require(UpgradeTargetMaterial is null
            ? UpgradedSections == 0 && UpgradeSectionSeconds == 0
            : GroupedWallSections && Enum.IsDefined(UpgradeTargetMaterial.Value) &&
                Material != WallMaterial.ReinforcedStone && UpgradeTargetMaterial.Value == Material + 1 &&
                game.HighestPopulation >= (UpgradeTargetMaterial == WallMaterial.Stone ? 250 : 1_000) &&
                UpgradedSections >= 0 && Range(UpgradeSectionSeconds, 0, SettlementState.UpgradeSecondsPerSection) &&
                UpgradeSectionSeconds < SettlementState.UpgradeSecondsPerSection);
        var state = new SettlementState(Bounds, GateSide, groupWallSections: false) { Material = Material, WaveNumber = WaveNumber,
            NextWaveSeconds = NextWaveSeconds, Revision = Revision, NextEnemyId = NextEnemyId,
            Accumulator = Accumulator, UpgradeTargetMaterial = UpgradeTargetMaterial,
            UpgradedSections = UpgradedSections, UpgradeSectionSeconds = UpgradeSectionSeconds,
            OrcsIntroduced = OrcsIntroduced, PlannedOrcWave = PlannedOrcWave, PlannedOrcSide = PlannedOrcSide,
            FirstAssaultSide = FirstAssaultSide, FirstAssaultLastOrcId = FirstAssaultLastOrcId,
            TrollsIntroduced = TrollsIntroduced, PlannedTrollWave = PlannedTrollWave };
        var template = state.Walls.ToDictionary(w => w.Cell);
        state.ClearWallNetwork();
        Require(Walls!.Count <= (long)game.Map.Width * game.Map.Height * 4);
        var wallIds = new HashSet<int>();
        foreach (var saved in Walls)
        {
            Require(saved is not null && saved.Id > 0 && saved.Id < int.MaxValue / 4 && wallIds.Add(saved.Id) &&
                game.Map.InBounds(saved.Cell) && Range(saved.MaxHealth, double.Epsilon, 100_000) &&
                Range(saved.Health, 0, saved.MaxHealth) && Enum.IsDefined(saved.Defense) && saved.DefenseFacing is -1 or 1 &&
                (!saved.IsGateWing || saved.Defense == WallDefenseKind.None) &&
                (saved.BuiltMaterial is null || Enum.IsDefined(saved.BuiltMaterial.Value)) &&
                Range(saved.ArrowCooldown, 0, WallDefenses.Get(WallDefenseKind.None).ReloadSeconds) &&
                Range(saved.DefenseCooldown, 0, WallDefenses.MaximumReload));
            Require(game.HighestPopulation >= WallDefenses.Get(saved!.Defense).RequiredPopulation);
            bool line = saved.StartX is not null;
            Require(line == (saved.StartY is not null) && line == (saved.EndX is not null) && line == (saved.EndY is not null));
            if (line)
                Require(Range(saved.StartX!.Value, 0, game.Map.Width) && Range(saved.EndX!.Value, 0, game.Map.Width) &&
                    Range(saved.StartY!.Value, 0, game.Map.Height) && Range(saved.EndY!.Value, 0, game.Map.Height) &&
                    Math.Pow(saved.EndX.Value - saved.StartX.Value, 2) + Math.Pow(saved.EndY.Value - saved.StartY.Value, 2) >= .0025 &&
                    saved.BuiltMaterial is not null && !saved.IsGateWing && (!saved.IsTower || saved.JunctionTower) && saved.SharedSectionId is null);
            else
                Require(template.TryGetValue(saved.Cell, out var original) && original.IsGate == saved.IsGate &&
                    (original.IsTower == saved.IsTower || original.IsTower && !saved.IsTower &&
                        !WorldScale.HasDryTowerFootprint(game.Map, original.Center)) &&
                    original.IsGateWing == saved.IsGateWing && saved.BelongsToFoundingWall);
            var wall = new WallComponent
            {
                Id = saved.Id, Cell = saved.Cell, MaxHealth = saved.MaxHealth, Health = saved.Health,
                IsGate = saved.IsGate, IsGateWing = saved.IsGateWing, IsTower = saved.IsTower,
                Start = line ? new(saved.StartX!.Value, saved.StartY!.Value) : null,
                End = line ? new(saved.EndX!.Value, saved.EndY!.Value) : null,
                BuiltMaterial = saved.BuiltMaterial, SectionBounds = saved.SectionBounds,
                Defense = saved.Defense, ArrowCooldown = saved.ArrowCooldown, DefenseCooldown = saved.DefenseCooldown,
                DefenseFacing = saved.DefenseFacing,
                JunctionTower = saved.JunctionTower,
                BelongsToFoundingWall = saved.BelongsToFoundingWall,
            };
            Require(game.Map.TerrainAt(saved.Cell.X, saved.Cell.Y).Buildable ||
                !saved.IsGate && game.Map.TerrainAt(saved.Cell.X, saved.Cell.Y).Name == Terrain.DefaultTerrains.WaterName);
            state.AddWall(wall);
        }
        var byId = state.Walls.ToDictionary(w => w.Id);
        Require(NextWallId > wallIds.DefaultIfEmpty().Max() && NextWallId < int.MaxValue / 4);
        state.NextWallId = NextWallId;
        foreach (var saved in Walls)
        {
            var wall = byId[saved.Id];
            if (saved.SharedSectionId is { } owner)
            {
                Require(owner != saved.Id && byId.TryGetValue(owner, out var section) &&
                    Walls.Single(w => w.Id == owner).SharedSectionId is null &&
                    section.Health == wall.Health && section.MaxHealth == wall.MaxHealth &&
                    section.SectionBounds == wall.SectionBounds);
                wall.SharedSection = byId[owner];
            }
            Require(saved.RepairAnimationActive is { } active &&
                (active ? wall.Health < wall.MaxHealth : wall.Health > wall.MaxHealth * WallComponent.RepairAnimationThreshold));
            wall.RepairAnimationActive = saved.RepairAnimationActive!.Value;
        }
        Require(UpgradeOrder is null ? UpgradeTargetMaterial is null :
            UpgradeTargetMaterial is not null && UpgradeOrder.Count > 0 &&
            UpgradeOrder.Distinct().Count() == UpgradeOrder.Count && UpgradeOrder.All(id => byId.ContainsKey(id)));
        state.UpgradeOrder = UpgradeOrder is null ? null : new(UpgradeOrder);
        Require(UpgradeTargetMaterial is null || UpgradedSections < state.UpgradeSections.Count());
        foreach (var wall in state.Walls)
        {
            Require(Math.Abs(wall.MaxHealth - SettlementState.MaterialHealth(state.MaterialFor(wall)) * wall.HealthUnits) < .01);
        }
        Require(EstablishedLand is not null && EstablishedLand.All(game.Map.InBounds) &&
            EstablishedLand.Distinct().Count() == EstablishedLand.Count);
        state.EstablishedLand.UnionWith(EstablishedLand!);
        var ids = new HashSet<int>();
        int maximumTier = 1 + (game.HighestPopulation >= 250 ? 1 : 0) + (game.HighestPopulation >= 500 ? 1 : 0) +
            (game.HighestPopulation >= 1_000 ? 1 : 0) + (game.HighestPopulation >= 2_500 ? 1 : 0);
        foreach (var goblin in Enemies!)
        {
            Require(goblin is not null && goblin.Id > 0 && goblin.Id < NextEnemyId && ids.Add(goblin.Id) &&
                Range(goblin.X, 0.5, game.Map.Width - 0.5) && Range(goblin.Y, 0.5, game.Map.Height - 0.5) &&
                Enum.IsDefined(goblin.Kind) && (goblin.Kind != SiegeEnemyKind.Orc || OrcsIntroduced && game.HighestPopulation >= 250) &&
                goblin.AppearanceSeed is >= 0 and < 1_000_000 &&
                (!SiegeEnemies.Get(goblin.Kind).IsTroll || TrollsIntroduced && game.HighestPopulation >= 1_000) &&
                goblin.Tier >= 1 && goblin.Tier <= maximumTier &&
                (goblin.MaxHealth == SiegeEnemies.Get(goblin.Kind).MaxHealth(goblin.Tier, WaveNumber) ||
                    goblin.Kind == SiegeEnemyKind.Goblin &&
                    goblin.MaxHealth == 32 + 18 * (goblin.Tier - 1) + 3 * Math.Min(30, WaveNumber - 1)) &&
                Range(goblin.Health, double.Epsilon, goblin.MaxHealth) &&
                Range(goblin.AttackCooldown, 0, SiegeEnemies.Get(goblin.Kind).AttackIntervalSeconds) &&
                Range(goblin.BlockedSeconds, 0, 0.600000001) &&
                goblin.Route is { Count: > 0 } && goblin.Route.Count <= SettlementState.MaximumRouteLength && goblin.RouteIndex >= 0 &&
                goblin.RouteIndex < goblin.Route.Count);
            Require(state.WallAt(goblin!.Route[^1]) is not null);
            Require(Range(goblin.OrcAttackSeconds, 0, OrcCombatAnimation.Duration - 1e-10) &&
                (!goblin.OrcAttacking || goblin.Kind == SiegeEnemyKind.Orc) &&
                (goblin.OrcAttacking || goblin.OrcAttackSeconds == 0));
            for (int i = 0; i < goblin.Route.Count; i++)
            {
                var p = goblin.Route[i];
                Require(game.Map.InBounds(p) && game.Map.TerrainAt(p.X, p.Y).Buildable);
                if (i > 0) Require(Math.Abs(p.X - goblin.Route[i - 1].X) + Math.Abs(p.Y - goblin.Route[i - 1].Y) == 1);
            }
            var cell = new Pos((int)Math.Floor(goblin.X), (int)Math.Floor(goblin.Y));
            var next = goblin.Route[goblin.RouteIndex];
            Require(game.Map.TerrainAt(cell.X, cell.Y).Buildable &&
                Math.Abs(goblin.X - next.X - 0.5) + Math.Abs(goblin.Y - next.Y - 0.5) <= 1.000001 &&
                (Math.Abs(goblin.X - next.X - 0.5) < 1e-6 || Math.Abs(goblin.Y - next.Y - 0.5) < 1e-6));
            double goblinMaxHealth = SiegeEnemies.Get(goblin.Kind).MaxHealth(goblin.Tier, WaveNumber);
            // Preserve damage percentage in old saves and exact health in current-balance snapshots.
            double health = goblin.MaxHealth == goblinMaxHealth ? goblin.Health :
                goblin.Health * goblinMaxHealth / goblin.MaxHealth;
            state.EnemyList.Add(new SiegeEnemy { Id = goblin.Id, X = goblin.X, Y = goblin.Y, Health = health,
                MaxHealth = goblinMaxHealth, Tier = goblin.Tier, Route = new(goblin.Route), RouteIndex = goblin.RouteIndex,
                AttackCooldown = goblin.AttackCooldown, BlockedSeconds = goblin.BlockedSeconds, Kind = goblin.Kind,
                AppearanceSeed = goblin.AppearanceSeed, OrcAttacking = goblin.OrcAttacking,
                OrcAttackSeconds = goblin.OrcAttackSeconds, PreviousOrcAttackSeconds = goblin.OrcAttackSeconds });
        }
        Require(Range(RepairPhase, 0, 1) && RepairPhase < 1 &&
            (RepairTarget is { } repairTarget
                ? WaveNumber > 0 && RepairTargetId is { } repairId && byId.TryGetValue(repairId, out var repairWall) &&
                    repairWall.Cell == repairTarget && repairWall.Health < repairWall.MaxHealth
                : RepairPhase == 0 && RepairTargetId is null));
        state.RepairTarget = RepairTarget;
        state.RepairTargetId = RepairTargetId;
        state.RepairPhase = RepairPhase;
        state.SeparateLoadedCrowd(game);
        Garrison?.Restore(game, state);
        Require(NextAttackId > 0 && Attacks is not null && Attacks.Count <= SettlementState.MaximumPendingAttacks &&
            Attacks.All(attack => attack is not null) && Attacks.Select(attack => attack.Id).Distinct().Count() == Attacks.Count);
        state.NextAttackId = NextAttackId;
        foreach (var attack in Attacks!) state.ProjectileList.Add(attack.Restore(game, NextAttackId));
        state.Revision = Revision;
        return state;
    }
}
