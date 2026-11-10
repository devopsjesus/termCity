using System.Buffers.Binary;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class BattleHornTests
{
    [Fact]
    public void HornIsDeterministicAudiblePcmWithoutClippingOrAbruptEnds()
    {
        var pcm = BattleHorn.Render();
        Assert.Equal((int)(BattleHorn.SampleRate * BattleHorn.DurationSeconds) * sizeof(short), pcm.Length);
        Assert.Equal(pcm, BattleHorn.Render());
        var samples = Enumerable.Range(0, pcm.Length / sizeof(short))
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * sizeof(short)))).ToArray();
        double rms = Math.Sqrt(samples.Average(sample => Math.Pow(sample / (double)short.MaxValue, 2)));
        Assert.InRange(rms, 0.1, 0.5);
        Assert.All(samples, sample => Assert.InRange((int)sample, -24_000, 24_000));
        Assert.Equal(0, samples[0]);
        Assert.InRange(Math.Abs(samples[^1]), 0, 1);
        Assert.True(samples.Distinct().Count() > 1_000);
    }
}
