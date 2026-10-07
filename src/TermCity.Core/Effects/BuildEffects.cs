using TermCity.Core.Util;

namespace TermCity.Core.Effects;

/// <summary>A glyph that was just removed from the map (what it looked like), used to draw its ghost.</summary>
public readonly record struct GhostCell(Pos Pos, string Glyph, Rgb Color);

/// <summary>A cell that was just built or zoned, with the colour associated with it.</summary>
public readonly record struct TintedCell(Pos Pos, Rgb Color);

/// <summary>
/// Demolition: each removed glyph shrinks to nothing while drifting toward the centre of the demolished area, as if
/// sucked into a vortex, and a few dust specks spiral in with it. Cells farther from the centre start first so the
/// whole area drains inwards. The map already shows the cleared ground underneath; the glyphs here are ghosts drawn
/// over it.
/// </summary>
public sealed class DemolishEffect : Effect
{
    /// <summary>Seconds one glyph takes to shrink away.</summary>
    public const double CellTime = 0.8;

    /// <summary>Spread of start times between the farthest and the nearest cell.</summary>
    public const double MaxStagger = 0.35;

    private const double DustTime = 0.7;
    private const int MaxDustCells = 120;

    private readonly GhostCell[] _cells;
    private readonly double[] _delays;
    private readonly double[] _dist;
    private readonly int _seed;
    private readonly int _dustEvery;

    public DemolishEffect(IReadOnlyList<GhostCell> cells, (double X, double Y) centre, int seed)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        Centre = centre;
        _seed = seed;
        _dist = new double[_cells.Length];
        double max = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            _dist[i] = Math.Sqrt(Sq(centre.X - (_cells[i].Pos.X + 0.5)) + Sq(centre.Y - (_cells[i].Pos.Y + 0.5)));
            max = Math.Max(max, _dist[i]);
        }

        _delays = new double[_cells.Length];
        for (int i = 0; i < _cells.Length; i++)
        {
            _delays[i] = max <= 0 ? 0 : MaxStagger * (1 - _dist[i] / max);
        }

        _dustEvery = Math.Max(1, _cells.Length / MaxDustCells);
    }

    public (double X, double Y) Centre { get; }

    public int CellsCount => _cells.Length;

    public override double Duration => MaxStagger + CellTime + 0.15;

    public override int Priority => 2;

    /// <summary>How far through its own shrink a cell is (0 untouched, 1 gone), for tests and callers.</summary>
    public double CellProgress(int index) => Easing.Remap(LocalTime, _delays[index], _delays[index] + CellTime);

    protected internal override void Contribute(EffectSink sink)
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            double t = CellProgress(i);
            if (t < 1)
            {
                double ease = Easing.EaseIn(t);
                double pull = ease * 0.85;
                double cx = cell.Pos.X + 0.5, cy = cell.Pos.Y + 0.5;
                float ox = (float)((Centre.X - cx) * pull);
                float oy = (float)((Centre.Y - cy) * pull);
                sink.AddCell(cell.Pos.X, cell.Pos.Y, new CellEffect(
                    cell.Glyph, (float)(1 - ease), ox, oy, cell.Color, 1f, Alpha: (float)(1 - 0.4 * t), Overlay: true));
            }

            if (i % _dustEvery == 0)
            {
                AddDust(sink, i, cell);
            }
        }
    }

    private void AddDust(EffectSink sink, int index, GhostCell cell)
    {
        int count = sink.Scaled(2);
        for (int k = 0; k < count; k++)
        {
            double start = _delays[index] + 0.1 * k + EffectRandom.Unit(_seed, cell.Pos.X, cell.Pos.Y, k) * 0.2;
            double t = Easing.Remap(LocalTime, start, start + DustTime);
            if (t <= 0 || t >= 1)
            {
                continue;
            }

            double rx = cell.Pos.X + 0.5 + EffectRandom.Signed(_seed, cell.Pos.X, cell.Pos.Y, 10 + k) * 0.35 - Centre.X;
            double ry = cell.Pos.Y + 0.5 + EffectRandom.Signed(_seed, cell.Pos.X, cell.Pos.Y, 20 + k) * 0.35 - Centre.Y;
            double radius = Math.Sqrt(rx * rx + ry * ry);
            if (radius < 0.3)
            {
                radius = 0.3;
                rx = 0.3;
                ry = 0;
            }

            double angle = Math.Atan2(ry, rx) + 3.0 * Easing.EaseIn(t);
            double r = radius * (1 - Easing.EaseIn(t));
            string glyph = EffectGlyphs.Dust[EffectRandom.Pick(_seed, EffectGlyphs.Dust.Length, cell.Pos.X, cell.Pos.Y, k)];
            sink.AddSprite(new EffectSprite(
                glyph, (float)(Centre.X + Math.Cos(angle) * r), (float)(Centre.Y + Math.Sin(angle) * r),
                EffectGlyphs.DustColor, 0.8f, (float)Easing.Envelope(t, 0.15, 0.7)));
        }
    }

    private static double Sq(double v) => v * v;
}

/// <summary>How a <see cref="ConstructEffect"/> shows a building appearing.</summary>
public enum ConstructStyle
{
    /// <summary>The glyph pops in from nothing (natural growth in a zone).</summary>
    Grow,

    /// <summary>A scaffold climbs through the block characters, then the finished glyph settles in (placed buildings).</summary>
    BuildUp,
}

/// <summary>
/// Construction: cells that gained a building are hidden, then built up (a scaffold of ▁▂▃▄▅▆▇█, or a scale-in pop)
/// with a sparkle when finished. Start times are jittered per cell so a batch of new buildings does not move in lockstep.
/// </summary>
public sealed class ConstructEffect : Effect
{
    public const double CellTime = 0.9;
    public const double MaxStagger = 0.4;

    private readonly TintedCell[] _cells;
    private readonly int _seed;

    public ConstructEffect(IReadOnlyList<TintedCell> cells, ConstructStyle style, int seed)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        Style = style;
        _seed = seed;
    }

    public ConstructStyle Style { get; }

    public override double Duration => MaxStagger + CellTime + 0.1;

    public override int Priority => 1;

    public double StartOf(Pos p) => EffectRandom.Unit(_seed, p.X, p.Y) * MaxStagger;

    protected internal override void Contribute(EffectSink sink)
    {
        foreach (var cell in _cells)
        {
            double start = StartOf(cell.Pos);
            double t = Easing.Remap(LocalTime, start, start + CellTime);
            if (t >= 1)
            {
                continue;
            }

            int x = cell.Pos.X, y = cell.Pos.Y;
            if (t <= 0)
            {
                sink.AddCell(x, y, new CellEffect(Scale: 0f, Alpha: 0f));
                continue;
            }

            if (Style == ConstructStyle.Grow)
            {
                sink.AddCell(x, y, new CellEffect(Scale: (float)Easing.EaseOutCubic(t)));
            }
            else if (t < 0.6)
            {
                int stage = Math.Min(EffectGlyphs.BuildBars.Length - 1, (int)(t / 0.6 * EffectGlyphs.BuildBars.Length));
                sink.AddCell(x, y, new CellEffect(
                    EffectGlyphs.BuildBars[stage].ToString(), 1f, 0f, 0f, EffectGlyphs.ScaffoldColor, 1f));
            }
            else
            {
                double u = Easing.Remap(t, 0.6, 1);
                sink.AddCell(x, y, new CellEffect(
                    Scale: (float)Easing.Lerp(0.5, 1, Easing.EaseOutCubic(u)),
                    Tint: EffectGlyphs.ScaffoldColor, TintAmount: (float)(1 - u)));
            }

            AddSparkle(sink, cell, t);
        }
    }

    private void AddSparkle(EffectSink sink, TintedCell cell, double t)
    {
        double s = Easing.Remap(t, 0.55, 1);
        if (s <= 0 || s >= 1)
        {
            return;
        }

        int count = sink.Scaled(2);
        for (int k = 0; k < count; k++)
        {
            double side = EffectRandom.Signed(_seed, cell.Pos.X, cell.Pos.Y, 30 + k);
            string glyph = EffectGlyphs.Sparkle[EffectRandom.Pick(_seed, EffectGlyphs.Sparkle.Length, cell.Pos.X, cell.Pos.Y, k)];
            sink.AddSprite(new EffectSprite(
                glyph, (float)(cell.Pos.X + 0.5 + side * 0.45), (float)(cell.Pos.Y + 0.35 - 0.6 * s),
                EffectGlyphs.SparkleColor, (float)(0.6 + 0.5 * Easing.Bump(s)), (float)Easing.Envelope(s, 0.2, 0.6)));
        }
    }
}

/// <summary>
/// Zoning: a ripple of the zone colour sweeps outward from where the player dragged, each cell pulsing in turn.
/// </summary>
public sealed class ZoneRippleEffect : Effect
{
    public const double PulseTime = 0.55;
    public const double SecondsPerCell = 0.05;
    public const double MaxSweep = 1.2;

    private readonly TintedCell[] _cells;
    private readonly double[] _delays;
    private readonly double _total;

    public ZoneRippleEffect(IReadOnlyList<TintedCell> cells, (double X, double Y) origin)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        _delays = new double[_cells.Length];
        double maxDist = 0;
        for (int i = 0; i < _cells.Length; i++)
        {
            double dx = _cells[i].Pos.X + 0.5 - origin.X, dy = _cells[i].Pos.Y + 0.5 - origin.Y;
            _delays[i] = Math.Sqrt(dx * dx + dy * dy);
            maxDist = Math.Max(maxDist, _delays[i]);
        }

        double step = maxDist * SecondsPerCell > MaxSweep ? MaxSweep / maxDist : SecondsPerCell;
        for (int i = 0; i < _delays.Length; i++)
        {
            _delays[i] *= step;
        }

        _total = maxDist * step + PulseTime;
    }

    public override double Duration => _total;

    protected internal override void Contribute(EffectSink sink)
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            double t = Easing.Remap(LocalTime, _delays[i], _delays[i] + PulseTime);
            if (t <= 0 || t >= 1)
            {
                continue;
            }

            double bump = Easing.Bump(t);
            sink.AddCell(_cells[i].Pos.X, _cells[i].Pos.Y, new CellEffect(
                Scale: (float)(1 + 0.2 * bump), BackgroundTint: _cells[i].Color, BackgroundAmount: (float)(0.7 * bump)));
        }
    }
}

/// <summary>A road cell that was just laid, with the background it replaced (so the new road can be hidden until drawn).</summary>
public readonly record struct RoadCell(Pos Pos, Rgb OldBackground);

/// <summary>
/// Road building: the new road is drawn in cell by cell like a pen stroke, with a bright head that settles into the
/// finished road glyph behind it.
/// </summary>
public sealed class RoadStrokeEffect : Effect
{
    public const double StepTime = 0.045;
    public const double MaxStroke = 1.5;
    public const double SettleTime = 0.2;
    public const int MaxOrdered = 400;

    private readonly RoadCell[] _cells;
    private readonly double _step;

    /// <summary>Cells must already be in drawing order; see <see cref="OrderPath"/>.</summary>
    public RoadStrokeEffect(IReadOnlyList<RoadCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = [.. cells];
        _step = _cells.Length * StepTime > MaxStroke ? MaxStroke / _cells.Length : StepTime;
    }

    public override double Duration => _cells.Length * _step + SettleTime;

    public override int Priority => 1;

    public double RevealTime(int index) => index * _step;

    protected internal override void Contribute(EffectSink sink)
    {
        int head = -1;
        for (int i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            double t = Easing.Remap(LocalTime, i * _step, i * _step + SettleTime);
            if (LocalTime < i * _step)
            {
                sink.AddCell(cell.Pos.X, cell.Pos.Y, new CellEffect(
                    Alpha: 0f, BackgroundTint: cell.OldBackground, BackgroundAmount: 1f));
                continue;
            }

            if (t >= 1)
            {
                continue;
            }

            head = i;
            sink.AddCell(cell.Pos.X, cell.Pos.Y, new CellEffect(
                Scale: (float)(1 + 0.35 * (1 - t)), Tint: EffectGlyphs.PenColor, TintAmount: (float)(1 - t),
                BackgroundTint: cell.OldBackground, BackgroundAmount: (float)(1 - Easing.EaseOut(t))));
        }

        if (head >= 0)
        {
            var p = _cells[head].Pos;
            sink.AddSprite(new EffectSprite("✦", p.X + 0.5f, p.Y + 0.5f, EffectGlyphs.PenColor, 0.9f, 0.9f));
        }
    }

    /// <summary>
    /// Orders cells into a plausible pen path: start at an end of the stroke, then keep stepping to an adjacent
    /// unvisited cell, jumping to the nearest remaining one when stuck. Larger inputs are returned unordered
    /// (sorted by position) rather than paying quadratic time.
    /// </summary>
    public static IReadOnlyList<Pos> OrderPath(IEnumerable<Pos> cells)
    {
        var remaining = cells.Distinct().OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
        if (remaining.Count <= 1 || remaining.Count > MaxOrdered)
        {
            return remaining;
        }

        var set = remaining.ToHashSet();
        Pos Start()
        {
            foreach (var p in remaining)
            {
                int n = Neighbours(p, set).Count();
                if (n <= 1)
                {
                    return p;
                }
            }

            return remaining[0];
        }

        var result = new List<Pos>(remaining.Count);
        var current = Start();
        while (true)
        {
            result.Add(current);
            set.Remove(current);
            if (set.Count == 0)
            {
                break;
            }

            var next = Neighbours(current, set).Cast<Pos?>().FirstOrDefault();
            if (next is null)
            {
                Pos from = current;
                next = set.OrderBy(p => Math.Abs(p.X - from.X) + Math.Abs(p.Y - from.Y)).ThenBy(p => p.Y).ThenBy(p => p.X).First();
            }

            current = next.Value;
        }

        return result;
    }

    private static IEnumerable<Pos> Neighbours(Pos p, HashSet<Pos> set)
    {
        foreach (var n in new[] { p.Offset(1, 0), p.Offset(0, 1), p.Offset(-1, 0), p.Offset(0, -1) })
        {
            if (set.Contains(n))
            {
                yield return n;
            }
        }
    }
}
