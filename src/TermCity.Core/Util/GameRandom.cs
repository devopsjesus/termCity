namespace TermCity.Core.Util;

/// <summary>
/// Small deterministic PRNG (SplitMix64). Unlike <see cref="Random"/> its state can be saved and restored,
/// which keeps saved games and seeded maps reproducible across runs and platforms.
/// </summary>
public sealed class GameRandom
{
    private ulong _state;

    public GameRandom(ulong seed) => _state = seed;

    public GameRandom(int seed) : this(unchecked((ulong)(uint)seed) * 0x9E3779B97F4A7C15UL + 0x1234567UL) { }

    public ulong State
    {
        get => _state;
        set => _state = value;
    }

    public ulong NextULong()
    {
        ulong z = unchecked(_state += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>Returns a value in [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        }

        return (int)(NextULong() % (ulong)maxExclusive);
    }

    /// <summary>Returns a value in [minInclusive, maxExclusive).</summary>
    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => NextDouble() < probability;

    /// <summary>Picks an index according to the given relative weights.</summary>
    public int Weighted(IReadOnlyList<int> weights)
    {
        int total = 0;
        foreach (int w in weights)
        {
            total += w;
        }

        int roll = Next(total);
        for (int i = 0; i < weights.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0)
            {
                return i;
            }
        }

        return weights.Count - 1;
    }

    /// <summary>
    /// Creates an independent generator derived from a seed and a stable name, so that each generation stage
    /// (hills, rivers, trees, ...) has its own stream and adding a new stage does not reshuffle the others.
    /// </summary>
    public static GameRandom ForStage(int seed, string stage)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in stage)
        {
            hash = unchecked((hash ^ c) * 1099511628211UL);
        }

        return new GameRandom(unchecked(hash ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL)));
    }
}
