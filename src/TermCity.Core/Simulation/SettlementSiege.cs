using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public enum WallMaterial { Wood, Stone, ReinforcedStone }
public enum WallDefenseKind { None, OilCauldron, Rocks, Ballista, Trebuchet }
public enum BaileyGateSide { Right, Bottom, Left, Top }

public sealed record WallDefenseDefinition(string Name, double Damage, double Range, double MinimumRange,
    double SplashRadius, double ReloadSeconds, int Cost, int RequiredPopulation);

public static class WallDefenses
{
    public static WallDefenseDefinition Get(WallDefenseKind kind) => kind switch
    {
        WallDefenseKind.None => new("Tower arrows", 12, 10, 0, 0, 1.2, 0, 0),
        WallDefenseKind.OilCauldron => new("Oil cauldron", 18, 2.5, 0, 2, 5, 600, 100),
        WallDefenseKind.Rocks => new("Dropped rocks", 42, 3.5, 0, 1.5, 6, 350, 100),
        WallDefenseKind.Ballista => new("Ballista", 65, 18, 3, 0, 4, 1_800, 250),
        WallDefenseKind.Trebuchet => new("Trebuchet", 110, 28, 8, 3, 8, 4_000, 1_000),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class WallComponent
{
    private double _health, _maxHealth;
    internal WallComponent? SharedSection;
    public Pos Cell { get; internal set; }
    public double Health { get => SharedSection?.Health ?? _health; internal set { if (SharedSection is { } section) section.Health = value; else _health = value; } }
    public double MaxHealth { get => SharedSection?.MaxHealth ?? _maxHealth; internal set { if (SharedSection is { } section) section.MaxHealth = value; else _maxHealth = value; } }
    public Pos SectionAnchor => SharedSection?.Cell ?? Cell;
    public CellRect SectionBounds { get; internal set; }
    public int SectionLength => Math.Max(1, SectionBounds.Area);
    public bool IsTower { get; internal set; }
    public bool IsGate { get; internal set; }
    public WallDefenseKind Defense { get; internal set; }
    internal double ArrowCooldown, DefenseCooldown;
}

public sealed class Goblin
{
    public int Id { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Health { get; internal set; }
    public double MaxHealth { get; internal set; }
    public int Tier { get; internal set; }
    public double BodyScale => 0.65 + Math.Min(5, Tier) * 0.1;
    public double CollisionRadius => RadiusForTier(Tier);
    internal static double RadiusForTier(int tier) => (0.65 + Math.Min(5, tier) * 0.1) * 0.62;
    internal double PreviousX, PreviousY, WalkDistance, PreviousWalkDistance;
    internal bool MotionInitialized;
    internal List<Pos> Route = [];
    internal int RouteIndex;
    internal double AttackCooldown, BlockedSeconds;
}

public sealed class SiegeProjectile
{
    public double StartX { get; internal set; }
    public double StartY { get; internal set; }
    public double EndX { get; internal set; }
    public double EndY { get; internal set; }
    public double Progress { get; internal set; }
    public WallDefenseKind Kind { get; internal set; }
    public double FlightSeconds => Kind is WallDefenseKind.None or WallDefenseKind.Ballista
        ? Math.Clamp(Math.Sqrt(Math.Pow(EndX - StartX, 2) + Math.Pow(EndY - StartY, 2)) / 55, 0.08, 0.28)
        : 0.65;
}

public sealed partial class SettlementState
{
    public const int MaximumGoblinSwarm = 120;
    internal const int MaximumRouteLength = 64;
    public const int MinimumEnclosureSize = 12;
    public const int MaximumEnclosureSize = 160;
    internal const double StepSeconds = 0.1;
    internal readonly List<WallComponent> WallList = [];
    internal readonly List<Goblin> GoblinList = [];
    internal readonly List<SiegeProjectile> ProjectileList = [];
    internal double Accumulator;
    internal int NextGoblinId = 1;
    private readonly Dictionary<Pos, WallComponent> _wallAt = [];
    public CellRect Bounds { get; }
    public BaileyGateSide GateSide { get; }
    public IReadOnlyList<WallComponent> Walls { get; }
    public IReadOnlyList<Goblin> Goblins { get; }
    public IReadOnlyList<SiegeProjectile> Projectiles { get; }
    public WallMaterial Material { get; internal set; }
    public int WaveNumber { get; internal set; }
    public double NextWaveSeconds { get; internal set; } = 120;
    public double NextWaveIntervalSeconds => WaveNumber == 0 ? 120 : Math.Max(45, 120 - 5 * WaveNumber);
    public double NextWaveFraction => Math.Clamp(NextWaveSeconds / NextWaveIntervalSeconds, 0, 1);
    public int Revision { get; internal set; }
    public double InterpolationFraction => Math.Clamp(Accumulator / StepSeconds, 0, 1);

    internal SettlementState(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right, bool groupWallSections = true)
    {
        Bounds = bounds;
        GateSide = gateSide;
        Walls = WallList.AsReadOnly();
        Goblins = GoblinList.AsReadOnly();
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
                (p.Y == bounds.Y || p.Y == bounds.Bottom) && (p.X - bounds.X) % 15 == 0 ||
                (p.X == bounds.X || p.X == bounds.Right) && (p.Y - bounds.Y) % 15 == 0;
            var wall = new WallComponent { Cell = p, Health = 200, MaxHealth = 200,
                IsGate = p == gate, IsTower = tower, SectionBounds = CellRect.Single(p) };
            WallList.Add(wall);
            _wallAt.Add(p, wall);
        }
        if (groupWallSections) GroupWallSections();
    }

    public bool ContainsInterior(Pos p) => p.X > Bounds.X && p.X < Bounds.Right &&
        p.Y > Bounds.Y && p.Y < Bounds.Bottom;
    internal WallComponent? WallAt(Pos p) => _wallAt.GetValueOrDefault(p);

    internal static double GoblinMaxHealth(int tier, int waveNumber) =>
        96 + 36 * (tier - 1) + 6 * Math.Min(30, waveNumber - 1);

    internal void EnsureFirstWave(CityGame game)
    {
        if (WaveNumber == 0 && game.HighestPopulation >= 100) SpawnWave(game);
    }

    internal void Advance(CityGame game, double seconds)
    {
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
        int count = requestedCount ?? Math.Min(MaximumGoblinSwarm, 20 + (WaveNumber + 1) * 4 + (tier - 1) * 8);
        var selected = ChooseCrowdSpawns(spawns, count, tier, WaveNumber + 1);
        if (requestedCount is not null && selected.Count != count) return false;
        if (requestedCount is not null)
        {
            GoblinList.Clear();
            ProjectileList.Clear();
            Accumulator = 0;
        }
        WaveNumber++;
        ClearRepairCrew();
        NextWaveSeconds = NextWaveIntervalSeconds;
        foreach (var spawn in selected)
        {
            var route = CrowdRoute(spawn, previous);
            double hp = GoblinMaxHealth(tier, WaveNumber);
            GoblinList.Add(new Goblin { Id = NextGoblinId++, X = spawn.X + 0.5, Y = spawn.Y + 0.5,
                PreviousX = spawn.X + 0.5, PreviousY = spawn.Y + 0.5, MotionInitialized = true,
                Health = hp, MaxHealth = hp, Tier = tier, Route = route });
        }
        Revision++;
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"Goblin siege wave {WaveNumber}: {GoblinList.Count} tier-{tier} attackers approach over dry land.", [], Bad: true));
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
        bool hadAttackers = GoblinList.Count > 0;
        foreach (var shot in ProjectileList) shot.Progress += StepSeconds / shot.FlightSeconds;
        ProjectileList.RemoveAll(shot => shot.Progress >= 1);
        foreach (var wall in WallList)
        {
            wall.ArrowCooldown = Math.Max(0, wall.ArrowCooldown - StepSeconds);
            wall.DefenseCooldown = Math.Max(0, wall.DefenseCooldown - StepSeconds);
            if (wall.Health <= 0) continue;
            if (wall.IsTower && wall.ArrowCooldown <= 0 && Fire(wall, WallDefenseKind.None))
                wall.ArrowCooldown = WallDefenses.Get(WallDefenseKind.None).ReloadSeconds;
            if (wall.Defense != WallDefenseKind.None && wall.DefenseCooldown <= 0 && Fire(wall, wall.Defense))
                wall.DefenseCooldown = WallDefenses.Get(wall.Defense).ReloadSeconds;
        }
        GoblinList.RemoveAll(g => g.Health <= 0);
        PrepareCrowdMovement();
        foreach (var goblin in _movementOrder)
        {
            if (AttackableWall(goblin) is { } wall)
            {
                goblin.BlockedSeconds = 0;
                goblin.AttackCooldown = Math.Max(0, goblin.AttackCooldown - StepSeconds);
                if (goblin.AttackCooldown <= 1e-8)
                {
                    wall.Health = Math.Max(0, wall.Health - (4 + 2 * goblin.Tier));
                    goblin.AttackCooldown = 1;
                    if (wall.Health <= 0)
                    {
                        Breach(game, wall);
                        break;
                    }
                }
            }
            else
            {
                var next = goblin.Route[goblin.RouteIndex];
                double dx = next.X + 0.5 - goblin.X, dy = next.Y + 0.5 - goblin.Y;
                double length = Math.Sqrt(dx * dx + dy * dy), movement = (1.4 + 0.1 * goblin.Tier) * StepSeconds;
                double travel = Math.Min(length, movement);
                double previousX = goblin.X, previousY = goblin.Y;
                if (length > 1e-12)
                    MoveCrowdMember(goblin, goblin.X + dx / length * travel, goblin.Y + dy / length * travel);
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
        if (NextWaveSeconds <= 1e-8 && GoblinList.Count == 0) SpawnWave(game);
        if (!hadAttackers || GoblinList.Count > 0) AdvanceRepairs(game, StepSeconds);
        Revision++;
    }

    private bool Fire(WallComponent wall, WallDefenseKind kind)
    {
        var definition = WallDefenses.Get(kind);
        double x = wall.Cell.X + 0.5, y = wall.Cell.Y + 0.5;
        var target = GoblinList.Where(g => g.Health > 0)
            .Select(g => (Goblin: g, Distance: Math.Sqrt(Math.Pow(g.X - x, 2) + Math.Pow(g.Y - y, 2))))
            .Where(t => t.Distance >= definition.MinimumRange && t.Distance <= definition.Range)
            .OrderBy(t => t.Distance).ThenBy(t => t.Goblin.Id).Select(t => t.Goblin).FirstOrDefault();
        if (target is null) return false;
        // Damage is resolved on firing; projectiles are transient visual traces, not unsaved pending impacts.
        foreach (var goblin in GoblinList)
            if (goblin == target || definition.SplashRadius > 0 &&
                Math.Pow(goblin.X - target.X, 2) + Math.Pow(goblin.Y - target.Y, 2) <=
                definition.SplashRadius * definition.SplashRadius)
                goblin.Health = Math.Max(0, goblin.Health - definition.Damage);
        ProjectileList.Add(new SiegeProjectile { StartX = x, StartY = y, EndX = target.X, EndY = target.Y, Kind = kind });
        return true;
    }

    private void Breach(CityGame game, WallComponent wall)
    {
        int lost = Math.Min(Math.Max(0, game.Money), 500 + WaveNumber * 100);
        game.Money -= lost;
        int casualties = 0, remaining = Math.Max(1, game.Stats.Population / 20);
        foreach (int index in game.Map.ZoneCells(ZoneType.Residential).Order())
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
        GoblinList.Clear();
        game.Report(new CityEvent(game.Week, EventKind.Raid,
            $"The settlement wall at ({wall.Cell.X},{wall.Cell.Y}) was breached: {casualties} residents slain and {lost} gold looted. The raiders withdrew; repair the breach.",
            [game.Map.Index(wall.Cell.X, wall.Cell.Y)]));
        game.NotifyStateChanged();
    }
}
