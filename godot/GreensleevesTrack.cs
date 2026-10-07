using System.Buffers.Binary;

namespace TermCity.GodotApp;

/// <summary>An original FM/chiptune arrangement of the traditional public-domain melody.</summary>
public static class GreensleevesTrack
{
    public const int SampleRate = 22_050;
    public const int BeatsPerMinute = 54;

    // Durations are eighth-note units in 3/4 time; MIDI pitches keep the score independent of the synth.
    private static readonly (int Pitch, int Units)[] Melody =
    [
        (64, 2),
        (69, 3), (71, 1), (72, 2), (74, 3), (76, 1), (74, 2),
        (72, 3), (71, 1), (67, 2), (64, 3), (66, 1), (68, 2),
        (69, 3), (69, 1), (68, 2), (69, 3), (71, 1), (68, 2), (64, 6),
        (69, 3), (71, 1), (72, 2), (74, 3), (76, 1), (74, 2),
        (72, 3), (71, 1), (67, 2), (64, 3), (66, 1), (68, 2),
        (69, 3), (68, 1), (66, 2), (68, 3), (69, 3), (69, 6),
        (79, 3), (79, 1), (78, 2), (76, 3), (74, 1), (72, 2),
        (71, 3), (67, 1), (64, 2), (66, 3), (68, 1), (69, 2),
        (76, 3), (76, 1), (74, 2), (72, 3), (71, 1), (68, 2), (64, 6),
        (79, 3), (79, 1), (78, 2), (76, 3), (74, 1), (72, 2),
        (71, 3), (67, 1), (64, 2), (66, 3), (68, 1), (69, 2),
        (69, 3), (68, 1), (66, 2), (68, 3), (69, 3), (69, 6),
    ];
    private static readonly int[] BassRoots = [45, 43, 48, 40, 45, 40, 45, 40, 48, 43, 45, 40];
    private static readonly int[] Arpeggio = [0, 7, 12, 7, 3, 7];

    public static byte[] Render() => RenderPhrase(Melody, 0, 0);

    internal static byte[] RenderVariation(int transpose, int variation, bool refrain)
    {
        int refrainStart = Array.FindIndex(Melody, note => note.Pitch == 79);
        var notes = refrain ? Melody[refrainStart..] : Melody[1..refrainStart];
        return RenderPhrase(notes, transpose, variation);
    }

    private static byte[] RenderPhrase((int Pitch, int Units)[] notes, int transpose, int variation)
    {
        double eighth = 30.0 / BeatsPerMinute;
        int units = notes.Sum(note => note.Units);
        int frames = (int)Math.Round(units * eighth * SampleRate);
        var pcm = new byte[checked(frames * sizeof(short))];
        int start = 0, elapsedUnits = 0;
        foreach (var (pitch, duration) in notes)
        {
            int end = (int)Math.Round((elapsedUnits + duration) * eighth * SampleRate);
            double frequency = Frequency(pitch + transpose);
            for (int frame = start; frame < end; frame++)
            {
                double time = frame / (double)SampleRate;
                double local = (frame - start) / (double)SampleRate;
                double remaining = (end - 1 - frame) / (double)SampleRate;
                double envelope = Math.Min(1, local / 0.02) * Math.Min(1, remaining / 0.10);
                double phase = Math.Tau * frequency * local;
                double timbre = 0.65 + (variation % 4) * 0.08;
                double lead = Math.Sin(phase + timbre * Math.Sin(phase * 2)) * envelope * 0.34;
                int pulse = (int)(time / eighth);
                int root = BassRoots[(pulse / 6) % BassRoots.Length];
                int arpIndex = variation % 2 == 0 ? pulse % Arpeggio.Length : 5 - pulse % Arpeggio.Length;
                int interval = Arpeggio[arpIndex];
                if (interval == 3 && root != 45) interval = 4;
                double pluckTime = time % eighth;
                double pluckEnvelope = Math.Min(1, pluckTime / 0.015) *
                    Math.Min(1, (eighth - pluckTime) / 0.06) * Math.Exp(-pluckTime * 3);
                double accompaniment = Math.Sin(Math.Tau * Frequency(root + transpose + 12 + interval) * pluckTime) *
                    pluckEnvelope * (0.11 + (variation % 3) * 0.005);
                double barTime = time % (eighth * 6);
                double bassEnvelope = Math.Min(1, barTime / 0.02) *
                    Math.Min(1, (eighth * 6 - barTime) / 0.08);
                double bass = Math.Sin(Math.Tau * Frequency(root + transpose) * time) * bassEnvelope * 0.055;
                double fade = Math.Min(1, time / 0.15) *
                    Math.Min(1, (frames - 1 - frame) / (SampleRate * 0.25));
                short sample = (short)Math.Round((lead + accompaniment + bass) * fade * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * sizeof(short), sizeof(short)), sample);
            }

            start = end;
            elapsedUnits += duration;
        }
        return pcm;
    }

    private static double Frequency(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
}

public sealed record MusicPhrase(byte[] Pcm, int Transpose, int Variation, bool Refrain);

/// <summary>Changes key only between complete phrases; all voices transpose together.</summary>
public sealed class GreensleevesSequence
{
    private readonly Random _random;
    private bool _opening = true;
    private int _transpose;
    private int _variation;

    public GreensleevesSequence(int? seed = null) => _random = seed is { } value ? new Random(value) : new Random();

    public MusicPhrase Next()
    {
        if (_opening)
        {
            _opening = false;
            return new MusicPhrase(GreensleevesTrack.Render(), 0, 0, false);
        }
        int[] steps = [7, -5, 5, -7];
        var allowed = steps.Where(step => _transpose + step is >= -12 and <= 12).ToArray();
        _transpose += allowed[_random.Next(allowed.Length)];
        _variation = (_variation + _random.Next(1, 12)) % 12;
        bool refrain = _random.Next(2) == 0;
        return new MusicPhrase(GreensleevesTrack.RenderVariation(_transpose, _variation, refrain),
            _transpose, _variation, refrain);
    }
}
