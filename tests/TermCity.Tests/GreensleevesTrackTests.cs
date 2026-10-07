using System.Buffers.Binary;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class GreensleevesTrackTests
{
    [Fact]
    public void OpeningIsFixedWhileLaterPhrasesModulateAndVaryReproducibly()
    {
        var sequence = new GreensleevesSequence(42);
        var replay = new GreensleevesSequence(42);
        var other = new GreensleevesSequence(7);
        var opening = sequence.Next();
        Assert.Equal(GreensleevesTrack.Render(), opening.Pcm);
        Assert.Equal(opening.Pcm, replay.Next().Pcm);
        Assert.Equal(opening.Pcm, other.Next().Pcm);
        int previousKey = 0, previousVariation = 0;
        var keys = new HashSet<int>();
        var variations = new HashSet<int>();
        for (int phrase = 0; phrase < 8; phrase++)
        {
            var next = sequence.Next();
            var repeated = replay.Next();
            Assert.Equal(next.Pcm, repeated.Pcm);
            Assert.Contains(next.Transpose - previousKey, new[] { 5, -5, 7, -7 });
            Assert.InRange(next.Transpose, -12, 12);
            Assert.NotEqual(previousVariation, next.Variation);
            Assert.InRange(next.Pcm.Length / (2.0 * GreensleevesTrack.SampleRate), 40, 55);
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(next.Pcm.AsSpan(0, 2)));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(next.Pcm.AsSpan(next.Pcm.Length - 2, 2)));
            int peak = 0;
            for (int i = 0; i < next.Pcm.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(next.Pcm.AsSpan(i, 2))));
            Assert.InRange(peak, 8_000, 20_000);
            keys.Add(next.Transpose);
            variations.Add(next.Variation);
            previousKey = next.Transpose;
            previousVariation = next.Variation;
        }
        Assert.True(keys.Count >= 3);
        Assert.True(variations.Count >= 3);
    }

    [Fact]
    public void TrackIsSlowAudibleBoundedDeterministicAndLoopsWithoutAClick()
    {
        byte[] pcm = GreensleevesTrack.Render();
        Assert.Equal(pcm, GreensleevesTrack.Render());
        double seconds = pcm.Length / (2.0 * GreensleevesTrack.SampleRate);
        Assert.InRange(seconds, 94.44, 94.45);
        Assert.Equal(54, GreensleevesTrack.BeatsPerMinute);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(pcm.Length - 2, 2)));
        int peak = 0;
        double energy = 0;
        for (int offset = 0; offset < pcm.Length; offset += 2)
        {
            int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset, 2));
            peak = Math.Max(peak, Math.Abs(sample));
            energy += (double)sample * sample;
        }
        Assert.InRange(peak, 8_000, 20_000);
        Assert.InRange(Math.Sqrt(energy / (pcm.Length / 2)), 1_000, 10_000);
        Assert.True(pcm.Length < 8_000_000, "A single cached track must stay below 8 MB.");
    }
}
