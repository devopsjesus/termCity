namespace TermCity.Core.Effects;

/// <summary>Coarse user-facing effect level; maps onto <see cref="EffectSettings"/>.</summary>
public enum EffectLevel
{
    Off,
    Low,
    High,
}

/// <summary>
/// User preferences and budgets for the effect system. <see cref="Active"/> is the single gate every spawn and frame
/// build checks: when it is false nothing is spawned and anything already running is cleared on the next update.
/// </summary>
public sealed class EffectSettings
{
    private double _intensity = 1;
    private int _maxEffects = 48, _maxCellEffects = 1500, _maxSprites = 400, _maxAmbientSprites = 48;

    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reduced-motion preference. Every effect is motion (scale, drift, particles, shake), so this disables them all;
    /// the underlying game changes still appear instantly.
    /// </summary>
    public bool ReducedMotion { get; set; }

    /// <summary>0..1 scale on particle counts, ambient density and shake amplitude. Zero disables effects.</summary>
    public double Intensity
    {
        get => _intensity;
        set
        {
            if (double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Intensity must be a number.");
            }

            _intensity = Math.Clamp(value, 0, 1);
        }
    }

    /// <summary>Most live (non-ambient) effects. When full, a new effect evicts the oldest one of lower or equal priority.</summary>
    public int MaxEffects
    {
        get => _maxEffects;
        set => _maxEffects = NonNegative(value);
    }

    /// <summary>Most distinct cells that may be modified in one frame.</summary>
    public int MaxCellEffects
    {
        get => _maxCellEffects;
        set => _maxCellEffects = NonNegative(value);
    }

    /// <summary>Most sprites drawn in one frame, ambient life included.</summary>
    public int MaxSprites
    {
        get => _maxSprites;
        set => _maxSprites = NonNegative(value);
    }

    /// <summary>Most sprites ambient life (people, cars, birds, smoke) may use at full intensity.</summary>
    public int MaxAmbientSprites
    {
        get => _maxAmbientSprites;
        set => _maxAmbientSprites = NonNegative(value);
    }

    public bool Active => Enabled && !ReducedMotion && Intensity > 0;

    public EffectLevel Level
    {
        get => !Active ? EffectLevel.Off : Intensity < 1 ? EffectLevel.Low : EffectLevel.High;
        set
        {
            Enabled = value != EffectLevel.Off;
            if (value != EffectLevel.Off)
            {
                Intensity = value == EffectLevel.Low ? 0.5 : 1;
            }
        }
    }

    /// <summary>Scales a particle count by intensity; zero when inactive. A positive request stays at least 1 while active.</summary>
    public int Scaled(int count) => !Active || count <= 0 ? 0 : Math.Max(1, (int)Math.Round(count * Intensity));

    private static int NonNegative(int value) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Budgets cannot be negative.");
}
