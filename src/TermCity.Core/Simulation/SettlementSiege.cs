using TermCity.Core.Util;
using TermCity.Core.World;
using System.Numerics;

namespace TermCity.Core.Simulation;

public enum WallMaterial { Wood, Stone, ReinforcedStone }
public enum WallDefenseKind { None, OilCauldron, Rocks, Ballista, Trebuchet }
public enum BaileyGateSide { Right, Bottom, Left, Top }

public sealed record WallDefenseDefinition(string Name, double Damage, double Range, double MinimumRange,
    double SplashRadius, double ReloadSeconds, int Cost, int RequiredPopulation);

public static class WallDefenses
{
    public static double MaximumReload => Enum.GetValues<WallDefenseKind>().Max(kind => Get(kind).ReloadSeconds);
    public static WallDefenseDefinition Get(WallDefenseKind kind) => kind switch
    {
        WallDefenseKind.None => new("Tower arrows", 24, 10, 0, 0, 2, 0, 0),
        WallDefenseKind.OilCauldron => new("Oil cauldron", 180, 2.5, 0, 0, 10, 600, 100),
        WallDefenseKind.Rocks => new("Dropped rocks", 140, 3.5, 0, 0, 7, 350, 100),
        WallDefenseKind.Ballista => new("Ballista", 240, 18, 3, 0, 6, 1_800, 250),
        WallDefenseKind.Trebuchet => new("Trebuchet", 360, 28, 8, 2.5, 14, 4_000, 1_000),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class WallComponent
{
    public const double RepairAnimationThreshold = 0.80;
    internal SettlementState? Settlement;
    public WallMaterial Material => Settlement?.MaterialFor(this) ?? BuiltMaterial ?? WallMaterial.Wood;
    private double _health, _maxHealth;
    private bool _repairAnimationActive;
    internal WallComponent? SharedSection;
    public int Id { get; internal set; }
    public Vector2? Start { get; internal set; }
    public Vector2? End { get; internal set; }
    public WallMaterial? BuiltMaterial { get; internal set; }
    public bool BelongsToFoundingWall { get; internal set; }
    public bool IsOriginal => Start is null;
    public Vector2 Center => Start is { } a && End is { } b ? (a + b) / 2 : new(Cell.X + .5f, Cell.Y + .5f);
    public Vector2 Tangent => Start is { } a && End is { } b ? Vector2.Normalize(b - a) :
        Settlement is { } settlement && Cell.Y != settlement.Bounds.Y && Cell.Y != settlement.Bounds.Bottom
            ? -Vector2.UnitY : Vector2.UnitX;
    public float Length => Start is { } a && End is { } b ? Vector2.Distance(a, b) : 1;
    public double HealthUnits => IsTower ? 1 : Start is null ? SectionLength : Length;
    public bool JunctionTower { get; internal set; }
    public Pos Cell { get; internal set; }
    public double Health
    {
        get => SharedSection?.Health ?? _health;
        internal set
        {
            if (SharedSection is { } section) section.Health = value;
            else
            {
                bool blocked = BlocksEnclosure;
                _health = value;
                UpdateRepairAnimation();
                if (blocked != BlocksEnclosure) Settlement?.InvalidateWallTopology(structureChanged: false);
            }
        }
    }
    public double MaxHealth
    {
        get => SharedSection?.MaxHealth ?? _maxHealth;
        internal set
        {
            if (SharedSection is { } section) section.MaxHealth = value;
            else
            {
                bool initializing = _maxHealth <= 0;
                _maxHealth = value;
                UpdateRepairAnimation(initializing);
            }
        }
    }
    public bool RepairAnimationActive
    {
        get => SharedSection?.RepairAnimationActive ?? _repairAnimationActive;
        internal set
        {
            if (SharedSection is { } section) section.RepairAnimationActive = value;
            else _repairAnimationActive = value;
        }
    }
    private void UpdateRepairAnimation(bool allowStart = true)
    {
        if (_maxHealth <= 0) return;
        if (_health >= _maxHealth) _repairAnimationActive = false;
        else if (allowStart && _health <= _maxHealth * RepairAnimationThreshold) _repairAnimationActive = true;
    }
    public Pos SectionAnchor => SharedSection?.Cell ?? Cell;
    public int SectionId => SharedSection?.Id ?? Id;
    public CellRect SectionBounds { get; internal set; }
    public int SectionLength => Math.Max(1, SectionBounds.Area);
    public bool IsTower { get; internal set; }
    public bool IsGate { get; internal set; }
    public bool IsGateWing { get; internal set; }
    public bool BlocksEnclosure => Health > 0 && (!IsGate || MaxHealth <= 0 || Health / MaxHealth > .5);
    public WallDefenseKind Defense { get; internal set; }
    public int DefenseFacing { get; internal set; } = 1;
    internal double ArrowCooldown, DefenseCooldown;

    internal WallComponent SnapshotCopy(SettlementState state)
    {
        var copy = (WallComponent)MemberwiseClone();
        copy.Settlement = state;
        copy.SharedSection = null;
        return copy;
    }

    internal void CopySnapshot(WallComponent source)
    {
        _health = source._health;
        _maxHealth = source._maxHealth;
        _repairAnimationActive = source._repairAnimationActive;
        Id = source.Id;
        Start = source.Start;
        End = source.End;
        BuiltMaterial = source.BuiltMaterial;
        BelongsToFoundingWall = source.BelongsToFoundingWall;
        JunctionTower = source.JunctionTower;
        Cell = source.Cell;
        SectionBounds = source.SectionBounds;
        IsTower = source.IsTower;
        IsGate = source.IsGate;
        IsGateWing = source.IsGateWing;
        Defense = source.Defense;
        DefenseFacing = source.DefenseFacing;
        ArrowCooldown = source.ArrowCooldown;
        DefenseCooldown = source.DefenseCooldown;
    }
}

public sealed class SiegeProjectile
{
    internal SiegeProjectile SnapshotCopy() => (SiegeProjectile)MemberwiseClone();
    internal void CopySnapshot(SiegeProjectile source)
    {
        Id = source.Id; SourceDefenderId = source.SourceDefenderId; TargetId = source.TargetId;
        Melee = source.Melee; Damage = source.Damage; Elapsed = source.Elapsed;
        Windup = source.Windup; TravelSeconds = source.TravelSeconds; ArcHeight = source.ArcHeight;
        Applied = source.Applied;
        WorldStartHeight = source.WorldStartHeight; WorldEndHeight = source.WorldEndHeight;
        FootprintCenter = source.FootprintCenter; CoverageOrigin = source.CoverageOrigin;
        FootprintForward = source.FootprintForward; StationIndex = source.StationIndex;
        SourceWallId = source.SourceWallId; DropHeight = source.DropHeight;
        StartHeight = source.StartHeight; EndHeight = source.EndHeight;
        StartX = source.StartX; StartY = source.StartY; EndX = source.EndX; EndY = source.EndY;
        Progress = source.Progress; Kind = source.Kind;
    }

    public int Id { get; internal set; }
    public int SourceDefenderId { get; internal set; }
    public int? TargetId { get; internal set; }
    public bool Melee { get; internal set; }
    public double Damage { get; internal set; }
    public double Elapsed { get; internal set; }
    public double Windup { get; internal set; }
    public double TravelSeconds { get; internal set; }
    public float ArcHeight { get; internal set; }
    public bool Applied { get; internal set; }
    public float WorldStartHeight { get; internal set; }
    public float WorldEndHeight { get; internal set; }
    public Vector2 FootprintCenter { get; internal set; }
    public Vector2 CoverageOrigin { get; internal set; }
    public Vector2 FootprintForward { get; internal set; }
    public int StationIndex { get; internal set; } = -1;
    public double ContactSeconds => Windup + TravelSeconds;
    public int SourceWallId { get; internal set; }
    public float DropHeight { get; internal set; }
    public float StartHeight { get; internal set; } = 2.9f;
    public double StartX { get; internal set; }
    public double StartY { get; internal set; }
    public double EndX { get; internal set; }
    public double EndY { get; internal set; }
    public float EndHeight { get; internal set; } = 0.5f;
    public double Progress { get; internal set; }
    public WallDefenseKind Kind { get; internal set; }
    public double FlightSeconds => Id > 0 ? ContactSeconds +
        (Kind == WallDefenseKind.OilCauldron ? Rendering.TowerDefenseArtwork.PourSeconds : .4) : Kind switch
    {
        WallDefenseKind.None or WallDefenseKind.Ballista =>
            Math.Clamp(Math.Sqrt(Math.Pow(EndX - StartX, 2) + Math.Pow(EndY - StartY, 2)) / 55, 0.08, 0.28),
        WallDefenseKind.OilCauldron or WallDefenseKind.Rocks =>
            Rendering.TowerDefenseArtwork.WindupSeconds +
            Rendering.TowerDefenseArtwork.FallSeconds(DropHeight > 0 ? DropHeight : StartHeight - EndHeight) +
            (Kind == WallDefenseKind.OilCauldron ? Rendering.TowerDefenseArtwork.PourSeconds : 0) +
            Rendering.TowerDefenseArtwork.ImpactSeconds,
        _ => .65,
    };
}

public sealed partial class SettlementState
{
    public const int MaximumEnemySwarm = 120;
    internal const int MaximumRouteLength = 64;
    public const int MinimumEnclosureSize = 12;
    public const int MaximumEnclosureSize = 160;
    internal const double StepSeconds = 0.1;
    internal readonly List<WallComponent> WallList = [];
    internal readonly List<SiegeEnemy> EnemyList = [];
    internal readonly List<SiegeProjectile> ProjectileList = [];
    internal double Accumulator;
    internal int NextEnemyId = 1;
    private readonly Dictionary<Pos, WallComponent> _wallAt = [];
    public CellRect Bounds { get; }
    public BaileyGateSide GateSide { get; }
    public IReadOnlyList<WallComponent> Walls { get; }
    public IReadOnlyList<SiegeEnemy> Enemies { get; }
    public IReadOnlyList<SiegeProjectile> Projectiles { get; }
    public WallMaterial Material { get; internal set; }
    public int WaveNumber { get; internal set; }
    public double NextWaveSeconds { get; internal set; } = 120;
    public double NextWaveIntervalSeconds => WaveNumber == 0 ? 120 : Math.Max(45, 120 - 5 * WaveNumber);
    public double NextWaveFraction => Math.Clamp(NextWaveSeconds / NextWaveIntervalSeconds, 0, 1);
    public int Revision { get; internal set; }
    internal double? PresentationFraction, PresentationSeconds;
    public double InterpolationFraction => PresentationFraction ?? Math.Clamp(Accumulator / StepSeconds, 0, 1);
    public double VisualSeconds => PresentationSeconds ?? (Revision + InterpolationFraction) * StepSeconds;

    internal SettlementState(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right, bool groupWallSections = true,
        GameMap? terrain = null)
    {
        Bounds = bounds;
        GateSide = gateSide;
        Walls = WallList.AsReadOnly();
        Enemies = EnemyList.AsReadOnly();
        Projectiles = ProjectileList.AsReadOnly();
        var gate = gateSide switch
        {
            BaileyGateSide.Right => new Pos(bounds.Right, bounds.Y + bounds.Height / 2),
            BaileyGateSide.Bottom => new Pos(bounds.X + bounds.Width / 2, bounds.Bottom),
            BaileyGateSide.Left => new Pos(bounds.X, bounds.Y + bounds.Height / 2),
            _ => new Pos(bounds.X + bounds.Width / 2, bounds.Y),
        };
        foreach (var p in bounds.Cells())
        {
            if (p.X != bounds.X && p.X != bounds.Right && p.Y != bounds.Y && p.Y != bounds.Bottom) continue;
            bool corner = (p.X == bounds.X || p.X == bounds.Right) && (p.Y == bounds.Y || p.Y == bounds.Bottom);
            bool tower = corner ||
                (p.Y == bounds.Y || p.Y == bounds.Bottom) && WorldScale.TowerPosition(p.X, bounds.X, bounds.Right) ||
                (p.X == bounds.X || p.X == bounds.Right) && WorldScale.TowerPosition(p.Y, bounds.Y, bounds.Bottom);
            bool gateSideCell = gateSide is BaileyGateSide.Right or BaileyGateSide.Left
                ? p.X == gate.X : p.Y == gate.Y;
            if (!corner && gateSideCell)
            {
                var location = gateSide is BaileyGateSide.Right or BaileyGateSide.Left
                    ? WorldScale.GateTower(bounds.Y, bounds.Bottom) : WorldScale.GateTower(bounds.X, bounds.Right);
                int along = gateSide is BaileyGateSide.Right or BaileyGateSide.Left ? p.Y : p.X;
                if (location.Original != location.Adjusted)
                {
                    if (along == location.Original) tower = false;
                    if (along == location.Adjusted) tower = true;
                }
            }
            bool wing = Math.Abs(p.X - gate.X) + Math.Abs(p.Y - gate.Y) == 1 &&
                (gateSide is BaileyGateSide.Right or BaileyGateSide.Left ? p.X == gate.X : p.Y == gate.Y);
            var wall = new WallComponent { Settlement = this, Id = WallList.Count + 1, BelongsToFoundingWall = true,
                Cell = p, Health = 200, MaxHealth = 200,
                IsGate = p == gate, IsGateWing = wing, IsTower = tower && !wing && p != gate &&
                    (terrain is null || WorldScale.HasDryTowerFootprint(terrain, new(p.X + .5f, p.Y + .5f))),
                SectionBounds = CellRect.Single(p) };
            WallList.Add(wall);
            _wallAt.Add(p, wall);
        }
        if (groupWallSections) GroupWallSections();
        NextWallId = WallList.Count + 1;
    }

    public bool ContainsInterior(Pos p) => WallNetwork.Contains(new(p.X + .5f, p.Y + .5f));
    internal WallComponent? WallAt(Pos p)
    {
        if (_wallAt.TryGetValue(p, out var original)) return original;
        EnsureWallSpatial();
        if (!_wallSpatial!.TryGetValue(p, out var walls)) return null;
        var point = new Vector2(p.X + .5f, p.Y + .5f);
        WallComponent? nearest = null;
        float distance = float.PositiveInfinity;
        foreach (var wall in walls)
        {
            if (wall.IsOriginal || Vector2.Distance(wall.Center, point) > 2 ||
                !Rendering.WallDamageLayout.ContainsFootprint(wall, point.X, point.Y)) continue;
            float current = Vector2.DistanceSquared(wall.Center, point);
            if (current >= distance) continue;
            distance = current;
            nearest = wall;
        }
        return nearest;
    }

    internal void EnsureFirstWave(CityGame game)
    {
        if (WaveNumber == 0 && game.HighestPopulation >= 100) SpawnWave(game);
    }

    internal void Advance(CityGame game, double seconds)
    {
        PlanFirstOrcApproach(game);
        PlanFirstTrollApproach(game);
        var initialSpeed = game.Speed;
        EnsureFirstWave(game);
        if (game.Speed != initialSpeed)
            seconds *= game.Config.SecondsPerWeek(initialSpeed) / game.Config.SecondsPerWeek(game.Speed);
        AdvanceUpgrades(game, seconds);
        if (WaveNumber == 0) return;
        Accumulator += seconds;
        while (Accumulator + 1e-9 >= StepSeconds)
        {
            Accumulator = Math.Max(0, Accumulator - StepSeconds);
            var speed = game.Speed;
            Tick(game);
            if (game.Speed != speed)
                Accumulator *= game.Config.SecondsPerWeek(speed) / game.Config.SecondsPerWeek(game.Speed);
        }
    }

    internal bool SpawnWave(CityGame game, int? requestedCount = null)
    {
        int tier = 1 + (game.HighestPopulation >= 250 ? 1 : 0) + (game.HighestPopulation >= 500 ? 1 : 0) +
            (game.HighestPopulation >= 1_000 ? 1 : 0) + (game.HighestPopulation >= 2_500 ? 1 : 0);
        var (spawns, previous) = BuildCrowdRoutes(game);
        spawns = spawns.Where(p => EnemySpawnClear(game, p.X + 0.5, p.Y + 0.5,
            SiegeEnemies.Goblin.CollisionRadius(tier))).ToList();
        int count = requestedCount ?? Math.Min(MaximumEnemySwarm, 20 + (WaveNumber + 1) * 4 + (tier - 1) * 8);
        bool mixed = requestedCount is null && game.HighestPopulation >= 250;
        var orcs = new List<Pos>();
        var orcRoutes = previous;
        if (mixed)
        {
            var accessible = BuildCrowdRoutes(game, SiegeEnemies.Orc.CollisionRadius(tier));
            orcRoutes = accessible.Previous;
            var available = OrcApproachSpawns(game, accessible.Spawns, orcRoutes, tier);
            var side = PlannedOrcSide ?? ChooseOrcSide(available, orcRoutes);
            var approach = available.Where(p => SideOf(CrowdRoute(p, orcRoutes)[^1]) == side).ToList();
            orcs = ChooseCrowdSpawns(approach, Math.Min(8, 3 + WaveNumber / 4), tier,
                WaveNumber + 1, SiegeEnemyKind.Orc, orcRoutes);
            double separation = SiegeEnemies.Orc.CollisionRadius(tier) + SiegeEnemies.Goblin.CollisionRadius(tier);
            spawns = spawns.Where(p => orcs.All(o =>
                Math.Pow(p.X - o.X, 2) + Math.Pow(p.Y - o.Y, 2) >= separation * separation)).ToList();
        }
        var trolls = new List<(Pos Spawn, SiegeEnemyKind Kind, int Seed)>();
        var trollRoutes = new Dictionary<SiegeEnemyKind, Dictionary<Pos, Pos>>();
        if (requestedCount is null && game.HighestPopulation >= 1_000 &&
            (TrollsIntroduced || PlannedTrollWave > 0 && WaveNumber + 1 >= PlannedTrollWave))
        {
            foreach (var kind in new[] { SiegeEnemyKind.StoneTroll, SiegeEnemyKind.MudTroll, SiegeEnemyKind.TreeTroll })
            {
                int seed = (int)(((long)(WaveNumber + 1) * 193 + (int)kind * 97) % 1_000_000);
                double radius = SiegeEnemies.Get(kind).CollisionRadius(tier) * (1 + (seed % 301) / 800d);
                var accessible = BuildCrowdRoutes(game, radius);
                var candidates = accessible.Spawns.Where(p => orcs.All(o =>
                        Math.Pow(p.X - o.X, 2) + Math.Pow(p.Y - o.Y, 2) >=
                        Math.Pow(radius + SiegeEnemies.Orc.CollisionRadius(tier), 2)) &&
                    trolls.All(t => Math.Pow(p.X - t.Spawn.X, 2) + Math.Pow(p.Y - t.Spawn.Y, 2) >=
                        Math.Pow(radius + SiegeEnemies.Get(t.Kind).CollisionRadius(tier) *
                            (1 + (t.Seed % 301) / 800d), 2)) &&
                    EnemySpawnClear(game, p.X + 0.5, p.Y + 0.5, radius)).ToList();
                if (candidates.Count == 0) continue;
                var spawn = candidates[seed % candidates.Count];
                trolls.Add((spawn, kind, seed));
                trollRoutes.Add(kind, accessible.Previous);
                spawns = spawns.Where(p => Math.Pow(p.X - spawn.X, 2) + Math.Pow(p.Y - spawn.Y, 2) >=
                    Math.Pow(radius + SiegeEnemies.Goblin.CollisionRadius(tier), 2)).ToList();
            }
        }
        var selected = ChooseCrowdSpawns(spawns, Math.Max(0, count - orcs.Count - trolls.Count), tier, WaveNumber + 1);
        if (requestedCount is not null && selected.Count != count) return false;
        if (requestedCount is not null)
        {
            EnemyList.Clear();
            ProjectileList.Clear();
            Accumulator = 0;
        }
        WaveNumber++;
        if (requestedCount is not null && !OrcsIntroduced && PlannedOrcWave > 0)
            PlannedOrcWave = Math.Max(PlannedOrcWave, WaveNumber + 1);
        if (requestedCount is not null && !TrollsIntroduced && PlannedTrollWave > 0)
            PlannedTrollWave = Math.Max(PlannedTrollWave, WaveNumber + 1);
        if (requestedCount is null && orcs.Count == 0 && !OrcsIntroduced &&
            PlannedOrcWave > 0 && PlannedOrcWave <= WaveNumber)
        {
            PlannedOrcWave = WaveNumber + 1;
            game.Report(new CityEvent(game.Week, EventKind.Raid,
                "The first Orcs were delayed by a blocked approach; scouts remain on watch.", [], Bad: false));
        }
        if (requestedCount is null && trolls.Count == 0 && !TrollsIntroduced &&
            PlannedTrollWave > 0 && PlannedTrollWave <= WaveNumber)
        {
            PlannedTrollWave = WaveNumber + 1;
            game.Report(new CityEvent(game.Week, EventKind.Raid,
                "The first trolls were delayed: no approach fits their size. Scouts remain on watch.", [], Bad: false));
        }
        ClearRepairCrew();
        NextWaveSeconds = NextWaveIntervalSeconds;
        foreach (var (spawn, kind) in orcs.Select(p => (p, SiegeEnemyKind.Orc))
            .Concat(selected.Select(p => (p, SiegeEnemyKind.Goblin))))
        {
            var route = CrowdRoute(spawn, kind == SiegeEnemyKind.Orc ? orcRoutes : previous);
            double hp = SiegeEnemies.Get(kind).MaxHealth(tier, WaveNumber);
            EnemyList.Add(new SiegeEnemy { Id = NextEnemyId++, X = spawn.X + 0.5, Y = spawn.Y + 0.5,
                PreviousX = spawn.X + 0.5, PreviousY = spawn.Y + 0.5, MotionInitialized = true,
                Health = hp, MaxHealth = hp, Tier = tier, Kind = kind, Route = route });
        }
        foreach (var (spawn, kind, seed) in trolls)
        {
            double hp = SiegeEnemies.Get(kind).MaxHealth(tier, WaveNumber);
            EnemyList.Add(new SiegeEnemy { Id = NextEnemyId++, X = spawn.X + 0.5, Y = spawn.Y + 0.5,
                PreviousX = spawn.X + 0.5, PreviousY = spawn.Y + 0.5, MotionInitialized = true,
                Health = hp, MaxHealth = hp, Tier = tier, Kind = kind, AppearanceSeed = seed,
                Route = CrowdRoute(spawn, trollRoutes[kind]) });
        }
        if (trolls.Count > 0) TrollsIntroduced = true;
        if (orcs.Count > 0)
        {
            if (!OrcsIntroduced)
            {
                FirstAssaultSide = PlannedOrcSide ?? SideOf(CrowdRoute(orcs[0], orcRoutes)[^1]);
                FirstAssaultLastOrcId = EnemyList.Where(e => e.Kind == SiegeEnemyKind.Orc).Max(e => e.Id);
            }
            OrcsIntroduced = true;
            PlannedOrcSide = null;
        }
        Revision++;
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"Siege wave {WaveNumber}: {selected.Count} tier-{tier} goblins" +
            (orcs.Count > 0 ? $" and {orcs.Count} Orcs" : "") +
            (trolls.Count > 0 ? $" and {trolls.Count} trolls" : "") + " approach over dry land.", [], Bad: true));
        game.BeginSiegeWave();
        return true;
    }

    internal static IEnumerable<Pos> Neighbors(Pos p)
    {
        yield return p.Offset(0, -1);
        yield return p.Offset(-1, 0);
        yield return p.Offset(1, 0);
        yield return p.Offset(0, 1);
    }

    private void Tick(CityGame game)
    {
        AdvanceDeaths(StepSeconds);
        bool breached = false;
        AdvanceCommittedAttacks(game, StepSeconds);
        AdvanceDefenders(game, StepSeconds);
        foreach (var shot in ProjectileList.Where(s => s.Id == 0)) shot.Progress += StepSeconds / shot.FlightSeconds;
        ProjectileList.RemoveAll(shot => shot.Progress >= 1);
        foreach (var wall in WallList)
        {
            wall.ArrowCooldown = Math.Max(0, wall.ArrowCooldown - StepSeconds);
            wall.DefenseCooldown = Math.Max(0, wall.DefenseCooldown - StepSeconds);
            if (wall.Health <= 0) continue;
            if (wall.IsTower && wall.ArrowCooldown <= 0 && Fire(game, wall, WallDefenseKind.None))
                wall.ArrowCooldown = WallDefenses.Get(WallDefenseKind.None).ReloadSeconds;
            if (wall.Defense != WallDefenseKind.None && wall.DefenseCooldown <= 0 && Fire(game, wall, wall.Defense))
                wall.DefenseCooldown = WallDefenses.Get(wall.Defense).ReloadSeconds;
        }
        EnemyList.RemoveAll(g => g.Health <= 0);
        PrepareCrowdMovement();
        foreach (var goblin in _movementOrder)
        {
            if (goblin.Health <= 0) continue;
            double attackDamage = goblin.Definition.AttackDamage(goblin.Tier);
            if (IsDefenderEngaged(goblin))
            {
                if (goblin.Kind == SiegeEnemyKind.Orc)
                {
                    var defender = EngagedDefender(goblin)!;
                    if (!MakeOrcAttackSpace(game, goblin, new((float)defender.X, (float)defender.Y),
                        OrcCombatAnimation.WallStandOff + WorldScale.HumanRadius)) continue;
                    double damage = OrcCombatAnimation.Advance(goblin, StepSeconds);
                    if (damage > 0) DefenderIntercepts(goblin, damage);
                    continue;
                }
                goblin.AttackCooldown = Math.Max(0, goblin.AttackCooldown - StepSeconds);
                if (goblin.AttackCooldown <= 1e-8)
                {
                    DefenderIntercepts(goblin, attackDamage);
                    goblin.AttackCooldown = goblin.Definition.AttackIntervalSeconds;
                }
                continue;
            }
            if (AttackableWall(goblin) is { } wall)
            {
                goblin.BlockedSeconds = 0;
                double damage;
                if (goblin.Kind == SiegeEnemyKind.Orc)
                {
                    if (!MakeOrcAttackSpace(game, goblin, WallAttackPoint(wall, goblin.X, goblin.Y),
                        OrcCombatAnimation.WallStandOff, WallRetreatDirection(wall))) continue;
                    damage = OrcCombatAnimation.Advance(goblin, StepSeconds);
                }
                else
                {
                    goblin.AttackCooldown = Math.Max(0, goblin.AttackCooldown - StepSeconds);
                    damage = goblin.AttackCooldown <= 1e-8 ? attackDamage : 0;
                    if (damage > 0) goblin.AttackCooldown = goblin.Definition.AttackIntervalSeconds;
                }
                if (damage > 0)
                {
                    wall.Health = Math.Max(0, wall.Health - damage);
                    if (wall.Health <= 0)
                    {
                        Breach(game, wall);
                        breached = true;
                        break;
                    }
                }
            }
            else
            {
                if (goblin.Kind == SiegeEnemyKind.Orc) OrcCombatAnimation.Stop(goblin);
                var next = goblin.Route[goblin.RouteIndex];
                double dx = next.X + 0.5 - goblin.X, dy = next.Y + 0.5 - goblin.Y;
                double length = Math.Sqrt(dx * dx + dy * dy),
                    movement = goblin.Definition.MovementSpeed(goblin.Tier) * StepSeconds;
                double travel = Math.Min(length, movement);
                double previousX = goblin.X, previousY = goblin.Y;
                if (length > 1e-12)
                    MoveCrowdMember(game, goblin, goblin.X + dx / length * travel, goblin.Y + dy / length * travel);
                if (Math.Abs(goblin.X - previousX) + Math.Abs(goblin.Y - previousY) < travel * 0.1)
                {
                    goblin.BlockedSeconds = Math.Min(0.6, goblin.BlockedSeconds + StepSeconds);
                    if (goblin.BlockedSeconds >= 0.6 - 1e-8)
                    {
                        RerouteBlockedGoblin(game, goblin);
                        goblin.BlockedSeconds = 0;
                    }
                }
                else goblin.BlockedSeconds = 0;
                if (Math.Abs(goblin.X - next.X - 0.5) + Math.Abs(goblin.Y - next.Y - 0.5) < 1e-8 &&
                    goblin.RouteIndex < goblin.Route.Count - 1) goblin.RouteIndex++;
            }
        }
        NextWaveSeconds = Math.Max(0, NextWaveSeconds - StepSeconds);
        if (NextWaveSeconds <= 1e-8 && EnemyList.Count == 0 && !ProjectileList.Any(s => s.Id > 0 && !s.Applied)) SpawnWave(game);
        if (!breached) AdvanceRepairs(game, StepSeconds);
        Revision++;
    }

    private bool Fire(CityGame game, WallComponent wall, WallDefenseKind kind)
    {
        return ReleaseMountedAttack(game, wall, kind);
    }

    private void Breach(CityGame game, WallComponent wall)
    {
        var exposed = game.Map.ZoneCells(ZoneType.Residential).Where(index =>
        {
            var p = game.Map.PosOf(index);
            return EstablishedAt(p) && !ContainsInterior(p);
        }).Order().ToArray();
        if (exposed.Length == 0)
        {
            EnemyList.Clear();
            game.Report(new CityEvent(game.Week, EventKind.Raid,
                "A wall was breached, but the inhabited districts remain enclosed. The raiders withdrew.",
                [game.Map.Index(wall.Cell.X, wall.Cell.Y)], Bad: false));
            game.NotifyStateChanged();
            return;
        }
        int lost = Math.Min(Math.Max(0, game.Money), 500 + WaveNumber * 100);
        game.Money -= lost;
        int casualties = 0, remaining = Math.Max(1, game.Stats.Population / 20);
        foreach (int index in exposed)
        {
            var h = game.Map.HouseholdLayer[index];
            int n = Math.Min(h.Total, remaining);
            if (n == 0) continue;
            int adults = Math.Min(h.Adults, n), children = Math.Min(h.Children, n - adults);
            game.Map.HouseholdLayer[index] = new Household((byte)(h.Adults - adults),
                (byte)(h.Children - children), (byte)(h.Seniors - (n - adults - children)));
            remaining -= n;
            casualties += n;
        }
        game.Tally.Deaths += casualties;
        EnemyList.Clear();
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"The settlement wall at ({wall.Cell.X},{wall.Cell.Y}) was breached: {casualties} residents slain and {lost} gold looted. The raiders withdrew; repair the breach.",
            [game.Map.Index(wall.Cell.X, wall.Cell.Y)]));
        game.NotifyStateChanged();
    }
}
