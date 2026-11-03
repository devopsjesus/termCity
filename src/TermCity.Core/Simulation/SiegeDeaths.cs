using System.Numerics;

namespace TermCity.Core.Simulation;

public enum SiegeDeathCause { Arrow, Blade, Crushing, Oil, Fall }
public sealed record SiegeDeath(Vector2 Position, float Elevation, bool Friendly, DefenderKind Defender,
    SiegeEnemyKind Enemy, float Scale, SiegeDeathCause Cause, Vector2 Direction)
{
    public int Tier { get; init; }
    public int AppearanceSeed { get; init; }
    public SiegeEnemy EnemyAppearance => new() { Kind = Enemy, Tier = Tier, AppearanceSeed = AppearanceSeed };
    public double Seconds { get; internal set; }
    public double Duration => Cause == SiegeDeathCause.Fall ? 1.8 : 1.4;
}

public sealed partial class SettlementState
{
    private readonly List<SiegeDeath> _deaths = [];
    public IReadOnlyList<SiegeDeath> Deaths => _deaths;

    internal void DamageEnemy(SiegeEnemy enemy, double damage, SiegeDeathCause cause, Vector2 source)
    {
        if (enemy.Health <= 0) return;
        enemy.Health = Math.Max(0, enemy.Health - damage);
        if (enemy.Health <= 0)
            RememberDeath(new(new((float)enemy.X, (float)enemy.Y), 0, false, default, enemy.Kind,
                (float)enemy.BodyScale, cause, Away(new((float)enemy.X, (float)enemy.Y), source))
                { Tier = enemy.Tier, AppearanceSeed = enemy.AppearanceSeed });
    }

    private void RememberDeath(SiegeDeath death)
    {
        if (_deaths.Count >= 184) _deaths.RemoveAt(0);
        _deaths.Add(death);
    }

    private static Vector2 Away(Vector2 point, Vector2 source) =>
        Vector2.DistanceSquared(point, source) > .00001f ? Vector2.Normalize(point - source) : Vector2.UnitY;

    private void AdvanceDeaths(double seconds)
    {
        foreach (var death in _deaths) death.Seconds += seconds;
        _deaths.RemoveAll(death => death.Seconds >= death.Duration);
    }
}
