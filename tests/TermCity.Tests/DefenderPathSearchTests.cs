using System.Numerics;
using System.Reflection;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DefenderPathSearchTests
{
    [Fact]
    public void WarmSearchesDoNotAllocatePerVisitedEdge()
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 12.5, Y = 12.5 };
        state.DefenderList.Add(unit);
        state.DefenderList.Add(new SettlementDefender { Id = 2, X = 20.5, Y = 20.5 });
        Pos[] goals = [new(35, 35)];
        for (int i = 0; i < 4; i++)
        {
            using var warm = state.DefenderPaths(game, unit, goals, stayInside: true);
            Assert.True(warm.CanReach(goals[0]));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int visited = 0;
        for (int i = 0; i < 8; i++)
        {
            using var paths = state.DefenderPaths(game, unit, goals, stayInside: true);
            Assert.True(paths.CanReach(goals[0]));
            visited += paths.VisitedCount;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(visited > 3000, $"The allocation fixture visited only {visited} cells.");
        Assert.True(bytes < 64 * 1024, $"Warm searches allocated {bytes} bytes for {visited} visited cells.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedGuardSearchMatchesIndependentTargetSearches(bool blocked, bool retaining)
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 15.5, Y = 15.5 };
        state.DefenderList.AddRange([unit, new() { Id = 2, X = 17.5, Y = 15.5 }]);
        state.EnemyList.AddRange([
            new() { Id = 1, X = 25.5, Y = 15.5, Health = 96, MaxHealth = 96 },
            new() { Id = 2, X = 16.5, Y = 27.5, Health = 96, MaxHealth = 96 },
            new() { Id = 3, X = 17.5, Y = 15.5, Health = 96, MaxHealth = 96 },
            new() { Id = 4, X = 45.5, Y = 15.5, Health = 96, MaxHealth = 96 },
        ]);
        if (blocked)
            for (int y = 11; y < 37; y++)
                game.Map.SetTerrain(21, y, game.Map.Content.Terrains.Get("Water"));
        if (retaining)
        {
            unit.TargetEnemyId = 1;
            unit.Path.Add(new(16, 15));
        }
        var ordered = Bind<Func<SettlementDefender, IOrderedEnumerable<SiegeEnemy>>>(state, "DefenseTargets");
        var visible = Bind<Func<CityGame, double, double, double, double, bool>>(state, "GroundFightVisible");
        SiegeEnemy? Expected()
        {
            foreach (var enemy in ordered(unit))
            {
                if (!state.EstablishedAt(new((int)enemy.X, (int)enemy.Y))) continue;
                if (Math.Pow(unit.X - enemy.X, 2) + Math.Pow(unit.Y - enemy.Y, 2) <=
                    Math.Pow(SettlementState.MeleeDistance(enemy), 2) &&
                    visible(game, unit.X, unit.Y, enemy.X, enemy.Y)) return enemy;
                if (unit.TargetEnemyId == enemy.Id && unit.PathIndex < unit.Path.Count) return enemy;
                if (OriginalPath(game, state, unit, new((int)enemy.X, (int)enemy.Y), false,
                    state.EstablishedAt(new((int)unit.X, (int)unit.Y))) is not null) return enemy;
            }
            return null;
        }
        var assign = Bind<Func<CityGame, SettlementDefender, SiegeEnemy?>>(state, "AssignGuardTarget");
        Assert.Same(Expected(), assign(game, unit));
        state.DefenderList[1].X = 16.5;
        state.EnemyList[2].Health = 0;
        Assert.Same(Expected(), assign(game, unit));
    }

    private static T Bind<T>(SettlementState state, string name) where T : Delegate =>
        typeof(SettlementState).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<T>(state);

    [Fact]
    public void InterleavedSearchesKeepTheirOwnPredecessorsAndDisposedResultsCannotReuseStorage()
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 15.5, Y = 15.5 };
        Pos near = new(15, 14), far = new(30, 30);
        using var first = state.DefenderPaths(game, unit, [near, far]);
        Assert.Equal(new[] { near }, first.PathTo(near));
        using (var second = state.DefenderPaths(game, unit, [far]))
            Assert.Equal(OriginalPath(game, state, unit, far, false, false), second.PathTo(far));
        Assert.Equal(OriginalPath(game, state, unit, far, false, false), first.PathTo(far));
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.PathTo(near));
    }

    [Fact]
    public void SearchingANearGoalDoesNotTraverseTheEntireFarGoalRoute()
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 15.5, Y = 15.5 };
        Pos near = new(15, 14), far = new(30, 30);
        using var paths = state.DefenderPaths(game, unit, [near, far]);
        Assert.Equal(new[] { near }, paths.PathTo(near));
        Assert.True(paths.VisitedCount < 10);
        Assert.Equal(OriginalPath(game, state, unit, far, false, false), paths.PathTo(far));
        int visited = paths.VisitedCount;
        Assert.Equal(new[] { near }, paths.PathTo(near));
        Assert.Equal(visited, paths.VisitedCount);
    }

    [Theory]
    [InlineData(0.01, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.99, 0)]
    [InlineData(0.49, 0.31)]
    public void CachedEdgesKeepFractionalBodyBlockingLive(double fraction, double elevation)
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 15.5, Y = 15.5 };
        var other = new SettlementDefender { Id = 2, X = 17 + fraction, Y = 15.5, Elevation = (float)elevation };
        state.DefenderList.AddRange([unit, other]);
        Pos[] goals = [new(17, 15), new(20, 15)];
        ComparePaths(game, state, unit, goals);
        other.X = 16 + fraction;
        ComparePaths(game, state, unit, goals.Reverse().ToArray());
        state.DefenderList.Remove(other);
        ComparePaths(game, state, unit, goals);
    }

    [Fact]
    public void CachedWallRoutesAndRangePostsFollowDamageRepairsMaterialAndEnemyMovement()
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Archer, X = 15.5, Y = 15.5 };
        state.DefenderList.Add(unit);
        var enemy = new SiegeEnemy { Id = 1, X = 18.5, Y = 8.5, Health = 96, MaxHealth = 96, Tier = 1 };
        state.EnemyList.Add(enemy);
        var tower = state.Walls.First(w => w.IsTower);
        Pos[] goals = [tower.Cell, new(18, 10), new(30, 15)];
        foreach (var material in Enum.GetValues<WallMaterial>())
        {
            state.Material = material;
            tower.Health = 0;
            ComparePaths(game, state, unit, goals, stayInside: true);
            CheckAssignment(game, state, unit, null);
            tower.Health = tower.MaxHealth;
            enemy.X += 2;
            ComparePaths(game, state, unit, goals.Reverse().ToArray(), stayInside: true);
            CheckAssignment(game, state, unit, null);
        }
        game.Map.SetTerrain(16, 15, game.Map.Content.Terrains.Get("Water"));
        ComparePaths(game, state, unit, goals, stayInside: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OccupiedGoalsAreEndpointsNotIntermediateCells(bool ignoreBodies, bool detour)
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var water = game.Map.Content.Terrains.Get("Water");
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.SetRoad(x, y, false);
                game.Map.SetTerrain(x, y, water);
            }
        for (int x = 15; x <= 20; x++)
        {
            game.Map.SetTerrain(x, 15, game.Map.Content.Terrains.Base);
            if (detour) game.Map.SetTerrain(x, 14, game.Map.Content.Terrains.Base);
        }
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Soldier, X = 15.5, Y = 15.5 };
        state.DefenderList.Add(unit);
        state.DefenderList.Add(new SettlementDefender { Id = 2, X = 17.5, Y = 15.5 });
        Pos occupied = new(17, 15), behind = new(20, 15);
        Pos[] goals = [occupied, behind, new(15, 15), new(16, 16), new(-1, 15)];
        foreach (bool stayInside in new[] { false, true })
        {
            ComparePaths(game, state, unit, goals, ignoreBodies, stayInside);
            ComparePaths(game, state, unit, goals.Reverse().ToArray(), ignoreBodies, stayInside);
            using var paths = state.DefenderPaths(game, unit, goals, ignoreBodies, stayInside);
            Assert.Equal(new Pos[] { new(16, 15), occupied }, paths.PathTo(occupied));
            var path = paths.PathTo(behind);
            if (!ignoreBodies && !detour) Assert.Null(path);
            else
            {
                Assert.NotNull(path);
                if (!ignoreBodies) Assert.DoesNotContain(occupied, path);
                else Assert.Contains(occupied, path);
            }
        }
    }

    [Theory]
    [InlineData(DefenderKind.Soldier)]
    [InlineData(DefenderKind.Archer)]
    public void StartGoalsAndNeighborTieOrderMatchTheOriginalSearch(DefenderKind kind)
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = kind, X = 15.5, Y = 15.5 };
        Pos[] goals = [new(15, 15), new(17, 13), new(13, 17), new(-1, 0)];
        ComparePaths(game, state, unit, goals);
        Assert.Empty(state.DefenderPaths(game, unit, goals).PathTo(goals[0])!);
        Assert.Equal(new Pos[] { new(15, 14), new(15, 13), new(16, 13), new(17, 13) },
            state.DefenderPaths(game, unit, goals).PathTo(goals[1]));
        unit.X = 15.2;
        ComparePaths(game, state, unit, goals);
        Assert.Equal(new Pos[] { goals[0] }, state.DefenderPaths(game, unit, goals).PathTo(goals[0]));
        Assert.Null(state.DefenderPaths(game, unit, []).PathTo(goals[0]));
        game.Map.SetTerrain(15, 15, game.Map.Content.Terrains.Get("Water"));
        state.DefenderCanReach(game, unit, goals[0]); // Refresh the existing navigation cache.
        ComparePaths(game, state, unit, goals);
        Assert.Null(state.DefenderPaths(game, unit, goals).PathTo(goals[0]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GroundWallAndLadderRoutesMatchTheOriginalSearch(bool ignoreBodies, bool stayInside)
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var tower = state.Walls.First(w => w.IsTower);
        Pos[] goals = [tower.Cell, state.Walls.First(w => !w.IsTower && !w.IsGate && !w.IsGateWing).Cell,
            state.Walls.Single(w => w.IsGate).Cell, new(15, 15), new(8, 15)];
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Archer, X = 15.5, Y = 15.5 };
        var point = WalkPoint(state, tower.Cell, tower);
        state.DefenderList.Add(unit);
        state.DefenderList.Add(new SettlementDefender { Id = 2, Kind = DefenderKind.Archer,
            X = point.X, Y = point.Y, Elevation = WallDamageLayout.ActorWalkHeight(tower, game.Map) });
        ComparePaths(game, state, unit, goals, ignoreBodies, stayInside);
        Assert.NotNull(state.DefenderPaths(game, unit, goals, ignoreBodies, stayInside).PathTo(tower.Cell));
        unit.X = tower.Cell.X + .5; unit.Y = tower.Cell.Y + .5;
        unit.Elevation = WallDamageLayout.ActorWalkHeight(tower, game.Map);
        ComparePaths(game, state, unit, goals.Reverse().ToArray(), ignoreBodies, stayInside);
        unit.Kind = DefenderKind.Soldier;
        unit.X = 15.5; unit.Y = 15.5; unit.Elevation = 0;
        ComparePaths(game, state, unit, goals, ignoreBodies, stayInside);
        Assert.Null(state.DefenderPaths(game, unit, goals, ignoreBodies, stayInside).PathTo(tower.Cell));
    }

    [Fact]
    public void AssignmentPreservesEnemyPostReuseAndShotPriorities()
    {
        var game = TestCity.Flat();
        var state = new SettlementState(new(10, 10, 28, 28));
        var unit = new SettlementDefender { Id = 1, Kind = DefenderKind.Archer, X = 15.5, Y = 15.5 };
        state.DefenderList.Add(unit);
        foreach (var cell in new Pos[] { new(3, 3), new(39, 18), new(18, 8), new(8, 18) })
            state.EnemyList.Add(new SiegeEnemy { Id = state.NextEnemyId++, X = cell.X + .5, Y = cell.Y + .5,
                Health = 96, MaxHealth = 96, Tier = 1 });
        CheckAssignment(game, state, unit, null);
        var expected = OriginalAssignment(game, state, unit, null)!.Value;
        unit.PathEnemyId = expected.Enemy.Id;
        unit.PathTarget = expected.Goal;
        unit.Path = expected.Path!;
        CheckAssignment(game, state, unit, null);
        Assert.Null(OriginalAssignment(game, state, unit, null)!.Value.Path);
        unit.Path = [];
        var tower = state.Walls.First(w => w.IsTower);
        unit.X = tower.Center.X; unit.Y = tower.Center.Y;
        unit.Elevation = unit.PreviousElevation = WallDamageLayout.ActorWalkHeight(tower, game.Map);
        CheckAssignment(game, state, unit, tower);
    }

    private static void CheckAssignment(CityGame game, SettlementState state, SettlementDefender unit,
        WallComponent? support)
    {
        var expected = OriginalAssignment(game, state, unit, support);
        var actual = Bind<Func<CityGame, SettlementDefender, WallComponent?,
            (SiegeEnemy Enemy, Pos? Goal, List<Pos>? Path)?>>(state, "AssignArcherTarget")(game, unit, support);
        Assert.Equal(expected?.Enemy.Id, actual?.Enemy.Id);
        Assert.Equal(expected?.Goal, actual?.Goal);
        Assert.Equal(expected?.Path, actual?.Path);
    }

    private static (SiegeEnemy Enemy, Pos? Goal, List<Pos>? Path)? OriginalAssignment(CityGame game,
        SettlementState state, SettlementDefender unit, WallComponent? support)
    {
        foreach (var enemy in Bind<Func<SettlementDefender, IOrderedEnumerable<SiegeEnemy>>>(state, "DefenseTargets")(unit))
        {
            if (unit.PathEnemyId == enemy.Id && unit.PathIndex < unit.Path.Count && unit.PathTarget is { } goal &&
                Math.Pow(goal.X + .5 - enemy.X, 2) + Math.Pow(goal.Y + .5 - enemy.Y, 2) <=
                Math.Pow(CombatProfiles.Archer.Range, 2)) return (enemy, null, null);
            if (Bind<Func<CityGame, SettlementDefender, WallComponent?, SiegeEnemy, bool>>(state, "ArcherHasShot")(
                game, unit, support, enemy)) return (enemy, null, null);
            foreach (var post in state.Walls.Where(w => Supports(w) && !w.IsGate && !w.IsGateWing)
                .OrderBy(w => Math.Pow(w.Center.X - enemy.X, 2) + Math.Pow(w.Center.Y - enemy.Y, 2) +
                    state.DefenderList.Where(other => other.Id != unit.Id).Sum(other =>
                        80 / (1 + Math.Pow(other.X - w.Center.X, 2) + Math.Pow(other.Y - w.Center.Y, 2))))
                .ThenBy(w => w.Cell.Y).ThenBy(w => w.Cell.X))
            {
                var point = WalkPoint(state, post.Cell, post);
                if (Math.Pow(point.X - enemy.X, 2) + Math.Pow(point.Y - enemy.Y, 2) >
                    Math.Pow(CombatProfiles.Archer.Range, 2)) continue;
                if (state.DefenderList.Any(other => other.Id != unit.Id &&
                    Math.Pow(other.X - post.Cell.X - .5, 2) + Math.Pow(other.Y - post.Cell.Y - .5, 2) <= .12)) continue;
                var path = OriginalPath(game, state, unit, post.Cell, false,
                    unit.Elevation > .01f || state.EstablishedAt(new((int)unit.X, (int)unit.Y)));
                if (path is not null) return (enemy, post.Cell, path);
            }
        }
        return null;
    }

    private static bool Supports(WallComponent wall) => wall.Health > 0 &&
        (!(wall.IsGate || wall.IsGateWing) || WallDamageLayout.DamageBand(wall) < 4);

    private static Vector2 WalkPoint(SettlementState state, Pos cell, WallComponent wall) =>
        typeof(SettlementState).GetMethod("WallWalkPoint", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Pos, WallComponent, Vector2>>()(cell, wall);

    private static void ComparePaths(CityGame game, SettlementState state, SettlementDefender unit,
        Pos[] goals, bool ignoreBodies = false, bool stayInside = false)
    {
        using var paths = state.DefenderPaths(game, unit, goals, ignoreBodies, stayInside);
        foreach (var goal in goals)
        {
            var expected = OriginalPath(game, state, unit, goal, ignoreBodies, stayInside);
            Assert.Equal(expected, paths.PathTo(goal));
            Assert.Equal(expected, state.DefenderPath(game, unit, goal, ignoreBodies, stayInside));
        }
    }

    // Frozen single-target BFS oracle: independent of the extracted traversal and its wrapper.
    private static List<Pos>? OriginalPath(CityGame game, SettlementState state, SettlementDefender unit,
        Pos target, bool ignoreBodies, bool stayInside)
    {
        if (!game.Map.InBounds(target)) return null;
        var groundPassable = Bind<Func<CityGame, Pos, bool>>(state, "GroundPassable");
        var wallSupport = Bind<Func<Pos, WallComponent?>>(state, "WallWalkSupport");
        var joined = Bind<Func<Pos, WallComponent, Pos, WallComponent, bool>>(state, "JoinedWalkway");
        var cells = new Dictionary<Pos, (bool Ground, WallComponent? Support, bool Passable)>();
        (bool Ground, WallComponent? Support, bool Passable) Cell(Pos p)
        {
            if (cells.TryGetValue(p, out var cached)) return cached;
            bool ground = groundPassable(game, p);
            var support = unit.Kind == DefenderKind.Archer ? wallSupport(p) : null;
            return cells[p] = (ground, support, ground || support is not null);
        }
        if (!Cell(target).Passable) return null;
        var start = new Pos((int)unit.X, (int)unit.Y);
        if (start == target)
            return Math.Abs(unit.X - target.X - .5) + Math.Abs(unit.Y - target.Y - .5) < .01 ? [] : [target];
        var first = (Cell: start, OnWall: unit.Kind == DefenderKind.Archer && unit.Elevation > .01f &&
            Cell(start).Support is not null);
        var previous = new Dictionary<(Pos Cell, bool OnWall), (Pos Cell, bool OnWall)>();
        var visited = new HashSet<(Pos Cell, bool OnWall)> { first };
        var queue = new Queue<(Pos Cell, bool OnWall)>();
        queue.Enqueue(first);
        while (queue.TryDequeue(out var current))
        {
            var p = current.Cell;
            foreach (var next in SettlementState.Neighbors(p))
            {
                if (stayInside && !state.EstablishedAt(next) && !game.Map.HasFootbridge(next.X, next.Y) &&
                    !(game.Map.HasRoad(next.X, next.Y) && state.ContainsInterior(next)) &&
                    wallSupport(next) is null && state.WallAt(next)?.IsGate != true) continue;
                var cell = Cell(next);
                if (!cell.Passable) continue;
                var fromWall = Cell(p).Support;
                var toWall = cell.Support;
                for (int level = 0; level < (unit.Kind == DefenderKind.Archer ? 2 : 1); level++)
                {
                    bool nextWall = level == 1;
                    if (nextWall ? toWall is null : !cell.Ground) continue;
                    var node = (Cell: next, OnWall: nextWall);
                    if (visited.Contains(node)) continue;
                    if (!ignoreBodies && next != target)
                    {
                        var destination = nextWall ? WalkPoint(state, next, toWall!) : new Vector2(next.X + .5f, next.Y + .5f);
                        float height = nextWall ? WallDamageLayout.ActorWalkHeight(toWall!, game.Map) : 0;
                        if (state.DefenderList.Any(other => other.Id != unit.Id &&
                            Math.Abs(other.Elevation - height) < .3f &&
                            Math.Pow(other.X - destination.X, 2) + Math.Pow(other.Y - destination.Y, 2) <
                                Math.Pow(WorldScale.HumanRadius * 2, 2))) continue;
                    }
                    bool onWall = current.OnWall;
                    if (onWall && nextWall && !joined(p, fromWall!, next, toWall!)) continue;
                    if (!onWall && !nextWall && !state.CanWalk(new(p.X + .5f, p.Y + .5f), new(next.X + .5f, next.Y + .5f))) continue;
                    if (onWall != nextWall && !WallDamageLayout.HasAccessLadder(onWall ? fromWall! : toWall!)) continue;
                    if (!onWall && nextWall && !state.EstablishedAt(p)) continue;
                    if (onWall != nextWall)
                    {
                        var access = onWall ? fromWall! : toWall!;
                        var ground = onWall ? next : p;
                        var offset = new Vector2(ground.X + .5f, ground.Y + .5f) - WallDamageLayout.LadderClimbPosition(access);
                        var inward = WallDamageLayout.LadderInward(access);
                        if (offset.LengthSquared() > 1.5f || Math.Abs(offset.X * inward.Y - offset.Y * inward.X) > .6f) continue;
                    }
                    previous[node] = current;
                    visited.Add(node);
                    if (next == target)
                    {
                        var path = new List<Pos>();
                        for (var step = node; step != first; step = previous[step]) path.Add(step.Cell);
                        path.Reverse();
                        return path;
                    }
                    queue.Enqueue(node);
                }
            }
        }
        return null;
    }
}
