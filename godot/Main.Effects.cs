using TermCity.Core.Effects;
using TermCity.Core.Session;

namespace TermCity.GodotApp;

public partial class Main
{
    public EffectSystem Effects { get; private set; } = null!;

    public EffectDirector Director { get; private set; } = null!;

    public EffectLevel EffectsLevel { get; private set; } = EffectLevel.High;

    private double _effectRedrawClock;

    private const double AmbientRedrawSeconds = 1.0 / 15;

    public bool CelebrationsEnabled { get; private set; }

    private void CreateEffects()
    {
        Effects = new EffectSystem(Session.Game.Config.Seed ^ 0x5eed1e5,
            new EffectSettings { ReducedMotion = _options.ReducedMotion, Celebrations = CelebrationsEnabled });
        Effects.Settings.Level = EffectsLevel;
        Director = new EffectDirector(Effects);
        Director.Attach(Session);
        Map.Effects = Effects;
    }

    public void SetEffectsLevel(EffectLevel level)
    {
        EffectsLevel = level;
        Effects.Settings.Level = level;
        Map.Invalidate(false);
        SavePreferences();
    }

    private void CycleEffects()
    {
        if (Effects.Settings.ReducedMotion)
        {
            Session.SetMessage("Effects are disabled by --reduced-motion.", MessageKind.Info);
            return;
        }
        SetEffectsLevel(EffectsLevel switch
        {
            EffectLevel.High => EffectLevel.Low,
            EffectLevel.Low => EffectLevel.Off,
            _ => EffectLevel.High,
        });
        Session.SetMessage($"Effects: {EffectsLabel()}.", MessageKind.Info);
        RefreshMenuState(CycleEffects, EffectsLabel());
    }

    private string EffectsLabel() => Effects.Settings.ReducedMotion ? "OFF (reduced motion)" : EffectsLevel.ToString().ToUpperInvariant();

    private void ToggleCelebrations()
    {
        CelebrationsEnabled = !CelebrationsEnabled;
        Effects.Settings.Celebrations = CelebrationsEnabled;
        if (!CelebrationsEnabled) Effects.ClearCelebrations();
        Map.Invalidate(false);
        SavePreferences();
        RefreshMenuState(ToggleCelebrations, CelebrationsEnabled ? "ON" : "OFF");
    }

    private void UpdateEffects(double delta)
    {
        if (_focused) Director.Update(delta);
        if (!Effects.NeedsRedraw) return;
        _effectRedrawClock += delta;
        // One-shot effects and shakes redraw every frame; ambient life alone redraws at a calmer rate to save CPU.
        if (Effects.ActiveOneShots > 0 || Effects.Shake != default || !Effects.HasVisuals ||
            _effectRedrawClock >= AmbientRedrawSeconds)
        {
            _effectRedrawClock = 0;
            Map.QueueRedraw();
        }
    }
}
