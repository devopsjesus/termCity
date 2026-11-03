namespace TermCity.Core.Simulation;

public enum SiegeEnemyKind { Goblin, Orc, TreeTroll, StoneTroll, MudTroll }

public sealed record SiegeEnemyDefinition(string Name, double BaseHealth, int BaseTier, double HealthPerTier,
    double HealthPerWave, double BaseDamage, double DamagePerTier, double BaseSpeed, double SpeedPerTier,
    double SizeMultiplier)
{
    public double AttackIntervalSeconds { get; init; } = 1;
    public bool IsTroll { get; init; }
    public double WidthMultiplier { get; init; } = 1;
    public double MaxHealth(int tier, int waveNumber) =>
        BaseHealth + HealthPerTier * (tier - BaseTier) + HealthPerWave * Math.Min(30, waveNumber - 1);
    public double AttackDamage(int tier) => BaseDamage + DamagePerTier * tier;
    public double MovementSpeed(int tier) => BaseSpeed + SpeedPerTier * tier;
    public double BodyScale(int tier) => 0.75 * SizeMultiplier;
    public double CollisionRadius(int tier) => BodyScale(tier) * 0.62 * WidthMultiplier;
}

public static class SiegeEnemies
{
    public static SiegeEnemyDefinition Goblin { get; } = new("Goblin", 96, 1, 36, 6, 4, 2, 1.4, 0.1, 1);
    public static SiegeEnemyDefinition Orc { get; } = new("Orc", 420, 2, 65, 12, 46, 8, 1.05, 0, 4d / 3);
    public static SiegeEnemyDefinition TreeTroll { get; } = new("Tree troll", 650, 4, 90, 14, 52, 9, 0.9, 0, 16d / 9)
        { IsTroll = true, WidthMultiplier = 0.8, AttackIntervalSeconds = 1.6 };
    public static SiegeEnemyDefinition StoneTroll { get; } = new("Stone troll", 1100, 4, 120, 18, 90, 12, 0.55, 0, 16d / 9)
        { IsTroll = true, WidthMultiplier = 1.3, AttackIntervalSeconds = 2.5 };
    public static SiegeEnemyDefinition MudTroll { get; } = new("Mud troll", 820, 4, 100, 16, 62, 10, 0.7, 0, 16d / 9)
        { IsTroll = true, WidthMultiplier = 1.1, AttackIntervalSeconds = 1.9 };

    public static SiegeEnemyDefinition Get(SiegeEnemyKind kind) => kind switch
    {
        SiegeEnemyKind.Goblin => Goblin,
        SiegeEnemyKind.Orc => Orc,
        SiegeEnemyKind.TreeTroll => TreeTroll,
        SiegeEnemyKind.StoneTroll => StoneTroll,
        SiegeEnemyKind.MudTroll => MudTroll,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
