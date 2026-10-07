using System.Buffers.Binary;

namespace TermCity.GodotApp;

/// <summary>A synthesised mechanical-keyboard "click-clack": a bright switch click, then a lower bottoming-out clack.</summary>
public static class PlacementClick
{
    public const int SampleRate = 22_050;
    public const int Variants = 4;

    public static byte[] Render(int variant)
    {
        var random = new Random(7919 * (variant + 1));
        double gap = 0.045 + 0.006 * variant;
        int frames = (int)(SampleRate * (gap + 0.07));
        var samples = new double[frames];
        AddBurst(samples, random, 0, 0.012, 2600 + 220 * variant, 0.55);
        AddBurst(samples, random, (int)(gap * SampleRate), 0.034, 900 + 90 * variant, 0.95);

        var pcm = new byte[frames * sizeof(short)];
        for (int i = 0; i < frames; i++)
        {
            short value = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * short.MaxValue * 0.8);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short)), value);
        }

        return pcm;
    }

    // A noise transient plus a decaying resonance: the noise gives the snap, the tone gives the plastic body.
    private static void AddBurst(double[] samples, Random random, int start, double seconds, double hertz, double level)
    {
        int length = (int)(seconds * SampleRate);
        double lowpass = 0;
        for (int i = 0; i < length && start + i < samples.Length; i++)
        {
            double t = (double)i / SampleRate;
            double envelope = Math.Exp(-t / (seconds / 4.5));
            lowpass += 0.45 * (random.NextDouble() * 2 - 1 - lowpass);
            double tone = Math.Sin(2 * Math.PI * hertz * t);
            samples[start + i] += level * envelope * (0.55 * lowpass + 0.45 * tone);
        }
    }
}
