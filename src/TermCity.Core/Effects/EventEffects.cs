using TermCity.Core.Util;

namespace TermCity.Core.Effects;

/// <summary>
/// A building burning: its ghost glyph glows and shrinks while flame sprites flicker above it. Pair with
/// <see cref="CollapseEffect"/> (spawned with a delay) for the smoke when it comes down.
/// </summary>
public sealed class FireEffect : Effect
{
    private readonly GhostCell[] _cells;
    private readonly int _seed;
    private readonly double _duration;

    public FireEffect(IReadOnlyList<GhostCell> cells, int seed, double duration = 3.0)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _cells = [.. cells];
        _seed = seed;
        _duration = duration;
    }

    public override double Duration => _duration;

    public override int Priority => 3;

    protected internal override void Contribute(EffectSink sink)
    {
        double p = Progress;
        foreach (var cell in _cells)
        {
            int x = cell.Pos.X, y = cell.Pos.Y;
            double flicker = 0.5 + 0.5 * Math.Sin(LocalTime * 11 + EffectRandom.Unit(_seed, x, y) * 6.28);
            var glow = Rgb.Blend(EffectGlyphs.Flame1, EffectGlyphs.Flame2, flicker);
            if (!string.IsNullOrWhiteSpace(cell.Glyph))
            {
                sink.AddCell(x, y, new CellEffect(
                    cell.Glyph, (float)Easing.Lerp(1, 0.55, Easing.EaseIn(p)), Tint: glow, TintAmount: 0.85f,
                    Alpha: (float)Easing.Lerp(1, 0.4, p), Overlay: true));
            }

            int flames = sink.Scaled(2);
            for (int k = 0; k < flames; k++)
            {
                double phase = LocalTime * 7 + EffectRandom.Unit(_seed, x, y, k) * 9;
                int shape = (int)Math.Abs(Math.Floor(phase)) % EffectGlyphs.Flame.Length;
                double rise = (phase - Math.Floor(phase)) * 0.5;
                float sx = (float)(x + 0.5 + EffectRandom.Signed(_seed, x, y, 5 + k) * 0.3);
                float sy = (float)(y + 0.45 - rise);
                var color = ((int)Math.Floor(phase) + k) % 2 == 0 ? EffectGlyphs.Flame1 : EffectGlyphs.Flame2;
                sink.AddSprite(new EffectSprite(
                    EffectGlyphs.Flame[shape], sx, sy, color, (float)(0.8 + 0.4 * flicker),
                    (float)Easing.Envelope(p, 0.08, 0.8)));
            }
        }
    }
}

/// <summary>Smoke puffs and dust rising from a spot where something just collapsed.</summary>
public sealed class CollapseEffect : Effect
{
    public const double Life = 2.2;
    public const double Stagger = 0.5;

    private readonly Pos[] _cells;
    private readonly int _seed;

    public CollapseEffect(IReadOnlyList<Pos> cells, int seed)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        _seed = seed;
    }

    public override double Duration => Life + Stagger;

    protected internal override void Contribute(EffectSink sink)
    {
        foreach (var cell in _cells)
        {
            int puffs = sink.Scaled(3);
            for (int k = 0; k < puffs; k++)
            {
                double start = EffectRandom.Unit(_seed, cell.X, cell.Y, k) * Stagger;
                double t = Easing.Remap(LocalTime, start, start + Life);
                if (t <= 0 || t >= 1)
                {
                    continue;
                }

                double sway = Math.Sin(t * 5 + k * 2) * 0.25 + EffectRandom.Signed(_seed, cell.X, cell.Y, 40 + k) * 0.2;
                string glyph = EffectGlyphs.Smoke[EffectRandom.Pick(_seed, EffectGlyphs.Smoke.Length, cell.X, cell.Y, k)];
                sink.AddSprite(new EffectSprite(
                    glyph, (float)(cell.X + 0.5 + sway), (float)(cell.Y + 0.5 - 2.0 * Easing.EaseOut(t)),
                    EffectGlyphs.SmokeColor, (float)(0.6 + 0.9 * t), (float)(0.8 * Easing.Envelope(t, 0.1, 0.4))));
            }
        }
    }
}

/// <summary>Flooding: the water colour washes over the cells and ripple glyphs drift across them.</summary>
public sealed class FloodEffect : Effect
{
    private readonly Pos[] _cells;
    private readonly int _seed;
    private readonly double _duration;

    public FloodEffect(IReadOnlyList<Pos> cells, int seed, double duration = 3.5)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        _seed = seed;
        _duration = duration;
    }

    public override double Duration => _duration;

    public override int Priority => 2;

    protected internal override void Contribute(EffectSink sink)
    {
        double p = Progress;
        double wash = Easing.Envelope(p, 0.2, 0.75);
        foreach (var cell in _cells)
        {
            sink.AddCell(cell.X, cell.Y, new CellEffect(
                Tint: EffectGlyphs.WaterColor, TintAmount: (float)(0.5 * wash),
                BackgroundTint: EffectGlyphs.WaterBackground, BackgroundAmount: (float)(0.6 * wash)));
            if (sink.Scaled(1) > 0)
            {
                double phase = LocalTime * 1.6 + EffectRandom.Unit(_seed, cell.X, cell.Y) * 6.28;
                string glyph = EffectGlyphs.Ripple[EffectRandom.Pick(_seed, EffectGlyphs.Ripple.Length, cell.X, cell.Y)];
                sink.AddSprite(new EffectSprite(
                    glyph, (float)(cell.X + 0.5 + 0.3 * Math.Sin(phase)), (float)(cell.Y + 0.6 + 0.1 * Math.Cos(phase * 1.3)),
                    EffectGlyphs.WaterColor, 0.9f, (float)(0.9 * wash)));
            }
        }
    }
}

/// <summary>Earthquake: the whole view shakes (decaying), and hit cells jitter with a crack mark.</summary>
public sealed class QuakeEffect : Effect
{
    public const double DefaultDuration = 1.6;
    public const int MaxJitterCells = 60;

    private readonly Pos[] _cells;
    private readonly int _seed;
    private readonly double _magnitude;

    public QuakeEffect(IReadOnlyList<Pos> cells, int seed, double magnitude = 1)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells.Take(MaxJitterCells)];
        _seed = seed;
        _magnitude = Math.Clamp(magnitude, 0, 3);
    }

    public override double Duration => DefaultDuration;

    public override int Priority => 3;

    /// <summary>Peak shake amplitude in cell units at full intensity.</summary>
    public const double PeakShake = 0.18;

    protected internal override void Contribute(EffectSink sink)
    {
        double p = Progress;
        double decay = Math.Pow(1 - p, 1.5);
        double amp = PeakShake * _magnitude * decay * sink.Intensity;
        sink.AddShake((float)(amp * Math.Sin(LocalTime * 47)), (float)(amp * 0.6 * Math.Cos(LocalTime * 61)));
        foreach (var cell in _cells)
        {
            double j = 0.12 * decay * sink.Intensity;
            sink.AddCell(cell.X, cell.Y, new CellEffect(
                OffsetX: (float)(j * Math.Sin(LocalTime * 53 + cell.X)), OffsetY: (float)(j * Math.Cos(LocalTime * 59 + cell.Y))));
            if (p > 0.15 && p < 0.9)
            {
                sink.AddSprite(new EffectSprite(
                    EffectGlyphs.Crack, cell.X + 0.5f, cell.Y + 0.5f, EffectGlyphs.DustColor, 1.1f,
                    (float)(0.8 * Easing.Envelope(p, 0.3, 0.7))));
            }
        }
    }
}

/// <summary>
/// Outbreak / abandonment: cells drain of colour and fade for a moment, then recover. With <c>ghosts</c> (things that
/// were just removed, such as an abandoned home) the ghost glyphs wash out and vanish instead.
/// </summary>
public sealed class FadeEffect : Effect
{
    private readonly Pos[] _cells;
    private readonly GhostCell[] _ghosts;
    private readonly Rgb _tint;
    private readonly double _duration;

    public FadeEffect(IReadOnlyList<Pos> cells, Rgb tint, double duration = 2.5, IReadOnlyList<GhostCell>? ghosts = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        _ghosts = ghosts is null ? [] : [.. ghosts];
        _tint = tint;
        _duration = duration;
    }

    public override double Duration => _duration;

    protected internal override void Contribute(EffectSink sink)
    {
        double p = Progress;
        double e = Easing.Envelope(p, 0.3, 0.65);
        foreach (var cell in _cells)
        {
            sink.AddCell(cell.X, cell.Y, new CellEffect(
                Tint: _tint, TintAmount: (float)(0.75 * e), Alpha: (float)(1 - 0.55 * e)));
        }

        foreach (var ghost in _ghosts)
        {
            sink.AddCell(ghost.Pos.X, ghost.Pos.Y, new CellEffect(
                ghost.Glyph, (float)Easing.Lerp(1, 0.9, p), Tint: Rgb.Blend(ghost.Color, _tint, Math.Min(1, 0.3 + p)),
                TintAmount: 1f, Alpha: (float)(1 - Easing.SmoothStep(p)), Overlay: true));
        }
    }
}

/// <summary>Milestone celebration: confetti bursts up and out from a point and falls back with gravity.</summary>
public sealed class ConfettiEffect : Effect
{
    public const double Life = 3.2;
    public const double Gravity = 12;

    private readonly (double X, double Y) _origin;
    private readonly int _count;
    private readonly int _seed;

    public ConfettiEffect((double X, double Y) origin, int count, int seed)
    {
        _origin = origin;
        _count = Math.Max(0, count);
        _seed = seed;
    }

    public override double Duration => Life + 0.3;

    public override int Priority => 1;

    public int Count => _count;

    /// <summary>Position of particle <paramref name="i"/> at <paramref name="t"/> seconds after it launched.</summary>
    public (double X, double Y) PositionAt(int i, double t)
    {
        double angle = -Math.PI / 2 + EffectRandom.Signed(_seed, i, 1) * 1.1;
        double speed = 5 + EffectRandom.Unit(_seed, i, 2) * 7;
        return (_origin.X + Math.Cos(angle) * speed * 1.4 * t, _origin.Y + Math.Sin(angle) * speed * t + 0.5 * Gravity * t * t);
    }

    protected internal override void Contribute(EffectSink sink)
    {
        int n = Math.Min(_count, sink.Scaled(_count));
        for (int i = 0; i < n; i++)
        {
            double start = EffectRandom.Unit(_seed, i, 3) * 0.3;
            double t = LocalTime - start;
            if (t <= 0 || t >= Life)
            {
                continue;
            }

            var (x, y) = PositionAt(i, t);
            double life = t / Life;
            sink.AddSprite(new EffectSprite(
                EffectGlyphs.Confetti[EffectRandom.Pick(_seed, EffectGlyphs.Confetti.Length, i, 4)], (float)x, (float)y,
                EffectGlyphs.ConfettiColors[EffectRandom.Pick(_seed, EffectGlyphs.ConfettiColors.Length, i, 5)],
                (float)(0.8 + 0.4 * EffectRandom.Unit(_seed, i, 6)), (float)(1 - Easing.Remap(life, 0.7, 1))));
        }
    }
}

/// <summary>Tax day: coins ($ and ¢) float up from buildings and fade.</summary>
public sealed class CoinsEffect : Effect
{
    public const double Life = 1.4;
    public const double Spread = 1.0;

    private readonly Pos[] _sources;
    private readonly int _seed;

    public CoinsEffect(IReadOnlyList<Pos> sources, int seed)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        _seed = seed;
    }

    public override double Duration => Life + Spread;

    protected internal override void Contribute(EffectSink sink)
    {
        foreach (var source in _sources)
        {
            double start = EffectRandom.Unit(_seed, source.X, source.Y) * Spread;
            double t = Easing.Remap(LocalTime, start, start + Life);
            if (t <= 0 || t >= 1)
            {
                continue;
            }

            string glyph = EffectGlyphs.Coin[EffectRandom.Pick(_seed, EffectGlyphs.Coin.Length, source.X, source.Y, 1)];
            sink.AddSprite(new EffectSprite(
                glyph, (float)(source.X + 0.5 + 0.2 * Math.Sin(t * 6 + source.X)), (float)(source.Y + 0.2 - 2.0 * Easing.EaseOut(t)),
                EffectGlyphs.CoinColor, 1f, (float)Easing.Envelope(t, 0.1, 0.55)));
        }
    }
}
