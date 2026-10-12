namespace TermCity.GodotApp;

/// <summary>An original FM/chiptune arrangement of the traditional public-domain melody.</summary>
public static class GreensleevesTrack
{
    public const int SampleRate = MedievalMusicSynth.SampleRate;
    public const int BeatsPerMinute = 54;

    // Durations are eighth-note units in 3/4 time; MIDI pitches keep the score independent of the synth.
    internal static readonly (int Pitch, int Units)[] Melody =
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
    internal static readonly int[] BassRoots = [45, 43, 48, 40, 45, 40, 45, 40, 48, 43, 45, 40];
    private static readonly Lazy<MusicSong> Song = new(() => MusicRegistry.CreateDefault().Get("greensleeves"));

    public static byte[] Render() => MedievalMusicSynth.Render(Song.Value);

    internal static byte[] RenderVariation(int transpose, int variation, bool refrain)
    {
        int refrainStart = Array.FindIndex(Melody, note => note.Pitch == 79);
        var notes = refrain ? Melody[refrainStart..] : Melody[1..refrainStart];
        return MedievalMusicSynth.Render(Song.Value, transpose, variation,
            notes.Select(n => new MusicNote(n.Pitch, n.Units)).ToArray());
    }
}

public sealed record MusicPhrase(byte[] Pcm, int Transpose, int Variation, bool Refrain)
{
    public string SongId { get; init; } = "greensleeves";
    public string Title { get; init; } = "Greensleeves";
}

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
