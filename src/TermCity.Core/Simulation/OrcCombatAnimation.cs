namespace TermCity.Core.Simulation;

public static class OrcCombatAnimation
{
    public const double Duration = 3.7 + 1d / 30;
    public const double PlaybackRate = 1.25;
    public const double CycleSeconds = Duration / PlaybackRate;
    public const double WallStandOff = 1.40;
    public const double WallReach = 1.50;
    private static readonly double[] Contacts = [1.01 + 1d / 30, 1.8841313070017447, 2.99 + 1d / 30];
    public static IReadOnlyList<double> ImpactSeconds { get; } = Array.AsReadOnly(Contacts);

    public static int ImpactsBetween(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start || end - start > Duration)
            throw new ArgumentOutOfRangeException(nameof(end));
        int count = 0;
        foreach (double contact in Contacts)
        {
            double next = contact + Math.Max(0, Math.Floor((start - contact) / Duration) + 1) * Duration;
            if (next > start + 1e-8 && next <= end + 1e-8) count++;
        }
        return count;
    }

    public static float Swing(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        double phase = seconds % Duration;
        foreach (double contact in Contacts)
        {
            double since = phase - contact;
            if (since is >= 0 and < .18)
            {
                double u = since / .18;
                return (float)(1 - u * u * (3 - 2 * u));
            }
            if (since is >= -.25 and < 0)
            {
                double u = (since + .25) / .25;
                return (float)(-u * u * (3 - 2 * u));
            }
        }
        return 0;
    }

    internal static double Advance(SiegeEnemy enemy, double seconds)
    {
        if (!enemy.OrcAttacking)
        {
            enemy.OrcAttacking = true;
            enemy.OrcAttackSeconds = enemy.PreviousOrcAttackSeconds = 0;
        }
        double start = enemy.OrcAttackSeconds, end = start + seconds * PlaybackRate;
        int impacts = ImpactsBetween(start, end);
        enemy.PreviousOrcAttackSeconds = start;
        enemy.OrcAttackSeconds = end % Duration;
        // Three blows deliver the same total damage as the old one-second cadence.
        return impacts * enemy.Definition.AttackDamage(enemy.Tier) * CycleSeconds /
            (Contacts.Length * enemy.Definition.AttackIntervalSeconds);
    }

    internal static void Stop(SiegeEnemy enemy)
    {
        enemy.OrcAttacking = false;
        enemy.OrcAttackSeconds = enemy.PreviousOrcAttackSeconds = 0;
    }
}
