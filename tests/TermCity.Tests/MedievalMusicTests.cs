using System.Buffers.Binary;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class MedievalMusicTests
{
    [Fact]
    public void CatalogContainsDistinctGentleScoresAndSupportsNewSongsWithoutPlaybackChanges()
    {
        var registry = MusicRegistry.CreateDefault();
        Assert.Equal(6, registry.Songs.Count);
        Assert.Equal(6, registry.Songs.Select(song => song.Id).Distinct().Count());
        Assert.All(registry.Songs, song => Assert.InRange(song.BeatsPerMinute, 48, 56));
        Assert.Contains(registry.Songs, song => song.Voice == MusicVoice.Strings);
        Assert.Contains(registry.Songs, song => song.Voice == MusicVoice.Wind);
        Assert.Contains(registry.Songs, song => song.Voice == MusicVoice.SoftBrass);
        var song = new MusicSong("new-vigil", "A New Vigil", 52, 6,
            [new MusicNote(62, 2), new MusicNote(65, 2), new MusicNote(62, 2)], [new MusicChord(38)]);
        registry.Register(song);
        Assert.Same(song, registry.Get("new-vigil"));
        var sequence = new MusicSequence(registry, 13);
        var visited = new HashSet<string>();
        for (int i = 0; i < registry.Songs.Count; i++) visited.Add(sequence.Next().SongId);
        Assert.Contains("new-vigil", visited);
        Assert.Throws<ArgumentException>(() => registry.Register(song));
        Assert.Throws<ArgumentException>(() => registry.Get("missing"));
    }

    [Fact]
    public void EverySongRendersCalmBoundedPcmWithSilentSeamsAndDistinctAudio()
    {
        var signatures = new HashSet<int>();
        foreach (var song in MusicRegistry.CreateDefault().Songs)
        {
            var pcm = MedievalMusicSynth.Render(song);
            Assert.Equal(0, pcm.Length % 2);
            double seconds = pcm.Length / (2d * MedievalMusicSynth.SampleRate);
            Assert.InRange(seconds, 40, 100);
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2)));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(pcm.Length - 2, 2)));
            double energy = 0; int peak = 0, worstJump = 0, previous = 0;
            var hash = new HashCode();
            for (int i = 0; i < pcm.Length; i += 2)
            {
                int sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2));
                peak = Math.Max(peak, Math.Abs(sample));
                energy += (double)sample * sample;
                worstJump = Math.Max(worstJump, Math.Abs(sample - previous));
                previous = sample;
                hash.Add(sample);
            }
            Assert.InRange(peak, 8_000, 20_000);
            Assert.InRange(Math.Sqrt(energy / (pcm.Length / 2)), 1_000, 10_000);
            Assert.True(worstJump < 8_000, $"{song.Title}: harsh sample transition {worstJump}.");
            Assert.True(pcm.Length < 8_000_000);
            signatures.Add(hash.ToHashCode());
        }
        Assert.Equal(6, signatures.Count);
    }

    [Fact]
    public void PlaylistVisitsEveryRegisteredSongAndAvoidsAdjacentRepeatsAcrossShuffleCycles()
    {
        var registry = MusicRegistry.CreateDefault();
        var sequence = new MusicSequence(registry, 42);
        var replay = new MusicSequence(registry, 42);
        var visited = new HashSet<string>();
        string? last = null;
        for (int n = 0; n < registry.Songs.Count + 2; n++)
        {
            var phrase = sequence.Next();
            var repeated = replay.Next();
            Assert.Equal(phrase.SongId, repeated.SongId);
            Assert.Equal(phrase.Pcm, repeated.Pcm);
            Assert.NotEqual(last, phrase.SongId);
            Assert.Equal(registry.Get(phrase.SongId).Title, phrase.Title);
            if (n < registry.Songs.Count) visited.Add(phrase.SongId);
            last = phrase.SongId;
        }
        Assert.Equal(registry.Songs.Count, visited.Count);
    }

    [Fact]
    public void SingleSongPlaylistsAreSupportedAndScoresAreImmutableValidatedSnapshots()
    {
        var notes = new[] { new MusicNote(62, 6) };
        var song = new MusicSong("vigil", "Vigil", 50, 6, notes, [new MusicChord(38)]);
        notes[0] = new(99, 1);
        Assert.Equal(62, song.Melody[0].Pitch);
        var registry = new MusicRegistry();
        Assert.Throws<ArgumentException>(() => new MusicSequence(registry));
        registry.Register(song);
        var sequence = new MusicSequence(registry, 2);
        Assert.Equal("vigil", sequence.Next().SongId);
        Assert.Equal("vigil", sequence.Next().SongId);
        Assert.Throws<ArgumentException>(() => new MusicSong("", "Invalid", 50, 6, notes, [new MusicChord(38)]));
        Assert.Throws<ArgumentException>(() => new MusicSong("invalid", "Invalid", 50, 6, [new MusicNote(62, 0)], [new MusicChord(38)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MedievalMusicSynth.Render(song, transpose: 13));
    }
}
