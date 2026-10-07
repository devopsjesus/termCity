namespace TermCity.Core.Effects;

/// <summary>Small pure easing helpers. Inputs are clamped to 0..1 where it matters; outputs are plain doubles.</summary>
public static class Easing
{
    public static double Clamp01(double t) => double.IsNaN(t) ? 0 : t < 0 ? 0 : t > 1 ? 1 : t;

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Maps <paramref name="value"/> from [from, to] onto 0..1, clamped.</summary>
    public static double Remap(double value, double from, double to) =>
        to == from ? (value >= to ? 1 : 0) : Clamp01((value - from) / (to - from));

    public static double EaseIn(double t) => Clamp01(t) * Clamp01(t);

    public static double EaseInCubic(double t)
    {
        t = Clamp01(t);
        return t * t * t;
    }

    public static double EaseOut(double t)
    {
        t = 1 - Clamp01(t);
        return 1 - t * t;
    }

    public static double EaseOutCubic(double t)
    {
        t = 1 - Clamp01(t);
        return 1 - t * t * t;
    }

    public static double SmoothStep(double t)
    {
        t = Clamp01(t);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Overshoots 1 slightly before settling: good for things that pop into place.</summary>
    public static double EaseOutBack(double t)
    {
        t = Clamp01(t);
        const double c1 = 1.70158, c3 = c1 + 1;
        double u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>0 at both ends of 0..1 and 1 in the middle (a triangle wave).</summary>
    public static double Bump(double t)
    {
        t = Clamp01(t);
        return 1 - Math.Abs(2 * t - 1);
    }

    /// <summary>Fades in over <paramref name="inEnd"/> and out from <paramref name="outStart"/> (both as fractions of 0..1).</summary>
    public static double Envelope(double t, double inEnd, double outStart) =>
        Remap(t, 0, inEnd) * (1 - Remap(t, outStart, 1));
}

/// <summary>
/// Stateless hash-based randomness. Effects are pure functions of their age, so any "random" variation they need is
/// derived from a seed and a couple of integer keys rather than from a stateful generator.
/// </summary>
public static class EffectRandom
{
    public static uint Mix(int seed, int a, int b = 0, int c = 0)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h = (h ^ (uint)a * 0x85EBCA6Bu) * 0xC2B2AE35u;
            h ^= h >> 15;
            h = (h ^ (uint)b * 0x27D4EB2Fu) * 0x165667B1u;
            h ^= h >> 13;
            h = (h ^ (uint)c * 0x9E3779B1u) * 0x85EBCA6Bu;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>A value in [0, 1).</summary>
    public static double Unit(int seed, int a, int b = 0, int c = 0) => Mix(seed, a, b, c) / 4294967296.0;

    /// <summary>A value in [-1, 1).</summary>
    public static double Signed(int seed, int a, int b = 0, int c = 0) => Unit(seed, a, b, c) * 2 - 1;

    public static int Pick(int seed, int count, int a, int b = 0, int c = 0) =>
        count <= 1 ? 0 : (int)(Mix(seed, a, b, c) % (uint)count);
}
