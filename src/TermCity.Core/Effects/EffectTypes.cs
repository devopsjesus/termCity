using TermCity.Core.Util;

namespace TermCity.Core.Effects;

/// <summary>
/// How an effect modifies the glyph already drawn in one map cell. Offsets are in cell units (1 = one cell width or
/// height; +Y is down). <b>Always use <see cref="Identity"/> rather than <c>default</c></b>: a defaulted struct has
/// scale and alpha of zero.
/// <para>
/// Scale, offset, tint and alpha apply to whatever glyph is drawn for the cell: <see cref="Glyph"/> when set,
/// otherwise the cell's own glyph. A <see cref="Glyph"/> normally <i>replaces</i> the cell's own glyph; with
/// <see cref="Overlay"/> it is drawn on top and the cell's own glyph stays (a ghost of something that was removed).
/// </para>
/// </summary>
public readonly record struct CellEffect(
    string? Glyph = null,
    float Scale = 1f,
    float OffsetX = 0f,
    float OffsetY = 0f,
    Rgb? Tint = null,
    float TintAmount = 0f,
    Rgb? BackgroundTint = null,
    float BackgroundAmount = 0f,
    float Alpha = 1f,
    bool Overlay = false)
{
    public CellEffect() : this(null)
    {
    }

    /// <summary>No change: the cell is drawn exactly as the map renderer produced it.</summary>
    public static CellEffect Identity => new();

    public bool IsIdentity =>
        Glyph is null && Scale == 1f && OffsetX == 0f && OffsetY == 0f && TintAmount == 0f &&
        BackgroundAmount == 0f && Alpha == 1f;

    /// <summary>
    /// Merges two modifiers: scales and alphas multiply, offsets add, a replacement glyph from <paramref name="b"/> wins,
    /// and the stronger tint (by amount) wins.
    /// </summary>
    public static CellEffect Combine(in CellEffect a, in CellEffect b)
    {
        bool tintFromB = b.TintAmount > a.TintAmount;
        bool backgroundFromB = b.BackgroundAmount > a.BackgroundAmount;
        return new CellEffect(
            b.Glyph ?? a.Glyph,
            a.Scale * b.Scale,
            a.OffsetX + b.OffsetX,
            a.OffsetY + b.OffsetY,
            tintFromB ? b.Tint : a.Tint,
            tintFromB ? b.TintAmount : a.TintAmount,
            backgroundFromB ? b.BackgroundTint : a.BackgroundTint,
            backgroundFromB ? b.BackgroundAmount : a.BackgroundAmount,
            a.Alpha * b.Alpha,
            b.Glyph is not null ? b.Overlay : a.Overlay);
    }
}

/// <summary>
/// A free-floating glyph drawn over the map. <see cref="X"/>/<see cref="Y"/> are the glyph's centre in fractional
/// <i>map</i> cell units, so cell (3, 5) has its centre at (3.5, 5.5). Sprites are not tied to the grid and may
/// move between and across cells.
/// </summary>
public readonly record struct EffectSprite(
    string Glyph, float X, float Y, Rgb Color, float Scale = 1f, float Alpha = 1f);

/// <summary>
/// Receives what the live effects want drawn this frame and enforces the per-frame budgets. Anything over budget is
/// dropped (and counted) rather than slowing the frame down.
/// </summary>
public sealed class EffectSink
{
    private readonly EffectSettings _settings;
    private readonly Dictionary<int, CellEffect> _cells = [];
    private readonly List<EffectSprite> _sprites = [];

    internal EffectSink(EffectSettings settings) => _settings = settings;

    public int CellCount => _cells.Count;

    public int SpriteCount => _sprites.Count;

    /// <summary>Contributions refused this frame because a budget was full.</summary>
    public int Dropped { get; private set; }

    /// <summary>Whole-view shake offset in cell units, added up from every shaking effect.</summary>
    public (float X, float Y) Shake { get; private set; }

    internal int AmbientSprites { get; private set; }

    internal bool CurrentIsAmbient { get; set; }

    internal IReadOnlyList<EffectSprite> Sprites => _sprites;

    internal static int Key(int x, int y) => (y << 16) | x;

    /// <summary>Layers a modifier onto a map cell. Returns false when out of range or over the cell budget.</summary>
    public bool AddCell(int x, int y, in CellEffect effect)
    {
        if (x < 0 || y < 0 || x > short.MaxValue || y > short.MaxValue)
        {
            return false;
        }

        int key = Key(x, y);
        if (_cells.TryGetValue(key, out var existing))
        {
            _cells[key] = CellEffect.Combine(existing, effect);
            return true;
        }

        if (_cells.Count >= _settings.MaxCellEffects)
        {
            Dropped++;
            return false;
        }

        _cells[key] = effect;
        return true;
    }

    /// <summary>Adds a sprite. Invisible sprites (zero alpha or scale) are skipped without counting against the budget.</summary>
    public bool AddSprite(in EffectSprite sprite)
    {
        if (sprite.Alpha <= 0.01f || sprite.Scale <= 0.01f)
        {
            return true;
        }

        if (_sprites.Count >= _settings.MaxSprites ||
            CurrentIsAmbient && AmbientSprites >= AmbientLimit)
        {
            Dropped++;
            return false;
        }

        _sprites.Add(sprite);
        if (CurrentIsAmbient)
        {
            AmbientSprites++;
        }

        return true;
    }

    public void AddShake(float x, float y) => Shake = (Shake.X + x, Shake.Y + y);

    /// <summary>The user's intensity (0..1); effects scale shake amplitude and similar continuous values by it.</summary>
    public double Intensity => _settings.Intensity;

    /// <summary>Scales a particle count by the user's intensity (at least 1 for a positive count while active).</summary>
    public int Scaled(int count) => _settings.Scaled(count);

    internal int AmbientLimit => (int)Math.Min(_settings.MaxAmbientSprites, _settings.MaxSprites);

    internal bool TryGet(int x, int y, out CellEffect effect)
    {
        if (x >= 0 && y >= 0 && x <= short.MaxValue && y <= short.MaxValue)
        {
            return _cells.TryGetValue(Key(x, y), out effect);
        }

        effect = default;
        return false;
    }

    internal IEnumerable<KeyValuePair<int, CellEffect>> Cells => _cells;

    internal void Reset()
    {
        _cells.Clear();
        _sprites.Clear();
        Dropped = 0;
        Shake = default;
        AmbientSprites = 0;
        CurrentIsAmbient = false;
    }
}
