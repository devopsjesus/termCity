using Godot;
using System.Buffers.Binary;

namespace TermCity.GodotApp;

public partial class Main
{
    public bool MusicEnabled { get; private set; } = true;

    public bool SoundEnabled { get; private set; } = true;

    public const double DefaultMusicVolume = 70, DefaultSoundVolume = 60;

    public double MusicVolume { get; private set; } = DefaultMusicVolume;

    public double SoundVolume { get; private set; } = DefaultSoundVolume;

    private const string AudioDialogTitle = "Audio controls";

    private AudioStreamPlayer _music = null!;

    private AudioStreamGenerator? _musicTrack;

    private AudioStreamGeneratorPlayback _musicPlayback = null!;

    private readonly Vector2[] _musicFrames = new Vector2[1024];

    private GreensleevesSequence _musicSequence = null!;

    private MusicPhrase _musicPhrase = null!;

    private Task<MusicPhrase>? _nextMusicPhrase;

    private int _musicSample;

    private int _musicFrameCount;

    private bool _musicStopped;

    private readonly CancellationTokenSource _musicCancellation = new();

    private Task? _musicPump;

    private int _musicPhraseCount;

    public int MusicPhraseCount => Volatile.Read(ref _musicPhraseCount);

    internal int MusicSkips => _musicPlayback.GetSkips();

    private AudioStreamWav[] _clicks = [];

    private AudioStreamPlayer _clickPlayer = null!;

    private readonly Random _clickRandom = new();

    private static double LoadVolume(ConfigFile settings, string key, double defaultValue)
    {
        var value = settings.GetValue("audio", key, defaultValue);
        if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) ||
            !double.IsFinite(value.AsDouble()) || value.AsDouble() is < 0 or > 100)
            throw new InvalidOperationException($"Saved {key} must be between 0 and 100.");
        return value.AsDouble();
    }

    private void CreateMusic()
    {
        _musicSequence = new GreensleevesSequence(_options.SmokeTest ? 42 : null);
        _musicPhrase = _musicSequence.Next();
        _musicPhraseCount = 1;
        _nextMusicPhrase = Task.Run(() => _musicSequence.Next());
        _musicTrack = new AudioStreamGenerator
        {
            MixRate = GreensleevesTrack.SampleRate,
            BufferLength = 1f,
        };
        _music = new AudioStreamPlayer
        {
            Name = "CityMusic", Stream = _musicTrack,
            VolumeDb = VolumeDb(MusicVolume, -24),
        };
        AddChild(_music);
        _music.Play();
        _musicPlayback = (AudioStreamGeneratorPlayback)_music.GetStreamPlayback();
        PumpMusic();
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
        // Only the playback resource is accessed here, never nodes or the scene tree. Map rendering must not
        // be responsible for keeping the audio ring buffer full.
        _musicPump = Task.Run(async () =>
        {
            while (!_musicCancellation.IsCancellationRequested)
            {
                PumpMusic();
                await Task.Delay(10, _musicCancellation.Token);
            }
        });
    }

    private float VolumeDb(double volume, float nominal) =>
        _options.SmokeTest || volume == 0 ? -80 : nominal + (float)(20 * Math.Log10(volume / 100));

    private void CreateClicks()
    {
        _clicks = Enumerable.Range(0, PlacementClick.Variants).Select(i => new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = PlacementClick.SampleRate,
            Stereo = false,
            Data = PlacementClick.Render(i),
        }).ToArray();
        _clickPlayer = new AudioStreamPlayer { Name = "PlacementClick", VolumeDb = VolumeDb(SoundVolume, -12) };
        AddChild(_clickPlayer);
    }

    private void PlayPlacementClick()
    {
        if (!_focused || !SoundEnabled || SoundVolume == 0 || _clicks.Length == 0) return;
        _clickPlayer.Stream = _clicks[_clickRandom.Next(_clicks.Length)];
        _clickPlayer.PitchScale = (float)(0.92 + _clickRandom.NextDouble() * 0.16);
        _clickPlayer.Play();
    }

    private bool AdvanceMusicPhrase()
    {
        if (_nextMusicPhrase is null || !_nextMusicPhrase.IsCompleted) return false;
        if (_nextMusicPhrase.IsFaulted)
        {
            throw new InvalidOperationException("Music synthesis failed.", _nextMusicPhrase.Exception);
        }
        _musicPhrase = _nextMusicPhrase.GetAwaiter().GetResult();
        _musicSample = 0;
        Interlocked.Increment(ref _musicPhraseCount);
        _nextMusicPhrase = Task.Run(() => _musicSequence.Next());
        return true;
    }

    private void PumpMusic()
    {
        if (_musicStopped) return;
        while (_musicPlayback.GetFramesAvailable() >= _musicFrames.Length)
        {
            while (_musicFrameCount < _musicFrames.Length)
            {
                if (_musicSample >= _musicPhrase.Pcm.Length && !AdvanceMusicPhrase())
                {
                    return;
                }
                float sample = BinaryPrimitives.ReadInt16LittleEndian(
                    _musicPhrase.Pcm.AsSpan(_musicSample, sizeof(short))) / 32768f;
                _musicFrames[_musicFrameCount++] = new Vector2(sample, sample);
                _musicSample += sizeof(short);
            }
            if (!_musicPlayback.PushBuffer(_musicFrames))
            {
                throw new InvalidOperationException("Could not queue synthesized music frames.");
            }
            _musicFrameCount = 0;
        }
    }

    private void ToggleMusic()
    {
        MusicEnabled = !MusicEnabled;
        _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
        SavePreferences();
        RefreshMenuState(ToggleMusic, MusicEnabled ? "ON" : "OFF");
    }

    private void ToggleSound()
    {
        SoundEnabled = !SoundEnabled;
        if (!SoundEnabled) _clickPlayer.Stop();
        SavePreferences();
        RefreshMenuState(ToggleSound, SoundEnabled ? "ON" : "OFF");
    }

    private void ShowAudioDialog() => Session.ShowPrompt(AudioDialogTitle,
        "Music and placement sounds have independent volume and mute controls.",
        [new("Close", Session.CancelPrompt)]);

    private void AddAudioControls(Container content, bool music)
    {
        var channel = new VBoxContainer();
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = music ? "Music" : "Sound", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var slider = new HSlider
        {
            Name = music ? "MusicVolume" : "SoundVolume",
            MinValue = 0, MaxValue = 100, Step = 1,
            Value = music ? MusicVolume : SoundVolume,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(200, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        var value = new Label { Text = $"{slider.Value:0}%", CustomMinimumSize = new Vector2(56, 0) };
        string muteText = (music ? MusicEnabled : SoundEnabled) ? "Mute" : "Unmute";
        var mute = new MnemonicButton { Name = music ? "MusicMute" : "SoundMute",
            Text = muteText, UnderlineIndex = muteText.IndexOf(music ? 'm' : 'u', StringComparison.OrdinalIgnoreCase),
            TooltipText = music ? "Mute/unmute music (M)" : "Mute/unmute sound (U)" };
        slider.ValueChanged += volume =>
        {
            if (music) MusicVolume = volume; else SoundVolume = volume;
            value.Text = $"{volume:0}%";
            (music ? _music : _clickPlayer).VolumeDb = VolumeDb(volume, music ? -24 : -12);
            if (music) _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
            else if (volume == 0) _clickPlayer.Stop();
            SavePreferences();
        };
        mute.Pressed += () =>
        {
            if (music)
            {
                MusicEnabled = !MusicEnabled;
                _music.StreamPaused = !MusicEnabled || MusicVolume == 0 || !_focused;
            }
            else
            {
                SoundEnabled = !SoundEnabled;
                if (!SoundEnabled) _clickPlayer.Stop();
            }
            mute.Text = (music ? MusicEnabled : SoundEnabled) ? "Mute" : "Unmute";
            mute.UnderlineIndex = mute.Text.IndexOf(music ? 'm' : 'u', StringComparison.OrdinalIgnoreCase);
            SavePreferences();
        };
        row.AddChild(value);
        row.AddChild(mute);
        _dialogButtons.Add(mute);
        channel.AddChild(row);
        channel.AddChild(slider);
        content.AddChild(channel);
    }

    private void StopMusic()
    {
        if (_musicStopped) return;
        _musicCancellation.Cancel();
        if (_musicPump is not null)
        {
            try { _musicPump.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (_musicCancellation.IsCancellationRequested) { }
            catch (InvalidOperationException error)
            {
                GD.PushError($"Music playback stopped: {error.Message}");
            }
        }
        _musicStopped = true;
        _music.Stop();
        _music.Stream = null;
        _musicPlayback.Dispose();
        _musicTrack?.Dispose();
        _musicTrack = null;
        _musicCancellation.Dispose();
    }
}
