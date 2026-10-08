using TermCity.Core.Util;

namespace TermCity.Core.Effects;

/// <summary>
/// Owns the live effects, advances them on a clock and answers per-cell and sprite queries for the renderer.
/// It knows nothing about the game or any UI toolkit: callers spawn effects (usually through
/// <see cref="EffectDirector"/>), call <see cref="Update"/> once per frame and then read
/// <see cref="CellAt"/>, <see cref="Sprites"/> and <see cref="Shake"/>.
/// </summary>
public sealed class EffectSystem
{
    /// <summary>Longest time step honoured by <see cref="Update"/>; a stalled frame never makes effects jump ahead.</summary>
    public const double MaxStep = 0.25;

    private const int MaxScheduled = 256;

    private readonly List<Effect> _effects = [];
    private readonly List<Scheduled> _scheduled = [];
    private readonly EffectSink _sink;
    private readonly GameRandom _rng;
    private long _sequence;
    private bool _hadVisuals;

    private sealed record Scheduled(double At, long Order, Action<EffectSystem> Callback);

    public EffectSystem(int seed = 0, EffectSettings? settings = null)
    {
        Seed = seed;
        Settings = settings ?? new EffectSettings();
        _rng = new GameRandom(seed);
        _sink = new EffectSink(Settings);
    }

    public int Seed { get; }

    public EffectSettings Settings { get; }

    /// <summary>Seconds of effect time elapsed (sum of the clamped update steps).</summary>
    public double Time { get; private set; }

    /// <summary>The part of the map currently on screen, in map cells. Ambient life only lives inside it.</summary>
    public CellRect? View { get; set; }

    /// <summary>Live effects, ambient life included.</summary>
    public int ActiveEffects => _effects.Count;

    /// <summary>Live effects excluding ambient life; zero once every one-shot effect has finished.</summary>
    public int ActiveOneShots => _effects.Count(e => !e.IsAmbient);

    public int PendingCallbacks => _scheduled.Count;

    /// <summary>Spawns that were refused (disabled, or no room and nothing of lower priority to evict).</summary>
    public int Rejected { get; private set; }

    /// <summary>Effects evicted to make room for a newer one.</summary>
    public int Evicted { get; private set; }

    /// <summary>Contributions dropped in the last frame build because a budget was full.</summary>
    public int DroppedLastFrame => _sink.Dropped;

    public int CellCount => Settings.Active ? _sink.CellCount : 0;

    public int SpriteCount => Settings.Active ? _sink.SpriteCount : 0;

    public bool HasVisuals => Settings.Active && (_sink.CellCount > 0 || _sink.SpriteCount > 0 || _sink.Shake != default);

    /// <summary>True when the last update changed what should be on screen (including everything vanishing).</summary>
    public bool NeedsRedraw { get; private set; }

    /// <summary>Whole-view shake offset in cell units (zero when idle).</summary>
    public (float X, float Y) Shake => Settings.Active ? _sink.Shake : default;

    public IReadOnlyList<EffectSprite> Sprites => Settings.Active ? _sink.Sprites : [];

    public IEnumerable<Effect> Effects => _effects;

    /// <summary>A reproducible seed for a new effect, drawn from this system's seeded stream.</summary>
    public int NextSeed() => (int)(_rng.NextULong() & 0x7fffffff);

    /// <summary>The seeded stream shared by everything that needs a stateful random choice.</summary>
    public GameRandom Random => _rng;

    /// <summary>What the effects currently do to a map cell (<see cref="CellEffect.Identity"/> when untouched).</summary>
    public CellEffect CellAt(int x, int y) =>
        Settings.Active && _sink.TryGet(x, y, out var effect) ? effect : CellEffect.Identity;

    public bool TryGetCell(int x, int y, out CellEffect effect)
    {
        if (Settings.Active && _sink.TryGet(x, y, out effect))
        {
            return true;
        }

        effect = CellEffect.Identity;
        return false;
    }

    /// <summary>Every cell currently modified, as map coordinates.</summary>
    public IEnumerable<(int X, int Y, CellEffect Effect)> ActiveCells()
    {
        if (!Settings.Active)
        {
            yield break;
        }

        foreach (var (key, effect) in _sink.Cells)
        {
            yield return (key & 0xffff, key >> 16, effect);
        }
    }

    /// <summary>
    /// Starts an effect after <paramref name="delay"/> seconds. Returns false when it was refused: effects are off, or
    /// the live-effect cap is full of effects with higher priority.
    /// </summary>
    public bool Spawn(Effect effect, double delay = 0)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (!double.IsFinite(delay) || delay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be finite and non-negative.");
        }

        if (effect.Sequence != 0)
        {
            throw new InvalidOperationException("An effect can only be spawned once.");
        }

        if (!Settings.Active)
        {
            Rejected++;
            return false;
        }

        if (!effect.IsAmbient && ActiveOneShots >= Settings.MaxEffects && !EvictFor(effect))
        {
            Rejected++;
            return false;
        }

        effect.Sequence = ++_sequence;
        effect.Delay = delay;
        effect.Age = 0;
        int index = _effects.FindIndex(e => Order(effect, e) < 0);
        _effects.Insert(index < 0 ? _effects.Count : index, effect);
        return true;
    }

    /// <summary>Runs <paramref name="callback"/> when the effect clock reaches now + <paramref name="delay"/> (sequencing).</summary>
    public bool Schedule(double delay, Action<EffectSystem> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!double.IsFinite(delay) || delay < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be finite and non-negative.");
        }

        if (!Settings.Active || _scheduled.Count >= MaxScheduled)
        {
            Rejected++;
            return false;
        }

        _scheduled.Add(new Scheduled(Time + delay, ++_sequence, callback));
        return true;
    }

    /// <summary>Advances the clock, runs due callbacks, retires finished effects and rebuilds the frame.</summary>
    public void Update(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), "Effect delta must be finite and non-negative.");
        }

        if (!Settings.Active)
        {
            NeedsRedraw = _hadVisuals;
            _hadVisuals = false;
            Clear();
            return;
        }

        dt = Math.Min(dt, MaxStep);
        Time += dt;
        RunDueCallbacks();
        foreach (var effect in _effects)
        {
            effect.Age += dt;
            if (effect.HasStarted)
            {
                effect.OnAdvance(dt, this);
            }
        }

        _effects.RemoveAll(e => e.IsFinished);
        Rebuild();
    }

    /// <summary>Removes every effect and callback immediately and empties the frame.</summary>
    public void Clear()
    {
        _effects.Clear();
        _scheduled.Clear();
        _sink.Reset();
    }

    /// <summary>Removes only ambient life (for example before changing city).</summary>
    public void ClearAmbient()
    {
        _effects.RemoveAll(e => e.IsAmbient);
        Rebuild();
    }

    public void ClearCelebrations()
    {
        _effects.RemoveAll(effect => effect is ConfettiEffect);
        Rebuild();
    }

    private void RunDueCallbacks()
    {
        while (true)
        {
            Scheduled? next = null;
            foreach (var item in _scheduled)
            {
                if (item.At <= Time && (next is null || (item.At, item.Order).CompareTo((next.At, next.Order)) < 0))
                {
                    next = item;
                }
            }

            if (next is null)
            {
                return;
            }

            _scheduled.Remove(next);
            next.Callback(this);
        }
    }

    private void Rebuild()
    {
        _sink.Reset();
        foreach (var effect in _effects)
        {
            if (!effect.HasStarted)
            {
                continue;
            }

            _sink.CurrentIsAmbient = effect.IsAmbient;
            effect.Contribute(_sink);
        }

        _sink.CurrentIsAmbient = false;
        bool visuals = _sink.CellCount > 0 || _sink.SpriteCount > 0 || _sink.Shake != default;
        NeedsRedraw = visuals || _hadVisuals;
        _hadVisuals = visuals;
    }

    // Higher priority first; among equals the newest first, so budgets favour what just happened.
    private static int Order(Effect a, Effect b) =>
        a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : b.Sequence.CompareTo(a.Sequence);

    private bool EvictFor(Effect incoming)
    {
        Effect? victim = null;
        foreach (var effect in _effects)
        {
            if (effect.IsAmbient || effect.Priority > incoming.Priority)
            {
                continue;
            }

            if (victim is null || effect.Priority < victim.Priority ||
                effect.Priority == victim.Priority && effect.Sequence < victim.Sequence)
            {
                victim = effect;
            }
        }

        if (victim is null)
        {
            return false;
        }

        _effects.Remove(victim);
        Evicted++;
        return true;
    }
}
