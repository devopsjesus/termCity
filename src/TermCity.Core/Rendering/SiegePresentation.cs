using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public sealed class SiegePresentation
{
    private CityGame? _game;
    private SettlementState? _source;
    private int _revision = -1;
    private double _age, _interval = .1, _sinceCommit, _seconds;
    public SettlementState? Current { get; private set; }
    public double Fraction => Current?.InterpolationFraction ?? 0;
    public bool IsCurrent(CityGame game) => ReferenceEquals(_game, game) && ReferenceEquals(_source, game.Settlement);

    public void Reset()
    {
        _game = null;
        _source = null;
        Current = null;
    }

    public void Advance(CityGame game, double elapsedSeconds, bool running)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        var source = game.Settlement;
        if (!ReferenceEquals(_game, game) || !ReferenceEquals(_source, source))
        {
            _game = game;
            _source = source;
            Current = source?.ForkForPresentation(game);
            _revision = source?.Revision ?? -1;
            _age = _sinceCommit = 0;
            _seconds = source?.VisualSeconds ?? 0;
            _interval = TickInterval(game);
            _age = (source?.InterpolationFraction ?? 0) * _interval;
        }
        if (source is null || Current is null) return;
        if (running)
        {
            _age += elapsedSeconds;
            _sinceCommit += elapsedSeconds;
            _seconds += elapsedSeconds * game.Config.MediumSecondsPerWeek / game.Config.SecondsPerWeek(game.Speed);
        }
        if (_revision != source.Revision)
        {
            double fraction = Math.Clamp(_age / _interval, 0, 1);
            var enemies = Current.Enemies.ToDictionary(enemy => enemy.Id, enemy =>
                (X: enemy.PreviousX + (enemy.X - enemy.PreviousX) * fraction,
                 Y: enemy.PreviousY + (enemy.Y - enemy.PreviousY) * fraction,
                 Distance: enemy.PreviousWalkDistance + (enemy.WalkDistance - enemy.PreviousWalkDistance) * fraction,
                 OrcAttacking: enemy.OrcAttacking,
                 OrcTime: SiegeMotion.OrcAnimationTime(enemy, fraction)));
            var defenders = Current.Defenders.ToDictionary(unit => unit.Id, unit =>
                (X: unit.PreviousX + (unit.X - unit.PreviousX) * fraction,
                 Y: unit.PreviousY + (unit.Y - unit.PreviousY) * fraction,
                 Height: unit.PreviousElevation + (unit.Elevation - unit.PreviousElevation) * (float)fraction));
            Current.CopyCombatState(game, source);
            foreach (var enemy in Current.Enemies)
                if (running && enemies.TryGetValue(enemy.Id, out var pose))
                {
                    enemy.PreviousX = pose.X;
                    enemy.PreviousY = pose.Y;
                    enemy.PreviousWalkDistance = pose.Distance;
                    if (pose.OrcAttacking && enemy.OrcAttacking)
                        enemy.PreviousOrcAttackSeconds = pose.OrcTime;
                }
            foreach (var unit in Current.Defenders)
                if (running && defenders.TryGetValue(unit.Id, out var pose))
                {
                    unit.PreviousX = pose.X;
                    unit.PreviousY = pose.Y;
                    unit.PreviousElevation = pose.Height;
                }
            double expected = TickInterval(game);
            _interval = Math.Clamp(_interval * .5 + _sinceCommit * .5, expected, Math.Max(expected, .25));
            _revision = source.Revision;
            _age = running ? 0 : source.InterpolationFraction * _interval;
            _sinceCommit = 0;
        }
        Current.PresentationFraction = Math.Clamp(_age / _interval, 0, 1);
        Current.PresentationSeconds = _seconds;
    }

    private static double TickInterval(CityGame game) =>
        SettlementState.StepSeconds * game.Config.SecondsPerWeek(game.Speed) / game.Config.MediumSecondsPerWeek;
}
