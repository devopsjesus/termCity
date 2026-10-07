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
}

/// <summary>What ambient life may ask about the map. Out-of-bounds cells must return <see cref="AmbientKind.None"/>.</summary>
public interface IAmbientWorld
{
    AmbientKind Classify(int x, int y);
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

    private readonly IAmbientWorld _world;
    private readonly List<Walker> _cars = [];
    private readonly List<Walker> _people = [];
    private readonly List<Bird> _birds = [];
    private readonly List<Pos> _roads = [];
    private readonly List<Pos> _homeRoads = [];
    private readonly List<Pos> _factories = [];
    private CellRect? _lastView;
    private double _lastScan;
    private double _clock;
    private int _smokeSeed;

    private sealed class Walker
    {
        public Pos Prev, Cell;
        public double T, Speed;
    }

    private sealed class Bird
    {
        public double X, Y, Vx, Vy, Phase;
    }

    public AmbientLife(IAmbientWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    public override double Duration => 1;

    public override bool IsPersistent => true;

    public override bool IsAmbient => true;

    public override int Priority => -1;

    public int Cars => _cars.Count;

    public int People => _people.Count;

    public int Birds => _birds.Count;

    public int SmokeSources => _factories.Count;

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
            Scan(view);
        }

        int budget = Budget(system.Settings);
        int carTarget = Math.Min(budget * 35 / 100, _roads.Count / 6);
        int peopleTarget = Math.Min(budget * 25 / 100, _homeRoads.Count / 4);
        int birdTarget = budget >= 10 ? Math.Max(1, budget / 10) : 0;
        var rng = system.Random;
        Step(_cars, dt, view, carTarget, _roads, rng, 3.0, 5.0);
        Step(_people, dt, view, peopleTarget, _homeRoads, rng, 0.7, 1.2);
        StepBirds(dt, view, birdTarget, rng);
    }

    private void Scan(CellRect view)
    {
        _roads.Clear();
        _homeRoads.Clear();
        _factories.Clear();
        int stride = view.Area > ScanLimit ? (int)Math.Ceiling(Math.Sqrt(view.Area / (double)ScanLimit)) : 1;
        for (int y = view.Y; y <= view.Bottom; y += stride)
        {
            for (int x = view.X; x <= view.Right; x += stride)
            {
                var kind = _world.Classify(x, y);
                if ((kind & AmbientKind.Road) != 0)
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
            double speed = 2 + rng.NextDouble() * 2;
            _birds.Add(new Bird
            {
                X = fromLeft ? view.X - 2 : view.Right + 3,
                Y = view.Y + rng.NextDouble() * view.Height,
                Vx = fromLeft ? speed : -speed,
                Vy = (rng.NextDouble() - 0.5) * 0.6,
                Phase = rng.NextDouble() * 6,
            });
        }
    }

    private static bool Near(CellRect view, Pos p) =>
        p.X >= view.X - 3 && p.X <= view.Right + 3 && p.Y >= view.Y - 3 && p.Y <= view.Bottom + 3;

    protected internal override void Contribute(EffectSink sink)
    {
        foreach (var car in _cars)
        {
            var (x, y) = Lerp(car);
            sink.AddSprite(new EffectSprite(EffectGlyphs.Car, (float)x, (float)y, EffectGlyphs.CarColor, 0.7f, 0.95f));
        }

        foreach (var person in _people)
        {
            var (x, y) = Lerp(person);
            sink.AddSprite(new EffectSprite(EffectGlyphs.Person, (float)x, (float)y, EffectGlyphs.PersonColor, 1f, 0.9f));
        }

        foreach (var bird in _birds)
        {
            int frame = (int)((_clock * 3 + bird.Phase) % 2);
            sink.AddSprite(new EffectSprite(EffectGlyphs.Bird[frame], (float)bird.X, (float)bird.Y, EffectGlyphs.BirdColor, 0.8f, 0.8f));
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
