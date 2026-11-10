using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public readonly record struct SiegeEnemyPose(Vector2 Position, Vector2 Forward, float Stride, float Bob, float Sway, float Attack)
{
    public bool OrcAttacking { get; init; }
    public double OrcAnimationSeconds { get; init; }
    public double WalkDistance { get; init; }
}

public static class SiegeMotion
{
    public static Vector2 IdleDefenderForward(SettlementState siege, SettlementDefender unit)
    {
        if (unit.Kind != DefenderKind.Archer) return -Vector2.UnitY;
        var position = Vector2.Lerp(new((float)unit.PreviousX, (float)unit.PreviousY),
            new((float)unit.X, (float)unit.Y), (float)siege.InterpolationFraction);
        var map = siege.AttachedMap;
        float level = unit.Elevation + (map is null ? 0 :
            TerrainSprite.Elevation(map, (int)unit.X, (int)unit.Y));
        var support = unit.Elevation > .01f ? siege.Walls
            .Where(wall => !wall.IsGateWing && WallDamageLayout.ContainsWalkway(wall, position.X, position.Y))
            .OrderBy(wall => Math.Abs(WallDamageLayout.WalkHeight(wall) +
                (map is null ? 0 : WallDamageLayout.GroundHeight(wall, map)) - level))
            .ThenBy(wall => wall.IsTower ? 0 : 1)
            .ThenBy(wall => Vector2.DistanceSquared(wall.Center, position)).FirstOrDefault() : null;
        if (support is not null)
            return support.IsTower ? -WallDamageLayout.LadderInward(support) :
                new Vector2(-support.Tangent.Y, support.Tangent.X) * WallDamageLayout.ExteriorSide(support);
        var outward = position - new Vector2(siege.Bounds.X + siege.Bounds.Width / 2f,
            siege.Bounds.Y + siege.Bounds.Height / 2f);
        return outward.LengthSquared() > .00001f ? Vector2.Normalize(outward) : -Vector2.UnitY;
    }

    public static SiegeEnemyPose Enemy(SettlementState siege, SiegeEnemy goblin)
    {
        float fraction = (float)siege.InterpolationFraction;
        var current = new Vector2((float)goblin.X, (float)goblin.Y);
        var previous = goblin.MotionInitialized ? new Vector2((float)goblin.PreviousX, (float)goblin.PreviousY) : current;
        var position = Vector2.Lerp(previous, current, fraction);
        var movement = current - previous;
        bool walking = movement.LengthSquared() > 0.000001f;
        var attackable = siege.AttackableWall(goblin);
        var wall = attackable?.Cell ?? goblin.Route[^1];
        var defender = siege.EngagedDefender(goblin);
        var towardWall = (defender is not null ? new Vector2((float)defender.X, (float)defender.Y) :
            goblin.Kind == SiegeEnemyKind.Orc && attackable is not null ?
                SettlementState.WallAttackPoint(attackable, goblin.X, goblin.Y) :
            new Vector2(wall.X + 0.5f, wall.Y + 0.5f)) - position;
        var forward = walking ? movement : towardWall;
        if (forward.LengthSquared() < 0.000001f)
            forward = new Vector2(siege.Bounds.X + siege.Bounds.Width / 2f, siege.Bounds.Y + siege.Bounds.Height / 2f) - position;
        forward = Vector2.Normalize(forward);
        double distance = goblin.PreviousWalkDistance +
            (goblin.WalkDistance - goblin.PreviousWalkDistance) * fraction;
        float stride = walking ? (float)Math.Sin(distance * Math.Tau / 0.6 + goblin.Id * 0.7) : 0;
        float attack = !walking && (defender is null ? attackable is not null :
            towardWall.LengthSquared() <= Math.Pow(SettlementState.MeleeDistance(goblin), 2))
            ? AttackSwing((goblin.Definition.AttackIntervalSeconds - goblin.AttackCooldown +
                siege.InterpolationFraction * SettlementState.StepSeconds) / goblin.Definition.AttackIntervalSeconds) : 0;
        if (goblin.Kind == SiegeEnemyKind.Orc)
            attack = goblin.OrcAttacking ? OrcCombatAnimation.Swing(OrcAnimationTime(goblin, fraction)) : 0;
        return new(position, forward, stride, Math.Abs(stride) * 0.065f, stride * 0.045f, attack)
        {
            OrcAttacking = goblin.OrcAttacking,
            OrcAnimationSeconds = OrcAnimationTime(goblin, fraction),
            WalkDistance = distance,
        };
    }

    internal static double OrcAnimationTime(SiegeEnemy enemy, double fraction)
    {
        double current = enemy.OrcAttackSeconds;
        if (current < enemy.PreviousOrcAttackSeconds) current += OrcCombatAnimation.Duration;
        return (enemy.PreviousOrcAttackSeconds + (current - enemy.PreviousOrcAttackSeconds) * fraction) %
            OrcCombatAnimation.Duration;
    }

    public static float AttackSwing(double progress)
    {
        float phase = (float)(progress - Math.Floor(progress));
        static float Ease(float value) => value * value * (3 - 2 * value);
        if (phase < 0.18f) return 1 - Ease(phase / 0.18f);
        if (phase < 0.55f) return 0;
        if (phase < 0.85f) return -Ease((phase - 0.55f) / 0.3f);
        return -1 + 2 * Ease((phase - 0.85f) / 0.15f);
    }

    public static float ProjectileProgress(SettlementState siege, SiegeProjectile projectile) =>
        (float)Math.Clamp(projectile.Progress + siege.InterpolationFraction * SettlementState.StepSeconds / projectile.FlightSeconds, 0, 1);

    public static double ProjectileElapsed(SettlementState siege, SiegeProjectile projectile) =>
        projectile.Elapsed + siege.InterpolationFraction * SettlementState.StepSeconds;

    public static float ProjectileFlightProgress(SettlementState siege, SiegeProjectile projectile) =>
        (float)Math.Clamp((ProjectileElapsed(siege, projectile) - projectile.Windup) / projectile.TravelSeconds, 0, 1);
}
