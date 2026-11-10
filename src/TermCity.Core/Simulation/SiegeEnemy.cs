using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

public sealed class SiegeEnemy
{
    public SiegeEnemyKind Kind { get; internal set; }
    public SiegeEnemyDefinition Definition => SiegeEnemies.Get(Kind);
    public int Id { get; internal set; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Health { get; internal set; }
    public double MaxHealth { get; internal set; }
    public int Tier { get; internal set; }
    public int AppearanceSeed { get; internal set; }
    public double SizeVariation => Definition.IsTroll ? 1 + (AppearanceSeed % 301) / 800d : 1;
    public double HeightFeet => Definition.IsTroll ? 8 * SizeVariation : Kind == SiegeEnemyKind.Orc ? 6 : 4.5;
    public double BodyScale => Definition.BodyScale(Tier) * SizeVariation;
    public double CollisionRadius => Definition.CollisionRadius(Tier) * SizeVariation;
    internal double PreviousX, PreviousY, WalkDistance, PreviousWalkDistance;
    internal bool MotionInitialized;
    internal List<Pos> Route = [];
    internal int RouteIndex;
    internal double AttackCooldown, BlockedSeconds;
    public bool OrcAttacking { get; internal set; }
    public double OrcAttackSeconds { get; internal set; }
    internal double PreviousOrcAttackSeconds;

    internal SiegeEnemy SnapshotCopy()
    {
        var copy = (SiegeEnemy)MemberwiseClone();
        copy.Route = new(Route);
        return copy;
    }

    internal void CopySnapshot(SiegeEnemy source)
    {
        Kind = source.Kind;
        Id = source.Id;
        X = source.X; Y = source.Y;
        Health = source.Health; MaxHealth = source.MaxHealth;
        Tier = source.Tier; AppearanceSeed = source.AppearanceSeed;
        PreviousX = source.PreviousX; PreviousY = source.PreviousY;
        WalkDistance = source.WalkDistance; PreviousWalkDistance = source.PreviousWalkDistance;
        MotionInitialized = source.MotionInitialized;
        Route = new(source.Route);
        RouteIndex = source.RouteIndex;
        AttackCooldown = source.AttackCooldown; BlockedSeconds = source.BlockedSeconds;
        OrcAttacking = source.OrcAttacking;
        OrcAttackSeconds = source.OrcAttackSeconds;
        PreviousOrcAttackSeconds = source.PreviousOrcAttackSeconds;
    }
}
