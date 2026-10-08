using TermCity.Core.Util;

namespace TermCity.Core.Effects;

[Flags]
public enum AmbientKind
{
    None = 0,
    Road = 1,
    Home = 2,
    Factory = 4,
    Water = 8,

    /// <summary>Open sea, as opposed to a river or a lake: water wide enough, and open enough to the map's edge, for whales.</summary>
    Sea = 16,
}

/// <summary>What ambient life may ask about the map. Out-of-bounds cells must return <see cref="AmbientKind.None"/>.</summary>
public interface IAmbientWorld
{
    AmbientKind Classify(int x, int y);

    /// <summary>
    /// Where a traveller at (<paramref name="x"/>, <paramref name="y"/>), in fractional map cells, is drawn so that it
    /// follows the curve of the road. A world with straight roads leaves the point alone.
    /// </summary>
    (double X, double Y) Snap(double x, double y) => (x, y);
}

/// <summary>
/// Ambient life: tiny cars and walkers moving along roads, birds crossing the view and smoke rising from factories.
/// It is cheap by construction: only what is inside the view exists, the whole thing shares a small sprite budget
/// (<see cref="EffectSettings.MaxAmbientSprites"/> scaled by intensity), the map is scanned a few times per second
/// at most, and actors are simple state machines, not simulations. It never touches the simulation.
/// </summary>
public sealed class AmbientLife : Effect
{
    public const double SmokeLife = 2.4;
    private const int SmokePuffs = 3;
    private const int ScanLimit = 6000;
    private const int MaxCandidates = 600;

    // Cells per second. Slow and unhurried: the town is meant to look lived in, not busy.
    internal static readonly (double Min, double Max) CarSpeed = (1.4, 2.4);
    internal static readonly (double Min, double Max) PersonSpeed = (0.35, 0.6);
    internal static readonly (double Min, double Max) BirdSpeed = (1.0, 2.0);

    /// <summary>How quickly a traveller's drawn position catches up with the curve it follows (per second).</summary>
    private const double FollowRate = 12;

    private readonly IAmbientWorld _world;
    private readonly List<Walker> _cars = [];
    private readonly List<Walker> _people = [];
    private readonly List<Bird> _birds = [];
    private readonly List<Pos> _roads = [];
    private readonly List<Pos> _homeRoads = [];
    private readonly List<Pos> _factories = [];
    private readonly List<Pos> _water = [];
    private readonly List<Pos> _sea = [];
    private readonly List<Splash> _splashes = [];
    private CellRect? _lastView;
    private double _lastScan;
    private double _clock;
    private double _birdClock;
    private int _smokeSeed;

    private sealed class Walker
    {
        public Pos Prev, Cell;
        public double T, Speed, X, Y;
        public bool Placed;
    }

    private sealed class Bird
    {
        public double X, Y, Vx, Vy, Phase;
    }

    /// <summary>A fish leaping, a whale surfacing or a few bubbles rising, at one cell of water.</summary>
    private sealed class Splash
    {
        public Pos Cell;
        public double Age, Life, Dir;
        public SplashKind Kind;
        public bool Whale => Kind == SplashKind.Whale;
    }

    private enum SplashKind { Fish, Whale, Bubbles }

    public AmbientLife(IAmbientWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    public override double Duration => 1;

    public override bool IsPersistent => true;

    public override bool IsAmbient => true;

    public override int Priority => -1;

    public int Cars => _cars.Count;

    public int People => _people.Count;

    public int Birds => _birds.Count;

    public int Fish => _splashes.Count(s => s.Kind == SplashKind.Fish);

    public int Bubbles => _splashes.Count(s => s.Kind == SplashKind.Bubbles);

    public int Whales => _splashes.Count(s => s.Whale);

    public int SmokeSources => _factories.Count;

    public bool TrafficAndBirdsPaused { get; set; }

    /// <summary>Most sprites this would draw at the current settings (the sink enforces the same cap).</summary>
    public int Budget(EffectSettings settings) => (int)(settings.MaxAmbientSprites * settings.Intensity);

    protected internal override void OnAdvance(double dt, EffectSystem system)
    {
        _clock += dt;
        if (system.View is not { } view)
        {
            _cars.Clear();
            _people.Clear();
            _birds.Clear();
            _splashes.Clear();
            _factories.Clear();
            return;
        }

        if (_smokeSeed == 0)
        {
            _smokeSeed = system.NextSeed() | 1;
        }

        double sinceScan = _clock - _lastScan;
        if (_lastView is null || (_lastView != view ? sinceScan >= 0.4 : sinceScan >= 3.0))
        {
            _lastScan = _clock;
            _lastView = view;
            Scan(view, system.Random);
        }

        int budget = Budget(system.Settings);
        int carTarget = Math.Min(budget * 35 / 100, _roads.Count / 6);
        int peopleTarget = Math.Min(budget * 25 / 100, _homeRoads.Count / 4);
        int birdTarget = budget >= 10 ? Math.Max(1, budget / 10) : 0;
        var rng = system.Random;
        if (!TrafficAndBirdsPaused)
        {
            _birdClock += dt;
            Step(_cars, dt, view, carTarget, _roads, rng, CarSpeed.Min, CarSpeed.Max);
            Step(_people, dt, view, peopleTarget, _homeRoads, rng, PersonSpeed.Min, PersonSpeed.Max);
            StepBirds(dt, view, birdTarget, rng);
        }
        StepWater(dt, view, budget >= 10 ? Math.Max(1, budget / 12) : 0, rng);
    }

    private void Scan(CellRect view, GameRandom rng)
    {
        _water.Clear();
        _sea.Clear();
        int waterSeen = 0, seaSeen = 0;
        _roads.Clear();
        _homeRoads.Clear();
        _factories.Clear();
        int stride = view.Area > ScanLimit ? (int)Math.Ceiling(Math.Sqrt(view.Area / (double)ScanLimit)) : 1;
        for (int y = view.Y; y <= view.Bottom; y += stride)
        {
            for (int x = view.X; x <= view.Right; x += stride)
            {
                var kind = _world.Classify(x, y);
                if ((kind & AmbientKind.Water) != 0)
                {
                    if ((kind & AmbientKind.Sea) != 0)
                    {
                        Reservoir(_sea, new Pos(x, y), ++seaSeen, rng);
                    }
                    else
                    {
                        Reservoir(_water, new Pos(x, y), ++waterSeen, rng);
                    }
                }
                else if ((kind & AmbientKind.Road) != 0)
                {
                    if (_roads.Count < MaxCandidates)
                    {
                        _roads.Add(new Pos(x, y));
                    }

                    if (_homeRoads.Count < MaxCandidates && NextToHome(x, y))
                    {
                        _homeRoads.Add(new Pos(x, y));
                    }
                }
                else if ((kind & AmbientKind.Factory) != 0 && _factories.Count < 8)
                {
                    _factories.Add(new Pos(x, y));
                }
            }
        }
    }

    /// <summary>Keeps a fair sample of a long run of cells, so a sea filling the view is not sampled only along its top rows.</summary>
    private static void Reservoir(List<Pos> list, Pos cell, int seen, GameRandom rng)
    {
        if (list.Count < MaxCandidates)
        {
            list.Add(cell);
            return;
        }

        int slot = rng.Next(seen);
        if (slot < MaxCandidates)
        {
            list[slot] = cell;
        }
    }

    private bool NextToHome(int x, int y) =>
        (_world.Classify(x + 1, y) & AmbientKind.Home) != 0 || (_world.Classify(x - 1, y) & AmbientKind.Home) != 0 ||
        (_world.Classify(x, y + 1) & AmbientKind.Home) != 0 || (_world.Classify(x, y - 1) & AmbientKind.Home) != 0;

    private void Step(List<Walker> actors, double dt, CellRect view, int target, List<Pos> spawn, GameRandom rng, double minSpeed, double maxSpeed)
    {
        for (int i = actors.Count - 1; i >= 0; i--)
        {
            var a = actors[i];
            a.T += a.Speed * dt;
            bool remove = !Near(view, a.Cell);
            while (!remove && a.T >= 1)
            {
                a.T -= 1;
                a.Prev = a.Cell;
                if (!TryNext(a, rng, out var next))
                {
                    remove = true;
                    break;
                }

                a.Cell = next;
            }

            if (remove)
            {
                actors.RemoveAt(i);
            }
        }

        while (actors.Count > target)
        {
            actors.RemoveAt(actors.Count - 1);
        }

        // At most one new actor per frame so they trickle in instead of popping in all at once.
        if (actors.Count < target && spawn.Count > 0)
        {
            var at = spawn[rng.Next(spawn.Count)];
            if ((_world.Classify(at.X, at.Y) & AmbientKind.Road) != 0 && view.Contains(at))
            {
                actors.Add(new Walker { Prev = at, Cell = at, T = 0, Speed = minSpeed + rng.NextDouble() * (maxSpeed - minSpeed) });
            }
        }

        foreach (var a in actors)
        {
            Place(a, dt);
        }
    }

    /// <summary>Moves the drawn position towards the road curve beside the actor, which keeps it on the line of an angled road.</summary>
    private void Place(Walker a, double dt)
    {
        var (x, y) = Lerp(a);
        var (tx, ty) = _world.Snap(x, y);
        if (!a.Placed)
        {
            a.X = tx;
            a.Y = ty;
            a.Placed = true;
            return;
        }

        double k = 1 - Math.Exp(-dt * FollowRate);
        a.X += (tx - a.X) * k;
        a.Y += (ty - a.Y) * k;
    }

    private bool TryNext(Walker a, GameRandom rng, out Pos next)
    {
        Span<Pos> options = stackalloc Pos[4];
        int n = 0;
        Pos straight = new(a.Cell.X * 2 - a.Prev.X, a.Cell.Y * 2 - a.Prev.Y);
        bool canStraight = false;
        foreach (var p in new[] { a.Cell.Offset(1, 0), a.Cell.Offset(0, 1), a.Cell.Offset(-1, 0), a.Cell.Offset(0, -1) })
        {
            if (p == a.Prev || (_world.Classify(p.X, p.Y) & AmbientKind.Road) == 0)
            {
                continue;
            }

            options[n++] = p;
            canStraight |= p == straight;
        }

        if (n == 0)
        {
            // Dead end: turn around if the way back is still a road, otherwise the actor is gone.
            next = a.Prev;
            return a.Prev != a.Cell && (_world.Classify(a.Prev.X, a.Prev.Y) & AmbientKind.Road) != 0;
        }

        next = canStraight && rng.Chance(0.75) ? straight : options[rng.Next(n)];
        return true;
    }

    private void StepBirds(double dt, CellRect view, int target, GameRandom rng)
    {
        for (int i = _birds.Count - 1; i >= 0; i--)
        {
            var b = _birds[i];
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            if (b.X < view.X - 4 || b.X > view.Right + 5 || b.Y < view.Y - 4 || b.Y > view.Bottom + 5)
            {
                _birds.RemoveAt(i);
            }
        }

        while (_birds.Count > target)
        {
            _birds.RemoveAt(_birds.Count - 1);
        }

        if (_birds.Count < target && rng.Chance(Math.Min(1, dt * 0.5)))
        {
            bool fromLeft = rng.Chance(0.5);
            double speed = BirdSpeed.Min + rng.NextDouble() * (BirdSpeed.Max - BirdSpeed.Min);
            _birds.Add(new Bird
            {
                X = fromLeft ? view.X - 2 : view.Right + 3,
                Y = view.Y + rng.NextDouble() * view.Height,
                Vx = fromLeft ? speed : -speed,
                Vy = (rng.NextDouble() - 0.5) * 0.3,
                Phase = rng.NextDouble() * 6,
            });
        }
    }

    private void StepWater(double dt, CellRect view, int target, GameRandom rng)
    {
        for (int i = _splashes.Count - 1; i >= 0; i--)
        {
            var s = _splashes[i];
            s.Age += dt;
            if (s.Age >= s.Life || !Near(view, s.Cell))
            {
                _splashes.RemoveAt(i);
            }
        }

        while (_splashes.Count > target)
        {
            _splashes.RemoveAt(_splashes.Count - 1);
        }

        if (_splashes.Count >= target || !rng.Chance(Math.Min(1, dt * 0.6)))
        {
            return;
        }

        // Pick the water first, so a wide sea in view does not crowd out the lakes, bays and rivers: whales need open
        // sea, bubbles rise anywhere, and fish leap everywhere.
        bool sea = _sea.Count > 0 && (_water.Count == 0 || rng.Chance(0.5));
        var list = sea ? _sea : _water;
        if (list.Count == 0)
        {
            return;
        }

        double roll = rng.NextDouble();
        var splashKind = sea
            ? roll < 0.4 ? SplashKind.Whale : roll < 0.8 ? SplashKind.Fish : SplashKind.Bubbles
            : roll < 0.6 ? SplashKind.Fish : SplashKind.Bubbles;
        var at = list[rng.Next(list.Count)];
        var kind = _world.Classify(at.X, at.Y);
        if (!view.Contains(at) || (kind & AmbientKind.Water) == 0 || (splashKind == SplashKind.Whale && (kind & AmbientKind.Sea) == 0))
        {
            return;
        }

        // A fish leaps along the water, so it needs a water cell on the side it is heading for.
        double dir = rng.Chance(0.5) ? 1 : -1;
        if (splashKind == SplashKind.Fish && (_world.Classify(at.X + (int)dir, at.Y) & AmbientKind.Water) == 0)
        {
            dir = -dir;
            if ((_world.Classify(at.X + (int)dir, at.Y) & AmbientKind.Water) == 0)
            {
                return;
            }
        }

        double life = splashKind switch { SplashKind.Whale => 4.0, SplashKind.Bubbles => 2.4, _ => 1.1 };
        _splashes.Add(new Splash { Cell = at, Dir = dir, Kind = splashKind, Life = life });
    }

    private static void DrawSplash(EffectSink sink, Splash s)
    {
        double t = s.Age / s.Life;
        double cx = s.Cell.X + 0.5, cy = s.Cell.Y + 0.5;
        if (s.Whale)
        {
            // Rises, blows twice and slips under again, leaving rings on the water.
            double rise = Easing.Envelope(t, 0.2, 0.3);
            sink.AddSprite(new EffectSprite(EffectGlyphs.Ripple[1], (float)cx, (float)(cy + 0.15), EffectGlyphs.WaterColor, (float)(1.0 + 1.2 * t), (float)(0.7 * rise)));
            sink.AddSprite(new EffectSprite(EffectGlyphs.WhaleBack, (float)cx, (float)(cy - 0.1 * rise), EffectGlyphs.WhaleColor, 2.1f, (float)Math.Min(1, rise * 1.4)));
            for (int k = 0; k < 2; k++)
            {
                double u = (t - 0.25 - 0.18 * k) / 0.3;
                if (u > 0 && u < 1)
                {
                    sink.AddSprite(new EffectSprite(
                        EffectGlyphs.Spout[k], (float)(cx + (k == 0 ? -0.12 : 0.12) * u), (float)(cy - 0.5 - 1.3 * u),
                        EffectGlyphs.SpoutColor, (float)(0.8 + 0.4 * u), (float)(0.9 * (1 - u))));
                }
            }

            return;
        }

        if (s.Kind == SplashKind.Bubbles)
        {
            // A few bubbles drift up from below the surface one after another, and each pops in a small ring.
            for (int k = 0; k < 3; k++)
            {
                double u = (t - 0.12 * k) / 0.6;
                if (u <= 0 || u >= 1)
                {
                    continue;
                }

                double bx = cx + (k - 1) * 0.28 + 0.1 * Math.Sin((u + k) * 5);
                sink.AddSprite(new EffectSprite(
                    EffectGlyphs.Bubble[Math.Min(k, EffectGlyphs.Bubble.Length - 1)], (float)bx, (float)(cy + 0.2 - 0.8 * u),
                    EffectGlyphs.BubbleColor, (float)(0.7 + 0.25 * u), (float)(0.9 * Easing.Envelope(u, 0.2, 0.3))));
            }

            double pop = (t - 0.65) / 0.3;
            if (pop > 0 && pop < 1)
            {
                sink.AddSprite(new EffectSprite(EffectGlyphs.Ripple[1], (float)cx, (float)(cy - 0.5), EffectGlyphs.WaterColor, (float)(0.6 + 0.6 * pop), (float)(0.8 * (1 - pop))));
            }

            return;
        }

        double y = cy - 1.2 * 4 * t * (1 - t);
        double x = cx + s.Dir * (t - 0.5) * 1.2;
        sink.AddSprite(new EffectSprite(s.Dir > 0 ? EffectGlyphs.FishRight : EffectGlyphs.FishLeft, (float)x, (float)y, EffectGlyphs.FishColor, 1.15f, 1f));
        foreach (double edge in new[] { 0.0, 1.0 })
        {
            // Rings spread where the fish leaves the water and where it re-enters.
            double since = edge == 0 ? t : t - 0.85;
            if (since < 0 || since > 0.3)
            {
                continue;
            }

            double u = since / 0.3;
            sink.AddSprite(new EffectSprite(
                EffectGlyphs.Ripple[1], (float)(cx + s.Dir * (edge - 0.5) * 1.2), (float)(cy + 0.15), EffectGlyphs.WaterColor,
                (float)(0.6 + 0.6 * u), (float)(0.8 * (1 - u))));
        }
    }

    private static bool Near(CellRect view, Pos p) =>
        p.X >= view.X - 3 && p.X <= view.Right + 3 && p.Y >= view.Y - 3 && p.Y <= view.Bottom + 3;

    protected internal override void Contribute(EffectSink sink)
    {
        foreach (var car in _cars)
        {
            sink.AddSprite(new EffectSprite(EffectGlyphs.Car, (float)car.X, (float)car.Y, EffectGlyphs.CarColor, 0.7f, 0.95f));
        }

        foreach (var person in _people)
        {
            sink.AddSprite(new EffectSprite(EffectGlyphs.Person, (float)person.X, (float)person.Y, EffectGlyphs.PersonColor, 1f, 0.9f));
        }

        foreach (var bird in _birds)
        {
            int frame = (int)((_birdClock * 3 + bird.Phase) % 2);
            sink.AddSprite(new EffectSprite(EffectGlyphs.Bird[frame], (float)bird.X, (float)bird.Y, EffectGlyphs.BirdColor, 0.8f, 0.8f));
        }

        foreach (var splash in _splashes)
        {
            DrawSplash(sink, splash);
        }

        for (int s = 0; s < _factories.Count; s++)
        {
            var f = _factories[s];
            for (int k = 0; k < SmokePuffs; k++)
            {
                double t = ((_clock + EffectRandom.Unit(_smokeSeed, f.X, f.Y) * SmokeLife) / SmokeLife + (double)k / SmokePuffs) % 1;
                string glyph = EffectGlyphs.Smoke[EffectRandom.Pick(_smokeSeed, EffectGlyphs.Smoke.Length, f.X, f.Y, k)];
                sink.AddSprite(new EffectSprite(
                    glyph, (float)(f.X + 0.7 + 0.35 * t + 0.1 * Math.Sin(t * 9 + k)), (float)(f.Y + 0.1 - 1.6 * t),
                    EffectGlyphs.SmokeColor, (float)(0.5 + 0.7 * t), (float)(0.7 * Easing.Envelope(t, 0.15, 0.5))));
            }
        }
    }

    private static (double X, double Y) Lerp(Walker w) =>
        (Easing.Lerp(w.Prev.X, w.Cell.X, w.T) + 0.5, Easing.Lerp(w.Prev.Y, w.Cell.Y, w.T) + 0.5);
}
