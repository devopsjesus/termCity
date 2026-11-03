using System.Text.Json.Nodes;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementGarrisonTests
{
    [Fact]
    public void WeaponRangeSelectionIsAutomaticAndUsesTheClickedEmplacement()
    {
        var game = Founded();
        var session = new GameSession(game, showGuide: false);
        var siege = game.Settlement!;
        var tower = siege.Walls.First(w => w.IsTower);
        session.SelectCell(tower.Cell);
        Assert.False(session.AreaOverlaysVisible);
        Assert.Equal((tower.Cell, WallDefenseKind.None), session.ActiveWallDefenseGuide!.Value);
        var member = siege.Walls.First(w => w.SectionAnchor != w.Cell && !w.IsGateWing);
        member.Defense = WallDefenseKind.OilCauldron;
        session.SelectCell(member.Cell);
        Assert.Equal((member.Cell, WallDefenseKind.OilCauldron), session.ActiveWallDefenseGuide!.Value);
        session.ClearSelection();
        Assert.Null(session.ActiveWallDefenseGuide);
        session.SelectCell(tower.Cell);
        session.PreviewWallDefense(WallDefenseKind.Rocks);
        Assert.Equal((tower.Cell, WallDefenseKind.Rocks), session.ActiveWallDefenseGuide!.Value);
        session.CancelPreview();
        tower.Health = 0;
        session.SelectCell(tower.Cell);
        Assert.Null(session.ActiveWallDefenseGuide);
    }

    [Fact]
    public void EveryEnemySpeciesHasPersistentSelectionStatusAndClearsWhenUnavailable()
    {
        var game = Founded();
        var siege = game.Settlement!;
        var session = new GameSession(game, showGuide: false);
        foreach (var kind in Enum.GetValues<SiegeEnemyKind>())
        {
            var enemy = new SiegeEnemy { Id = siege.NextEnemyId++, Kind = kind, X = 45.5, Y = 37.5,
                Health = 96, MaxHealth = 120, Route = [new(44, 37), new(43, 37)] };
            siege.EnemyList.Add(enemy);
            session.SelectEnemy(enemy.Id);
            Assert.Same(enemy, session.SelectedEnemy);
            Assert.Contains(enemy.Definition.Name, session.SelectedEnemySummary!);
            Assert.Contains("96/120", session.SelectedEnemySummary!);
            enemy.Health = 80;
            Assert.Contains("80/120", session.SelectedEnemySummary!);
            session.SelectCell(new(30, 30));
            Assert.Null(session.SelectedEnemy);
            session.SelectEnemy(enemy.Id);
            enemy.Health = 0;
            Assert.Null(session.SelectedEnemySummary);
        }
    }

    internal static CityGame Founded(int population = 100)
    {
        var game = WorldScaleTests.Founded(population);
        game.Settlement!.EnemyList.Clear();
        game.Speed = GameSpeed.Medium;
        return game;
    }

    internal static Pos PlaceGarrison(CityGame game, string name, bool nearGate = true)
    {
        var type = game.Map.Content.Buildings.Get(name);
        var bounds = game.Settlement!.Bounds;
        var gate = game.Settlement.Walls.Single(w => w.IsGate).Cell;
        var cell = bounds.Cells().Where(p => type.FootprintAt(p.X, p.Y).Cells().All(c =>
                game.InsideSettlement(c) && game.CanBuildOn(c.X, c.Y)) &&
                game.CanPlaceBuilding(type, p.X, p.Y))
            .OrderBy(p => (nearGate ? 1 : -1) * (Math.Abs(p.X - gate.X) + Math.Abs(p.Y - gate.Y)))
            .First();
        Assert.True(game.PlaceBuilding(type, type.FootprintAt(cell.X, cell.Y)).Success);
        game.Map.BuildingEstablishedDays.Clear();
        game.Touch();
        return cell;
    }

    [Fact]
    public void GarrisonsUnlockAtOneHundredAndHaveFiniteReplenishingRosters()
    {
        var game = Founded();
        var siege = game.Settlement!;
        var home = PlaceGarrison(game, "Guard Post");
        var type = game.Map.Content.Buildings.Get("Guard Post");
        Assert.Equal(100, CityProgression.RequiredPopulation(type));
        Assert.Equal(0, type.SeatRank);
        var report = BuildingReport.Build(game, home);
        Assert.Contains(report.Groups[0].Rows, row => row[0].Text == "Roster" && row[1].Text.Contains("/4"));
        Assert.DoesNotContain(report.Groups[0].Rows, row => row[0].Text == "Staffing");
        for (int n = 0; n < 100; n++) siege.AdvanceDefenders(game, .1);
        Assert.Equal(4, siege.Defenders.Count(d => d.Home == home));
        var unit = siege.DefenderList.First();
        unit.Health = 0;
        siege.RosterTimers[home] = 18;
        for (int n = 0; n < 50; n++) siege.AdvanceDefenders(game, .1);
        Assert.Equal(3, siege.Defenders.Count);
        for (int n = 0; n < 140; n++) siege.AdvanceDefenders(game, .1);
        Assert.Equal(4, siege.Defenders.Count);
        Assert.DoesNotContain(siege.Defenders, d => d.Id == unit.Id);
    }

    [Fact]
    public void RecruitsAvoidDisconnectedPocketsBesideTheirBarracks()
    {
        var game = Founded();
        PlaceGarrison(game, "Guard Post", nearGate: false);
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .01);
        var first = Assert.Single(siege.Defenders);
        var pocket = new Pos((int)first.X, (int)first.Y);
        siege.DefenderList.Clear();
        siege.RosterTimers.Clear();
        foreach (var neighbor in new[] { new Pos(pocket.X - 1, pocket.Y), new Pos(pocket.X + 1, pocket.Y),
            new Pos(pocket.X, pocket.Y - 1), new Pos(pocket.X, pocket.Y + 1) })
            if (game.Map.BuildingAt(neighbor.X, neighbor.Y) is null)
                game.Map.SetTerrain(neighbor.X, neighbor.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();

        siege.AdvanceDefenders(game, .01);

        var recruit = Assert.Single(siege.Defenders);
        Assert.NotEqual(pocket, new Pos((int)recruit.X, (int)recruit.Y));
        Assert.True(siege.DefenderCanReach(game, recruit, siege.Walls.Single(w => w.IsGate).Cell));
        Assert.False(siege.DefenderCanReach(game, recruit, pocket));
    }

    [Fact]
    public void ArchersWalkAndClimbThroughTowersInsteadOfTeleporting()
    {
        var game = Founded();
        PlaceGarrison(game, "Archer Barracks");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = siege.Defenders.Single();
        Assert.Equal(0, unit.Elevation);
        var initial = new Pos((int)unit.X, (int)unit.Y);
        for (int n = 0; n < 700; n++)
        {
            double x = unit.X, y = unit.Y;
            siege.AdvanceDefenders(game, .1);
            Assert.True(Math.Sqrt(Math.Pow(x - unit.X, 2) + Math.Pow(y - unit.Y, 2)) <= SettlementDefender.MovementSpeed * .1 + .00001);
        }
        Assert.True(unit.Elevation > .5);
        Assert.Contains(siege.Walls, w => w.Cell == new Pos((int)unit.X, (int)unit.Y));
        Assert.NotEqual(initial, new Pos((int)unit.X, (int)unit.Y));
    }

    [Theory]
    [InlineData(DefenderKind.Archer)]
    [InlineData(DefenderKind.Soldier)]
    public void GatePassagesRemainGroundRoutesWithoutLadders(DefenderKind kind)
    {
        var game = Founded();
        PlaceGarrison(game, kind == DefenderKind.Archer ? "Archer Barracks" : "Guard Post");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = Assert.Single(siege.Defenders);
        var gate = siege.Walls.Single(w => w.IsGate);
        var inward = new System.Numerics.Vector2(gate.Tangent.Y, -gate.Tangent.X) * WallDamageLayout.ExteriorSide(gate);
        unit.X = gate.Center.X + inward.X * 3;
        unit.Y = gate.Center.Y + inward.Y * 3;
        unit.Elevation = 0;
        var outside = gate.Center - inward * 3;
        var destination = new Pos((int)outside.X, (int)outside.Y);
        var command = siege.CommandDefenders(game, [unit.Id], destination, null);
        Assert.True(command.Success, command.Message);
        bool passedGate = false;
        for (int n = 0; n < 100 && unit.Order == DefenderOrder.Move; n++)
        {
            siege.AdvanceDefenders(game, .1);
            Assert.Equal(0, unit.Elevation);
            passedGate |= new Pos((int)unit.X, (int)unit.Y) == gate.Cell;
        }
        Assert.True(passedGate);
        Assert.Equal(destination, new Pos((int)unit.X, (int)unit.Y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchersDescendAndAscendOutsideTheLadderWithMidClimbSaveLoad(bool terraced)
    {
        var game = Founded();
        PlaceGarrison(game, "Archer Barracks");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = Assert.Single(siege.Defenders);
        game.SetFunding(ServiceKind.Defence, 0);
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.X == siege.Bounds.Right &&
            w.Cell.Y > siege.Bounds.Y + 4 && w.Cell.Y < siege.Bounds.Bottom - 4);
        if (terraced)
        {
            game.Map.SetTerrain(tower.Cell.X, tower.Cell.Y, game.Map.Content.Terrains.Get("Hill"));
            game.Touch();
        }
        var inward = WallDamageLayout.LadderInward(tower);
        var outside = tower.Center + inward * 3;
        var ground = new Pos((int)outside.X, (int)outside.Y);
        unit.X = tower.Center.X;
        unit.Y = tower.Center.Y;
        unit.Elevation = WallDamageLayout.ActorWalkHeight(tower, game.Map);
        var command = siege.CommandDefenders(game, [unit.Id], ground, null);
        Assert.True(command.Success, command.Message);
        bool saved = false;
        int descending = 0, ascending = 0;
        float WorldHeight() => TerrainSprite.Elevation(game.Map, (int)unit.X, (int)unit.Y) + unit.Elevation;
        void CheckClimb(float previous, bool goingUp)
        {
            float delta = WorldHeight() - previous;
            if (Math.Abs(delta) < .001f || unit.Elevation < .01f ||
                WorldHeight() >= TerrainSprite.Elevation(game.Map, tower.Cell.X, tower.Cell.Y) +
                    WallDamageLayout.ActorWalkHeight(tower, game.Map) - .01f) return;
            Assert.Equal(goingUp, delta > 0);
            var body = new System.Numerics.Vector2((float)unit.X, (float)unit.Y);
            Assert.True(System.Numerics.Vector2.Distance(body, WallDamageLayout.LadderClimbPosition(tower)) < .01f);
            Assert.False(WallDamageLayout.ContainsFootprint(tower, unit.X, unit.Y, WorldScale.HumanRadius));
            Assert.InRange(Math.Abs(delta), 0, SettlementDefender.ClimbSpeed * .1 + .00001);
            if (goingUp) ascending++; else descending++;
        }
        for (int n = 0; n < 180 && unit.Order == DefenderOrder.Move; n++)
        {
            float previous = WorldHeight();
            siege.AdvanceDefenders(game, .1);
            CheckClimb(previous, false);
            if (!saved && descending > 3)
            {
                string json = SaveGameStore.Serialize(game);
                game = SaveGameStore.Deserialize(json);
                Assert.Equal(json, SaveGameStore.Serialize(game));
                siege = game.Settlement!;
                unit = siege.Defenders.Single();
                tower = siege.Walls.Single(w => w.Id == tower.Id);
                saved = true;
            }
        }
        Assert.True(saved && descending > 4);
        Assert.Equal(0, unit.Elevation);
        Assert.Equal(ground, new Pos((int)unit.X, (int)unit.Y));
        Assert.Equal(DefenderOrder.Automatic, unit.Order);
        command = siege.CommandDefenders(game, [unit.Id], tower.Cell, null);
        Assert.True(command.Success, command.Message);
        for (int n = 0; n < 180 && unit.Order == DefenderOrder.Move; n++)
        {
            float previous = WorldHeight();
            siege.AdvanceDefenders(game, .1);
            CheckClimb(previous, true);
        }
        Assert.True(ascending > 4);
        Assert.Equal(DefenderOrder.Automatic, unit.Order);
        Assert.Equal(tower.Cell, new Pos((int)unit.X, (int)unit.Y));
        Assert.Equal(WallDamageLayout.ActorWalkHeight(tower, game.Map), unit.Elevation, 4);
    }

    [Fact]
    public void ArchersCanCrossTheGateOnItsConnectedUpperWalkway()
    {
        var game = Founded();
        PlaceGarrison(game, "Archer Barracks");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = Assert.Single(siege.Defenders);
        game.SetFunding(ServiceKind.Defence, 0);
        foreach (var tower in siege.Walls.Where(w => w.IsTower)) tower.IsTower = false;
        siege.InvalidateWallTopology();
        var gate = siege.Walls.Single(w => w.IsGate);
        var normal = new System.Numerics.Vector2(-gate.Tangent.Y, gate.Tangent.X);
        var start = new Pos(gate.Cell.X + (int)gate.Tangent.X * 2, gate.Cell.Y + (int)gate.Tangent.Y * 2);
        var destination = new Pos(gate.Cell.X - (int)gate.Tangent.X * 2, gate.Cell.Y - (int)gate.Tangent.Y * 2);
        var support = siege.Walls.Single(w => w.Cell == start);
        unit.X = start.X + .5 + normal.X * WallDamageLayout.WalkwayOffset(support);
        unit.Y = start.Y + .5 + normal.Y * WallDamageLayout.WalkwayOffset(support);
        unit.Elevation = WallDamageLayout.ActorWalkHeight(support, game.Map);
        var command = siege.CommandDefenders(game, [unit.Id], destination, null);
        Assert.True(command.Success, command.Message);
        bool crossed = false;
        for (int n = 0; n < 60 && unit.Order == DefenderOrder.Move; n++)
        {
            siege.AdvanceDefenders(game, .1);
            Assert.True(unit.Elevation > .5f);
            crossed |= new Pos((int)unit.X, (int)unit.Y) == gate.Cell;
        }
        Assert.True(crossed);
        Assert.Equal(DefenderOrder.Automatic, unit.Order);
        Assert.Equal(destination, new Pos((int)unit.X, (int)unit.Y));
    }

    [Fact]
    public void ArchersUseSideLaddersBetweenWalkwaysAndTowerDecks()
    {
        var game = Founded();
        PlaceGarrison(game, "Archer Barracks");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = Assert.Single(siege.Defenders);
        game.SetFunding(ServiceKind.Defence, 0);
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.Y == siege.Bounds.Bottom &&
            w.Cell.X > siege.Bounds.X + 4 && w.Cell.X < siege.Bounds.Right - 4);
        var ladder = Assert.Single(WallDamageLayout.WalkwayLadders(tower).Take(1));
        var lower = tower.Center + ladder.Direction * 3;
        var walkway = siege.Walls.Single(w => w.Cell == new Pos((int)lower.X, (int)lower.Y));
        var position = walkway.Center + new System.Numerics.Vector2(-walkway.Tangent.Y, walkway.Tangent.X) *
            WallDamageLayout.WalkwayOffset(walkway);
        unit.X = position.X;
        unit.Y = position.Y;
        unit.Elevation = WallDamageLayout.ActorWalkHeight(walkway, game.Map);
        foreach (var goal in new[] { tower.Cell, walkway.Cell })
        {
            var command = siege.CommandDefenders(game, [unit.Id], goal, null);
            Assert.True(command.Success, command.Message);
            int climbing = 0;
            for (int step = 0; step < 160 && unit.Order == DefenderOrder.Move; step++)
            {
                float height = unit.Elevation;
                siege.AdvanceDefenders(game, .1);
                if (Math.Abs(unit.Elevation - height) < .01f) continue;
                climbing++;
                var anchor = ladder.Position + ladder.Direction * WallDamageLayout.LadderClimberOffset;
                Assert.True(System.Numerics.Vector2.Distance(new((float)unit.X, (float)unit.Y), anchor) < .01f,
                    $"Side climb at {unit.X:F3},{unit.Y:F3}, expected {anchor}; goal={goal}, " +
                    $"height={height:F3}->{unit.Elevation:F3}, path={unit.PathIndex}/{unit.Path.Count}.");
            }
            Assert.True(climbing > 3, $"No complete side climb: goal={goal}, order={unit.Order}, " +
                $"position={unit.X:F3},{unit.Y:F3}, height={unit.Elevation:F3}, path={unit.PathIndex}/{unit.Path.Count}.");
            Assert.Equal(DefenderOrder.Automatic, unit.Order);
        }
    }

    [Fact]
    public void CachedGateClimbingRoutesCannotReplaceTowerAccess()
    {
        var game = Founded();
        PlaceGarrison(game, "Archer Barracks");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = Assert.Single(siege.Defenders);
        foreach (var tower in siege.Walls.Where(w => w.IsTower)) tower.IsTower = false;
        siege.InvalidateWallTopology();
        game.SetFunding(ServiceKind.Defence, 0);
        var gate = siege.Walls.Single(w => w.IsGate);
        var inward = new System.Numerics.Vector2(gate.Tangent.Y, -gate.Tangent.X) * WallDamageLayout.ExteriorSide(gate);
        unit.X = gate.Center.X + inward.X;
        unit.Y = gate.Center.Y + inward.Y;
        unit.Elevation = 0;
        var destination = new Pos(gate.Cell.X + (int)gate.Tangent.X * 2, gate.Cell.Y + (int)gate.Tangent.Y * 2);
        Assert.False(siege.DefenderCanReach(game, unit, destination));
        unit.Order = DefenderOrder.Move;
        unit.Destination = destination;
        unit.PathTarget = destination;
        unit.Path = [gate.Cell, new(gate.Cell.X + (int)gate.Tangent.X, gate.Cell.Y + (int)gate.Tangent.Y), destination];
        unit.PathIndex = 0;
        for (int n = 0; n < 20; n++)
        {
            siege.AdvanceDefenders(game, .1);
            Assert.Equal(0, unit.Elevation);
        }
        Assert.True(unit.Order == DefenderOrder.Automatic,
            $"Order={unit.Order}, position={unit.X:F3},{unit.Y:F3}, elevation={unit.Elevation:F3}, " +
            $"path={unit.PathIndex}/{unit.Path.Count}, next={(unit.PathIndex < unit.Path.Count ? unit.Path[unit.PathIndex] : null)}, " +
            $"target={unit.PathTarget}, destination={destination}.");
    }

    [Fact]
    public void OrdersRejectUnreachableGroundAndResumeAutomaticDefenseWhenTargetDies()
    {
        var game = Founded();
        PlaceGarrison(game, "Guard Post");
        var siege = game.Settlement!;
        siege.AdvanceDefenders(game, .1);
        var unit = siege.Defenders.Single();
        var session = new GameSession(game, showGuide: false);
        session.SelectDefender(unit.Id);
        game.Map.SetTerrain(2, 2, game.Map.Content.Terrains.Get("Water"));
        Assert.False(session.CommandSelectedDefenders(new(2, 2)).Success);
        var target = new SiegeEnemy { Id = siege.NextEnemyId++, Tier = 1, X = 45.5, Y = 37.5,
            Health = 96, MaxHealth = 96, Route = [new(44, 37), new(43, 37)] };
        siege.EnemyList.Add(target);
        Assert.True(session.CommandSelectedDefenders(enemyId: target.Id).Success);
        Assert.Equal(DefenderOrder.Attack, unit.Order);
        target.Health = 0;
        siege.AdvanceDefenders(game, .1);
        Assert.Equal(DefenderOrder.Automatic, unit.Order);
        Assert.Null(unit.TargetEnemyId);
        Assert.True(session.CommandSelectedDefenders(new((int)unit.X, (int)unit.Y)).Success);
        for (int n = 0; n < 10 && unit.Order == DefenderOrder.Move; n++) siege.AdvanceDefenders(game, .1);
        Assert.Equal(DefenderOrder.Automatic, unit.Order);
    }

    [Fact]
    public void GarrisonsAndOrdersRoundTripWithoutChangingSimulationContinuation()
    {
        var game = Founded();
        PlaceGarrison(game, "Guard Post");
        for (int n = 0; n < 30; n++) game.Update(.1);
        var siege = game.Settlement!;
        var ids = siege.Defenders.Select(d => d.Id).Take(2).ToArray();
        Assert.NotEmpty(ids);
        Assert.True(siege.CommandDefenders(game, ids, new(50, 37), null).Success);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(loaded));
        for (int n = 0; n < 50; n++) { game.Update(.1); loaded.Update(.1); }
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(loaded));
        var data = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        data["Settlement"]!["Garrison"]!["Units"]![0]!["Health"] = -1;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(data.ToJsonString()));
    }

    [Fact]
    public void FirstOrcWarningIsOneTimeAndMixedEnemiesPreserveSpeciesOnLoad()
    {
        var game = Founded();
        game.HighestPopulation = game.HighestMilestone = 250;
        var siege = game.Settlement!;
        siege.NextWaveSeconds = 30;
        game.Update(.1);
        Assert.NotNull(siege.FirstOrcApproach);
        var side = siege.FirstOrcApproach;
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(side, loaded.Settlement!.FirstOrcApproach);
        siege.NextWaveSeconds = 0;
        game.Update(.1);
        Assert.True(siege.OrcsIntroduced);
        Assert.Null(siege.FirstOrcApproach);
        Assert.Contains(siege.Enemies, g => g.Kind == SiegeEnemyKind.Orc && g.BodyScale == 1);
        Assert.Contains(siege.Enemies, g => g.Kind == SiegeEnemyKind.Goblin);
        foreach (var orc in siege.Enemies.Where(g => g.Kind == SiegeEnemyKind.Orc))
        {
            var end = orc.Route[^1];
            Assert.True(side switch
            {
                BaileyGateSide.Right => end.X == siege.Bounds.Right,
                BaileyGateSide.Left => end.X == siege.Bounds.X,
                BaileyGateSide.Bottom => end.Y == siege.Bounds.Bottom,
                _ => end.Y == siege.Bounds.Y,
            });
        }

        var copy = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(siege.Enemies.Select(g => g.Kind), copy.Settlement!.Enemies.Select(g => g.Kind));
        for (int a = 0; a < siege.Enemies.Count; a++)
            for (int b = a + 1; b < siege.Enemies.Count; b++)
                Assert.True(Math.Sqrt(Math.Pow(siege.Enemies[a].X - siege.Enemies[b].X, 2) +
                    Math.Pow(siege.Enemies[a].Y - siege.Enemies[b].Y, 2)) + .00001 >=
                    siege.Enemies[a].CollisionRadius + siege.Enemies[b].CollisionRadius);
    }

    [Fact]
    public void PaidRepairsWorkDuringCombatAndMissingGarrisonsDoNotLeaveUnLoadableUnits()
    {
        var game = Founded();
        var home = PlaceGarrison(game, "Guard Post");
        var state = game.Settlement!;
        state.AdvanceDefenders(game, .1);
        Assert.NotEmpty(state.Defenders);
        Assert.True(game.DebugStartSiegeWave(8).Success);
        var wall = state.WallSections.First();
        wall.Health -= 50;
        int gold = game.Money;
        var paid = game.RepairSettlementWalls();
        Assert.True(paid.Success);
        Assert.Equal(20, paid.Cost);
        Assert.Equal(gold - 20, game.Money);
        Assert.NotEmpty(state.Enemies);
        game.Map.SetBuilding(home.X, home.Y, null);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Empty(loaded.Settlement!.Defenders);
        Assert.Empty(loaded.Settlement.RosterTimers);
    }

    [Fact]
    public void InsideGarrisonsShootNearbyOrcsWithoutProvidingMapWideProtection()
    {
        static (bool Breach, double Damage, string Context) Assault(bool? near)
        {
            var game = Founded();
            game.HighestPopulation = game.HighestMilestone = 250;
            var state = game.Settlement!;
            if (near is { } location)
            {
                PlaceGarrison(game, "Archer Barracks", location);
                PlaceGarrison(game, "Guard Post", location);
                for (int n = 0; n < 200; n++) state.AdvanceDefenders(game, .1);
            }
            string deployed = string.Join("; ", state.Defenders.Select(d =>
                $"{d.Id}:{d.Kind}@{d.X:F1},{d.Y:F1} height={d.Elevation:F1} path={d.PathIndex}/{d.Path.Count}"));
            int y = state.Bounds.Y + state.Bounds.Height / 2;
            for (int n = -1; n <= 1; n++)
            {
                var cell = new Pos(state.Bounds.Right, y + n * 2);
                state.EnemyList.Add(new SiegeEnemy { Id = state.NextEnemyId++, Kind = SiegeEnemyKind.Orc, Tier = 2,
                    X = cell.X + 4, Y = cell.Y + .5, Health = 420, MaxHealth = 420, Route = [cell] });
            }
            double damage = 0;
            for (int n = 0; n < 160 && state.Enemies.Count > 0; n++)
            {
                damage = Math.Max(damage, 1260 - state.Enemies.Sum(enemy => enemy.Health));
                state.Advance(game, .1);
            }
            return (game.Events.Any(e => e.Message.Contains("was breached")),
                damage,
                $"deployed={deployed}; survivors={state.Defenders.Count}; " +
                string.Join("; ", game.Events.Where(e => e.Message.Contains("was breached")).Select(e => e.Message)));
        }
        var unguarded = Assault(null);
        var prepared = Assault(true);
        var distant = Assault(false);
        Assert.True(unguarded.Breach);
        Assert.True(prepared.Damage > unguarded.Damage, prepared.Context);
        Assert.True(prepared.Damage > distant.Damage,
            $"Nearby garrison outgoing damage {prepared.Damage:F1} must exceed distant damage {distant.Damage:F1}.");
    }

    [Theory]
    [InlineData(42)]
    [InlineData(1)]
    [InlineData(7)]
    public void ContinuousRepairsMakeTheFirstThreeUnassistedWavesRecoverableOnDryGround(int seed)
    {
        var game = TestCity.Flat(seed, new GameConfig { MapWidth = 320, MapHeight = 192 }, CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        SettlementDebugSetup.Apply(game, siege: true);
        foreach (var area in game.Map.BuildingFootprints.Values.Where(area =>
            SettlementState.RosterCapacity(game.Map.BuildingAt(area.X, area.Y)!) > 0).ToArray())
            Assert.True(game.Demolish(area.Cells()).Success);
        var state = game.Settlement!;
        game.Paused = false;
        var deficits = new List<double>();
        int previous = state.WaveNumber;
        for (int step = 0; step < 4000 && state.WaveNumber < 4; step++)
        {
            state.Advance(game, .1);
            if (state.WaveNumber == previous) continue;
            deficits.Add(state.WallSections.Sum(w => w.MaxHealth - w.Health));
            previous = state.WaveNumber;
        }
        Assert.True(state.WaveNumber == 4, $"Seed {seed}, wave {state.WaveNumber}: " +
            string.Join("; ", state.Enemies.Select(e => $"{e.Id}@{e.X:F3},{e.Y:F3} hp={e.Health:F1} " +
                $"next={e.Route[e.RouteIndex]} index={e.RouteIndex}/{e.Route.Count} target={state.AttackableWall(e)?.Cell}")));
        Assert.DoesNotContain(game.Events, e => e.Message.Contains("was breached"));
        Assert.All(deficits, deficit => Assert.InRange(deficit, 0, 0.001));
        Assert.Empty(state.Defenders);
    }
}
