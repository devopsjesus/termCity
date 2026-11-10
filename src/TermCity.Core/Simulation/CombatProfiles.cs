namespace TermCity.Core.Simulation;

public sealed record FighterAttackProfile(double Damage, double Interval, double Range, double Windup);

public static class CombatProfiles
{
    public static FighterAttackProfile Archer { get; } = new(40, 1.5, 9, .3);
    public static FighterAttackProfile Guard { get; } = new(60, 1.2, 1.35, .3);
    public static FighterAttackProfile Fighter(DefenderKind kind) => kind switch
    {
        DefenderKind.Archer => Archer,
        DefenderKind.Soldier => Guard,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static float Swing(DefenderKind kind, double elapsed)
    {
        var profile = Fighter(kind);
        if (elapsed < 0 || elapsed >= profile.Interval) return 0;
        if (elapsed < profile.Windup)
        {
            float t = (float)(elapsed / profile.Windup);
            return -t * t * (3 - 2 * t);
        }
        float recovery = (float)Math.Clamp((elapsed - profile.Windup) / .3, 0, 1);
        return 1 - recovery * recovery * (3 - 2 * recovery);
    }
}
