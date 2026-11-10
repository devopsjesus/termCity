using TermCity.Core.Util;

namespace TermCity.Core.Simulation;

internal sealed record SavedDefender(int Id, Pos Home, DefenderKind Kind, double X, double Y,
    float Elevation, double Health, DefenderOrder Order, int? TargetEnemyId, Pos? Destination,
    double AttackCooldown, List<Pos> Path, int PathIndex, Pos? PathTarget, int? PatrolWallId = null);
internal sealed record SavedRoster(Pos Home, double Seconds);

internal sealed class SavedGarrison
{
    public int NextId { get; set; } = 1;
    public List<SavedDefender> Units { get; set; } = [];
    public List<SavedRoster> Rosters { get; set; } = [];

    public static SavedGarrison Capture(SettlementState state) => new()
    {
        NextId = state.NextDefenderId,
        Units = state.Defenders.Where(d => d.Health > 0 && state.GarrisonHomeExists(d.Home)).Select(d => new SavedDefender(d.Id, d.Home, d.Kind,
            d.X, d.Y, d.Elevation, d.Health, d.Order, d.TargetEnemyId, d.Destination, d.AttackCooldown,
            new(d.Path), d.PathIndex, d.PathTarget, d.PatrolWallId)).ToList(),
        Rosters = state.RosterTimers.Where(p => state.GarrisonHomeExists(p.Key)).OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X)
            .Select(p => new SavedRoster(p.Key, p.Value)).ToList(),
    };

    public void Restore(CityGame game, SettlementState state)
    {
        state.BindGarrisonGame(game);
        static bool Range(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;
        static void Require(bool valid)
        {
            if (!valid) throw new InvalidDataException("Invalid garrison units, orders or recruitment state.");
        }
        bool Home(Pos p) => game.Map.InBounds(p) && state.EstablishedAt(p) &&
            game.Map.BuildingAt(p.X, p.Y) is { } building && SettlementState.RosterCapacity(building) > 0 &&
            game.Map.BuildingAnchor(game.Map.Index(p.X, p.Y)) == game.Map.Index(p.X, p.Y);
        Require(NextId > 0 && Units is not null && Units.Count <= SettlementState.MaximumDefenders &&
            Rosters is not null && Rosters.Count <= game.Map.BuildingFootprints.Count);
        var ids = new HashSet<int>();
        foreach (var unit in Units!)
        {
            Require(unit is not null && unit.Id > 0 && unit.Id < NextId && ids.Add(unit.Id) &&
                Home(unit.Home) && Enum.IsDefined(unit.Kind) && Enum.IsDefined(unit.Order) &&
                Range(unit.X, 0, game.Map.Width - 0.00001) && Range(unit.Y, 0, game.Map.Height - 0.00001) &&
                Range(unit.Elevation, 0, 10) && Range(unit.Health, double.Epsilon, 160) &&
                Range(unit.AttackCooldown, 0, CombatProfiles.Fighter(unit.Kind).Interval) &&
                unit.Path is not null && unit.Path.Count <= game.Map.Width * game.Map.Height &&
                unit.PathIndex >= 0 && unit.PathIndex <= unit.Path.Count &&
                (unit.PatrolWallId is null || state.Walls.Any(w => w.Id == unit.PatrolWallId)));
            var expectedKind = game.Map.BuildingAt(unit!.Home.X, unit.Home.Y)!.Name == "Archer Barracks"
                ? DefenderKind.Archer : DefenderKind.Soldier;
            Require(unit.Kind == expectedKind && state.SavedDefenderCellValid(game.Map, unit.Kind, new((int)unit.X, (int)unit.Y)) &&
                (unit.Destination is not { } destination || game.Map.InBounds(destination)) &&
                (unit.PathTarget is not { } pathTarget || game.Map.InBounds(pathTarget)) &&
                (unit.Order != DefenderOrder.Move || unit.Destination is not null) &&
                (unit.Order != DefenderOrder.Attack || unit.TargetEnemyId is > 0));
            for (int i = 0; i < unit.Path!.Count; i++)
                Require(state.SavedDefenderCellValid(game.Map, unit.Kind, unit.Path[i]) &&
                    (i == 0 || Math.Abs(unit.Path[i].X - unit.Path[i - 1].X) + Math.Abs(unit.Path[i].Y - unit.Path[i - 1].Y) == 1));
            bool stale = unit.TargetEnemyId is { } target && !state.Enemies.Any(g => g.Id == target);
            state.DefenderList.Add(new SettlementDefender
            {
                Id = unit.Id, Home = unit.Home, Kind = unit.Kind, X = unit.X, Y = unit.Y,
                PreviousX = unit.X, PreviousY = unit.Y, Elevation = unit.Elevation, PreviousElevation = unit.Elevation,
                Health = unit.Health, Order = stale ? DefenderOrder.Automatic : unit.Order,
                TargetEnemyId = stale ? null : unit.TargetEnemyId, Destination = unit.Destination,
                AttackCooldown = unit.AttackCooldown, Path = new(unit.Path), PathIndex = unit.PathIndex,
                PathTarget = unit.PathTarget,
                PatrolWallId = unit.PatrolWallId,
            });
        }
        foreach (var group in state.Defenders.GroupBy(d => d.Home))
            Require(group.Count() <= SettlementState.RosterCapacity(game.Map.BuildingAt(group.Key.X, group.Key.Y)!));
        foreach (var timer in Rosters!)
        {
            Require(timer is not null && Home(timer.Home) && Range(timer.Seconds, 0, 18) &&
                !state.RosterTimers.ContainsKey(timer.Home));
            state.RosterTimers.Add(timer!.Home, timer.Seconds);
        }
        state.NextDefenderId = NextId;
    }
}
