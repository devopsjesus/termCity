using System.Buffers.Binary;

namespace TermCity.GodotApp;

public enum MusicVoice { Strings, Wind, SoftBrass }
public readonly record struct MusicNote(int Pitch, int Units);
public readonly record struct MusicChord(int Root, bool Minor = true);

/// <summary>A score definition independent of the synthesizer, playlist and Godot playback resources.</summary>
public sealed class MusicSong
{
    public MusicSong(string id, string title, int beatsPerMinute, int barUnits,
        IEnumerable<MusicNote> melody, IEnumerable<MusicChord> chords, MusicVoice voice = MusicVoice.Wind)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Songs require an identifier and title.");
        if (beatsPerMinute is < 40 or > 80 || barUnits is < 4 or > 12)
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute), "Background scores require a gentle tempo and valid meter.");
        var notes = melody.ToArray();
        var harmony = chords.ToArray();
        if (notes.Length == 0 || notes.Any(n => n.Units <= 0 || n.Pitch is < 0 or > 108) ||
            notes.Sum(n => n.Units) > 360 || harmony.Length == 0 || harmony.Any(c => c.Root is < 24 or > 60) ||
            !Enum.IsDefined(voice))
            throw new ArgumentException("Invalid melody, chord sequence or instrument.");
        Id = id; Title = title; BeatsPerMinute = beatsPerMinute; BarUnits = barUnits; Voice = voice;
        Melody = Array.AsReadOnly(notes); Chords = Array.AsReadOnly(harmony);
    }

    public string Id { get; }
    public string Title { get; }
    public int BeatsPerMinute { get; }
    public int BarUnits { get; }
    public MusicVoice Voice { get; }
    public IReadOnlyList<MusicNote> Melody { get; }
    public IReadOnlyList<MusicChord> Chords { get; }
}

public sealed class MusicRegistry
{
    private readonly List<MusicSong> _songs = [];
    private readonly Dictionary<string, MusicSong> _byId = new(StringComparer.Ordinal);
    public IReadOnlyList<MusicSong> Songs => _songs.AsReadOnly();

    public void Register(MusicSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!_byId.TryAdd(song.Id, song)) throw new ArgumentException($"Song '{song.Id}' is already registered.", nameof(song));
        _songs.Add(song);
    }

    public MusicSong Get(string id) => _byId.TryGetValue(id, out var song) ? song :
        throw new ArgumentException($"No song registered as '{id}'.", nameof(id));

    public static MusicRegistry CreateDefault()
    {
        var registry = new MusicRegistry();
        registry.Register(new MusicSong("greensleeves", "Greensleeves", GreensleevesTrack.BeatsPerMinute, 6,
            GreensleevesTrack.Melody.Select(n => new MusicNote(n.Pitch, n.Units)),
            GreensleevesTrack.BassRoots.Select(root => new MusicChord(root, root is 45 or 40)), MusicVoice.Wind));
        RegisterOriginals(registry);
        return registry;
    }

    private static void RegisterOriginals(MusicRegistry registry)
    {
        // Original modal themes. Eight-note phrases are answered by a softer companion phrase.
        Add("willow-court", "Willow Court", 52, 6, 62, [0, 2, 3, 5, 7, 9, 10],
            [0, 2, 4, 3, 2, 1, 2, 0, 4, 5, 4, 2, 3, 2, 1, 0],
            [0, -2, 3, -2, 0, 5, -2, 0], MusicVoice.Strings);
        Add("cloister-light", "Cloister Light", 48, 8, 60, [0, 2, 4, 5, 7, 9, 11],
            [0, 2, 1, 3, 2, 4, 3, 2, 5, 4, 3, 2, 1, 3, 2, 0],
            [0, 5, -3, -5, 0, 5, -5, 0], MusicVoice.Wind);
        Add("evening-reeve", "The Evening Reeve", 56, 6, 67, [0, 2, 4, 5, 7, 9, 10],
            [4, 3, 2, 1, 0, 1, 2, 0, 2, 4, 5, 4, 3, 2, 1, 0],
            [0, -2, 5, 0, -2, 5, -2, 0], MusicVoice.SoftBrass);
        Add("river-lanterns", "River Lanterns", 50, 8, 65, [0, 2, 3, 5, 7, 8, 10],
            [0, 1, 3, 2, 4, 3, 2, 1, 0, 2, 4, 5, 4, 3, 1, 0],
            [0, -2, 3, -5, 0, 3, -2, 0], MusicVoice.Wind);
        Add("orchard-vigil", "Orchard Vigil", 54, 6, 69, [0, 2, 3, 5, 7, 8, 10],
            [2, 1, 0, 2, 3, 2, 4, 3, 5, 4, 2, 3, 2, 1, 2, 0],
            [0, -5, -2, 3, 0, -5, -2, 0], MusicVoice.Strings);

        void Add(string id, string title, int bpm, int meter, int tonic, int[] scale, int[] theme,
            int[] roots, MusicVoice voice)
        {
            var notes = new List<MusicNote>();
            for (int phrase = 0; phrase < theme.Length / 2; phrase++)
            {
                int a = theme[phrase * 2], b = theme[phrase * 2 + 1];
                int answer = Math.Max(0, b - 1);
                notes.Add(new(tonic + scale[a], meter == 6 ? 2 : 3));
                notes.Add(new(tonic + scale[b], meter == 6 ? 1 : 1));
                notes.Add(new(tonic + scale[answer], meter == 6 ? 1 : 2));
                notes.Add(new(tonic + scale[b], 2));
            }
            var refrain = notes.Select(n => n with { Pitch = n.Pitch + 12 }).ToArray();
            notes.AddRange(refrain);
            bool minorMode = id is "willow-court" or "river-lanterns" or "orchard-vigil";
            var chords = roots.Select(root => new MusicChord(tonic - 24 + root,
                minorMode ? root is 0 or -5 or 5 : root == -3));
            registry.Register(new MusicSong(id, title, bpm, meter, notes, chords, voice));
        }
    }
}

/// <summary>Shared calm PCM16 arrangement: alternating lead/strings, modal plucks and a quiet running bass.</summary>
public static class MedievalMusicSynth
{
    public const int SampleRate = 22_050;
    public static byte[] Render(MusicSong song, int transpose = 0, int variation = 0,
        IReadOnlyList<MusicNote>? phrase = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (transpose is < -12 or > 12 || variation is < 0 or > 11)
            throw new ArgumentOutOfRangeException(nameof(transpose));
        var notes = phrase ?? song.Melody;
        if (notes.Count == 0 || notes.Any(n => n.Units <= 0 || n.Pitch is < 0 or > 108))
            throw new ArgumentException("A rendered phrase requires valid notes.", nameof(phrase));
        double eighth = 30d / song.BeatsPerMinute;
        int totalUnits = notes.Sum(n => n.Units);
        if (totalUnits > 360) throw new ArgumentException("Music phrases must remain within the background synthesis memory budget.", nameof(phrase));
        int frames = (int)Math.Round(totalUnits * eighth * SampleRate);
        var pcm = new byte[checked(frames * sizeof(short))];
        var harmony = song.Chords.Select(chord => (
            Root: Frequency(chord.Root + transpose),
            Fifth: Frequency(chord.Root + transpose + 7),
            Octave: Frequency(chord.Root + transpose + 12),
            Third: Frequency(chord.Root + transpose + 12 + (chord.Minor ? 3 : 4)),
            HighFifth: Frequency(chord.Root + transpose + 19))).ToArray();
        int start = 0, elapsedUnits = 0;
        foreach (var note in notes)
        {
            int end = (int)Math.Round((elapsedUnits + note.Units) * eighth * SampleRate);
            double frequency = note.Pitch == 0 ? 0 : Frequency(note.Pitch + transpose);
            for (int frame = start; frame < end; frame++)
            {
                double time = frame / (double)SampleRate;
                double local = (frame - start) / (double)SampleRate;
                double remaining = (end - 1 - frame) / (double)SampleRate;
                int pulse = (int)(time / eighth), bar = pulse / song.BarUnits;
                var chord = harmony[bar % harmony.Length];
                var voice = (bar / 4 + variation) % 2 == 0 ? song.Voice :
                    song.Voice == MusicVoice.Strings ? MusicVoice.Wind : MusicVoice.Strings;
                double lead = frequency == 0 ? 0 : Oscillator(voice, frequency, local) *
                    Envelope(local, remaining, voice == MusicVoice.SoftBrass ? 0.12 : 0.06, 0.16) * 0.28;
                double barTime = time % (eighth * song.BarUnits);
                double barRemaining = eighth * song.BarUnits - barTime;
                double sustain = Envelope(barTime, barRemaining, 0.16, 0.22);
                double chordVoice = (Oscillator(MusicVoice.Strings, chord.Octave, barTime) +
                    Oscillator(MusicVoice.Strings, chord.Third, barTime) +
                    Oscillator(MusicVoice.Wind, chord.HighFifth, barTime)) / 3 * sustain * 0.105;
                double pluckTime = time % eighth;
                double pluckFrequency = ((pulse + variation) % 6) switch
                {
                    0 or 3 => chord.Octave, 1 or 4 => chord.Third, _ => chord.HighFifth,
                };
                double pluck = Math.Sin(Math.Tau * pluckFrequency * pluckTime) *
                    Envelope(pluckTime, eighth - pluckTime, 0.025, 0.09) * Math.Exp(-pluckTime * 3) * 0.075;
                double bassPeriod = eighth * 2, bassTime = time % bassPeriod;
                double bassFrequency = (pulse / 2 % 4) switch { 0 or 2 => chord.Root, 1 => chord.Fifth, _ => chord.Octave };
                double bass = Oscillator(MusicVoice.Strings, bassFrequency, bassTime) *
                    Envelope(bassTime, bassPeriod - bassTime, 0.08, 0.15) * 0.055;
                double fade = Envelope(time, (frames - 1 - frame) / (double)SampleRate, 0.25, 0.4);
                short sample = (short)Math.Round((lead + chordVoice + pluck + bass) * fade * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * sizeof(short), sizeof(short)), sample);
            }
            start = end; elapsedUnits += note.Units;
        }
        return pcm;
    }

    private static double Frequency(int midi) => 440 * Math.Pow(2, (midi - 69) / 12d);
    private static double Envelope(double elapsed, double remaining, double attack, double release) =>
        Math.Clamp(elapsed / attack, 0, 1) * Math.Clamp(remaining / release, 0, 1);
    private static double Oscillator(MusicVoice voice, double frequency, double time)
    {
        double phase = Math.Tau * frequency * time;
        return voice switch
        {
            MusicVoice.Strings => (Math.Sin(phase) + 0.2 * Math.Sin(phase * 2) + 0.07 * Math.Sin(phase * 4)) / 1.27,
            MusicVoice.Wind => Math.Sin(phase + 0.22 * Math.Sin(phase * 2)),
            MusicVoice.SoftBrass => Math.Sin(phase + 0.38 * Math.Sin(phase)) * 0.9,
            _ => throw new ArgumentOutOfRangeException(nameof(voice)),
        };
    }
}

/// <summary>Shuffled complete songs, avoiding adjacent repeats, with shared phrase synthesis and deterministic seeds.</summary>
public sealed class MusicSequence
{
    private readonly MusicSong[] _songs;
    private readonly Random _random;
    private int[] _order = [];
    private int _index;
    private int _last = -1;
    public MusicSequence(MusicRegistry registry, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _songs = registry.Songs.ToArray();
        if (_songs.Length == 0) throw new ArgumentException("Music playback requires at least one registered song.", nameof(registry));
        _random = seed is { } value ? new Random(value) : new Random();
    }

    public MusicPhrase Next()
    {
        if (_index >= _order.Length)
        {
            _order = Enumerable.Range(0, _songs.Length).ToArray();
            _random.Shuffle(_order);
            if (_order.Length > 1 && _order[0] == _last) (_order[0], _order[1]) = (_order[1], _order[0]);
            _index = 0;
        }
        _last = _order[_index++];
        var song = _songs[_last];
        int variation = _random.Next(12);
        return new MusicPhrase(MedievalMusicSynth.Render(song, variation: variation), 0, variation, false)
        { SongId = song.Id, Title = song.Title };
    }
}
