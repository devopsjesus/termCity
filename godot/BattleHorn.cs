using System.Buffers.Binary;

namespace TermCity.GodotApp;

public static class BattleHorn
{
    public const int SampleRate = 22_050;
    public const double DurationSeconds = 1.7;

    public static byte[] Render()
    {
        int frames = (int)(SampleRate * DurationSeconds);
        var pcm = new byte[frames * sizeof(short)];
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double time = i / (double)SampleRate;
            double attack = Math.Min(1, time / 0.08);
            double release = Math.Min(1, (DurationSeconds - time) / 0.35);
            double envelope = attack * attack * release * release;
            double pitch = 110 + 9 * (1 - Math.Exp(-time * 9)) + Math.Sin(time * Math.Tau * 4.5) * 0.45;
            phase += Math.Tau * pitch / SampleRate;
            double brass = Math.Sin(phase) + 0.42 * Math.Sin(phase * 2) +
                0.24 * Math.Sin(phase * 3) + 0.11 * Math.Sin(phase * 4);
            double breath = 0.03 * Math.Sin(time * Math.Tau * 617) * Math.Sin(phase * 0.51);
            double sample = Math.Tanh(brass + breath) * envelope * 0.7;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short)),
                (short)Math.Round(sample * short.MaxValue));
        }
        return pcm;
    }
}
