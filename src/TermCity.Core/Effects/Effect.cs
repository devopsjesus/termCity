namespace TermCity.Core.Effects;

/// <summary>
/// A timed visual. Most effects are pure functions of their <see cref="LocalTime"/>: <see cref="Contribute"/> is asked
/// "what do you look like now?" every frame and nothing is integrated, so effects are deterministic, trivially
/// testable and cannot drift. A few (ambient life) keep small state in <see cref="OnAdvance"/>.
/// </summary>
public abstract class Effect
{
    /// <summary>Seconds since the effect was spawned, including its start delay.</summary>
    public double Age { get; internal set; }

    /// <summary>Seconds after spawning before the effect starts to show.</summary>
    public double Delay { get; internal set; }

    /// <summary>Order the system spawned it in (stable tie-breaker for budgets and eviction).</summary>
    public long Sequence { get; internal set; }

    /// <summary>Seconds the effect shows for once it starts. Persistent effects ignore this.</summary>
    public abstract double Duration { get; }

    /// <summary>Higher priority effects survive budget pressure and are built into the frame first.</summary>
    public virtual int Priority => 0;

    /// <summary>Persistent effects never finish by themselves (ambient life); they are removed with the system.</summary>
    public virtual bool IsPersistent => false;

    /// <summary>Ambient effects use their own sprite budget and do not count towards the live-effect cap.</summary>
    public virtual bool IsAmbient => false;

    public virtual string Name => GetType().Name;

    public bool HasStarted => Age >= Delay;

    public double LocalTime => Math.Max(0, Age - Delay);

    /// <summary>0..1 through the effect's duration (0 before it starts).</summary>
    public double Progress => Duration <= 0 ? 1 : Easing.Clamp01(LocalTime / Duration);

    public bool IsFinished => !IsPersistent && Age >= Delay + Duration;

    /// <summary>Called once per update after the clock advanced, only for effects that have started.</summary>
    protected internal virtual void OnAdvance(double dt, EffectSystem system)
    {
    }

    /// <summary>Writes this effect's cell modifiers and sprites for the current time.</summary>
    protected internal abstract void Contribute(EffectSink sink);
}
