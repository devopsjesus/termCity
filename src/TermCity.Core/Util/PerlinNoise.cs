namespace TermCity.Core.Util;

/// <summary>Seeded 2D gradient noise used for terrain and feature clustering.</summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];

    public PerlinNoise(GameRandom rng)
    {
        int[] p = new int[256];
        for (int i = 0; i < 256; i++)
        {
            p[i] = i;
        }

        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        for (int i = 0; i < 512; i++)
        {
            _perm[i] = p[i & 255];
        }
    }

    /// <summary>Single octave noise, roughly in [-1, 1].</summary>
    public double Noise(double x, double y)
    {
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;
        double xf = x - Math.Floor(x);
        double yf = y - Math.Floor(y);
        double u = Fade(xf);
        double v = Fade(yf);

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];

        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v) * 1.4142;
    }

    /// <summary>Multi-octave noise, normalised to roughly [0, 1].</summary>
    public double Fractal(double x, double y, int octaves = 3, double persistence = 0.5)
    {
        double sum = 0, amp = 1, freq = 1, max = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Noise(x * freq, y * freq) * amp;
            max += amp;
            amp *= persistence;
            freq *= 2;
        }

        return Math.Clamp((sum / max + 1) / 2, 0, 1);
    }

    /// <summary>Returns the value above which roughly <paramref name="coverage"/> of the samples lie.</summary>
    public static double ThresholdForCoverage(double[] values, double coverage)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = Math.Clamp((int)((1 - coverage) * sorted.Length), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double a, double b, double t) => a + t * (b - a);

    private static double Grad(int hash, double x, double y) => (hash & 3) switch
    {
        0 => x + y,
        1 => -x + y,
        2 => x - y,
        _ => -x - y,
    };
}
