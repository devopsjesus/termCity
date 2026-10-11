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

    private void AddAudioControls(Container content, bool music)
    {
        var channel = new VBoxContainer();
        channel.AddThemeConstantOverride("separation", 3);
        var row = new HBoxContainer();
        var label = new Label { Text = music ? "MUSIC" : "SOUND",
            VerticalAlignment = VerticalAlignment.Center };
        var labelCell = ConstructionTableCell(label);
        labelCell.Name = music ? "MusicLabelCell" : "SoundLabelCell";
        labelCell.SizeFlagsHorizontal = SizeFlags.Fill;
        row.AddChild(labelCell);
        var slider = new HSlider
        {
            Name = music ? "MusicVolume" : "SoundVolume",
            MinValue = 0, MaxValue = 100, Step = 1,
            Value = music ? MusicVolume : SoundVolume,
            FocusMode = FocusModeEnum.All,
            CustomMinimumSize = new Vector2(200, 32), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        foreach (string state in new[] { "slider", "grabber_area", "grabber_area_highlight" })
        {
            var track = new StyleBoxFlat
            {
                BgColor = new Color(state == "slider" ? "#101010" :
                    state == "grabber_area_highlight" ? "#243847" : "#18232c"),
                BorderColor = Colors.White,
                ContentMarginTop = 4, ContentMarginBottom = 4,
            };
            track.SetBorderWidthAll(1);
            slider.AddThemeStyleboxOverride(state, track);
        }
        foreach (string state in new[] { "grabber", "grabber_highlight", "grabber_disabled" })
        {
            using var image = Image.CreateEmpty(12, 20, false, Image.Format.Rgba8);
            image.Fill(state == "grabber_disabled" ? new Color("#777777") :
                state == "grabber_highlight" ? new Color("#ffe066") : Colors.White);
            slider.AddThemeIconOverride(state, ImageTexture.CreateFromImage(image));
        }
        slider.AddThemeConstantOverride("grabber_offset", 0);
        var mute = new Button { Name = music ? "MusicMute" : "SoundMute",
            CustomMinimumSize = new Vector2(36, 32),
            TooltipText = music ? "Mute/unmute music (M)" : "Mute/unmute sound (U)" };
        var speaker = new Control { MouseFilter = MouseFilterEnum.Ignore };
        speaker.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        speaker.Draw += () =>
        {
            var origin = (speaker.Size / 2).Floor();
            speaker.DrawRect(new Rect2(origin + new Vector2(-10, -4), new Vector2(6, 8)), Colors.White);
            speaker.DrawRect(new Rect2(origin + new Vector2(-4, -6), new Vector2(2, 12)), Colors.White);
            speaker.DrawRect(new Rect2(origin + new Vector2(-2, -8), new Vector2(2, 16)), Colors.White);
            speaker.DrawRect(new Rect2(origin + new Vector2(0, -10), new Vector2(4, 20)), Colors.White);
            if (music ? MusicEnabled : SoundEnabled)
            {
                speaker.DrawRect(new Rect2(origin + new Vector2(6, -4), new Vector2(2, 8)), Colors.White);
                speaker.DrawRect(new Rect2(origin + new Vector2(10, -8), new Vector2(2, 2)), Colors.White);
                speaker.DrawRect(new Rect2(origin + new Vector2(12, -6), new Vector2(2, 12)), Colors.White);
                speaker.DrawRect(new Rect2(origin + new Vector2(10, 6), new Vector2(2, 2)), Colors.White);
            }
        };
        mute.AddChild(speaker);
        slider.ValueChanged += volume =>
        {
            if (music) MusicVolume = volume; else SoundVolume = volume;
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
            speaker.QueueRedraw();
            SavePreferences();
        };
        var sliderPadding = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sliderPadding.AddThemeConstantOverride("margin_left", 12);
        sliderPadding.AddThemeConstantOverride("margin_right", 12);
        sliderPadding.AddChild(slider);
        row.AddChild(sliderPadding);
        row.AddChild(mute);
        _dialogControls.Add(slider);
        _dialogControls.Add(mute);
        channel.AddChild(row);
        var frame = ConstructionTableCell(channel);
        frame.Name = music ? "MusicControls" : "SoundControls";
        frame.GetThemeStylebox("panel").SetContentMarginAll(4);
        content.AddChild(frame);
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
