using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class GarrisonVisualTests
{
    [Theory]
    [InlineData(WallMaterial.Wood)]
    [InlineData(WallMaterial.Stone)]
    [InlineData(WallMaterial.ReinforcedStone)]
    public void ClimberBodiesStayOnTheExposedSideOfEveryTowerLadder(WallMaterial material)
    {
        var state = new SettlementState(new(40, 40, 40, 40));
        foreach (var wall in state.Walls.Where(w => w.IsTower))
        {
            wall.BuiltMaterial = material;
            var inward = WallDamageLayout.LadderInward(wall);
            var ladder = WallDamageLayout.LadderPosition(wall);
            var climber = WallDamageLayout.LadderClimbPosition(wall);
            Assert.Equal(WallDamageLayout.LadderClimberOffset, Vector2.Dot(climber - ladder, inward), 4);
            Assert.False(WallDamageLayout.ContainsFootprint(wall, climber.X, climber.Y, WorldScale.HumanRadius));
            Assert.True(Vector2.Dot(inward, new Vector2(60, 60) - wall.Center) > 0);
        }
    }

    [Fact]
    public void GroundLaddersStayInStructuralInteriorsAndWalkwayLaddersStartAboveGround()
    {
        var state = new SettlementState(new(40, 40, 40, 40));
        foreach (var tower in state.Walls.Where(w => w.IsTower))
        {
            Assert.True(WallDamageLayout.HasAccessLadder(tower));
            Assert.True(state.WallNetwork.ContainsAny(WallDamageLayout.LadderClimbPosition(tower)));
            var connections = WallDamageLayout.WalkwayLadders(tower);
            Assert.NotEmpty(connections);
            Assert.All(connections, ladder =>
            {
                Assert.True(WallDamageLayout.WalkHeight(ladder.Walkway) > 0);
                Assert.True(WallDamageLayout.WalkHeight(ladder.Walkway) < WallDamageLayout.WalkHeight(tower));
                Assert.True(WallDamageLayout.ContainsWalkway(ladder.Walkway,
                    ladder.Position.X - ladder.Direction.X * WallDamageLayout.LadderWallOffset,
                    ladder.Position.Y - ladder.Direction.Y * WallDamageLayout.LadderWallOffset));
            });
        }
        var outside = WallDamageLayout.PreviewTower(new(20.5f, 20.5f), WallMaterial.Wood);
        outside.Id = state.NextWallId++;
        state.AddWall(outside);
        Assert.False(WallDamageLayout.HasAccessLadder(outside));
    }

    [Fact]
    public void GeneratedTowerLaddersAppearOnlyWhenTheirLandingIsEnclosed()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        Assert.True(game.BuildWallRun(new(10.5f, 90.5f), new(30.5f, 90.5f)).Success);
        Assert.True(game.BuildWallRun(new(30.5f, 90.5f), new(30.5f, 110.5f)).Success);
        var tower = Assert.Single(game.Settlement!.Walls, wall => wall.JunctionTower);
        Assert.False(WallDamageLayout.HasAccessLadder(tower));
        Assert.True(game.BuildWallRun(new(30.5f, 110.5f), new(10.5f, 110.5f)).Success);
        Assert.True(game.BuildWallRun(new(10.5f, 110.5f), new(10.5f, 90.5f)).Success);
        Assert.True(WallDamageLayout.HasAccessLadder(tower));
        Assert.True(game.Settlement.WallNetwork.ContainsAny(WallDamageLayout.LadderClimbPosition(tower)));
    }

    [Theory]
    [InlineData(BaileyGateSide.Top)]
    [InlineData(BaileyGateSide.Right)]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Left)]
    public void GatesNeverHaveLaddersAndOriginalWalkwaysFaceTheInterior(BaileyGateSide side)
    {
        var state = new SettlementState(new(40, 40, 40, 40), side);
        foreach (var wall in state.Walls)
        {
            Assert.Equal(wall.IsTower, WallDamageLayout.HasAccessLadder(wall));
            if (wall.IsTower || wall.IsGateWing) continue;
            var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
            var walkway = wall.Center + normal * WallDamageLayout.WalkwayOffset(wall);
            Assert.True(state.WallNetwork.ContainsAny(walkway), $"Walkway points outside: {wall.Cell}");
        }
        var gate = state.Walls.Single(w => w.IsGate);
        var bridge = WallDamageLayout.Fragments(gate).Single(p => p.Kind == WallFragmentKind.Body &&
            Math.Abs(p.Size.Y - .16f) < .001f);
        Assert.Equal(WallDamageLayout.WalkwayOffset(gate), bridge.Center.Z);
        Assert.True(bridge.Size.Z >= WallDamageLayout.WalkwayWidth);
        var tower = state.Walls.First(w => w.IsTower);
        tower.Health = 0;
        Assert.False(WallDamageLayout.HasAccessLadder(tower));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectedConcaveWalkwaysFaceInsideRegardlessOfDrawingDirection(bool reverse)
    {
        var state = new SettlementState(new(40, 40, 40, 40));
        Vector2[] points = [new(10.5f, 10.5f), new(30.5f, 10.5f), new(30.5f, 20.5f),
            new(20.5f, 20.5f), new(20.5f, 30.5f), new(10.5f, 30.5f)];
        var walls = new List<WallComponent>();
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            var wall = WallDamageLayout.Preview(reverse ? b : a, reverse ? a : b, WallMaterial.Wood);
            WallDamageLayout.OrientPreview(wall, state, [points]);
            var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
            Assert.True(WallNetwork.PointInside(points, wall.Center + normal * WallDamageLayout.WalkwayOffset(wall)));
            wall.Id = state.NextWallId++;
            state.AddWall(wall);
            _ = WallDamageLayout.ExteriorSide(wall);
            walls.Add(wall);
        }
        var connector = WallDamageLayout.Preview(new(30.5f, 10.5f), new(40.5f, 40.5f), WallMaterial.Wood);
        connector.Id = state.NextWallId++;
        state.AddWall(connector);
        var region = state.WallNetwork.RegionAt(new(15, 15))!;
        Assert.True(region.ConnectedToRoot);
        foreach (var wall in walls)
        {
            var normal = new Vector2(-wall.Tangent.Y, wall.Tangent.X);
            float side = WallDamageLayout.ExteriorSide(wall);
            var deck = WallDamageLayout.Fragments(wall).Single(p => p.Kind == WallFragmentKind.Trim);
            Assert.True(region.Contains(wall.Center + normal * deck.Center.Z));
            Assert.False(region.Contains(wall.Center + normal * side * .2f));
            wall.DefenseFacing *= -1;
            Assert.Equal(side, WallDamageLayout.ExteriorSide(wall));
        }
        var sides = walls.Select(WallDamageLayout.ExteriorSide).ToArray();
        walls[0].Health = 0;
        Assert.Equal(sides, walls.Select(WallDamageLayout.ExteriorSide));
        walls[0].Health = walls[0].MaxHealth;
        Assert.Equal(sides, walls.Select(WallDamageLayout.ExteriorSide));
    }

    [Theory]
    [InlineData(WallMaterial.Wood)]
    [InlineData(WallMaterial.Stone)]
    [InlineData(WallMaterial.ReinforcedStone)]
    public void WalkwayLeavesHumanClearanceBehindTheParapet(WallMaterial material)
    {
        var wall = WallDamageLayout.Preview(new(10.5f, 10.5f), new(11.5f, 10.5f), material);
        var deck = WallDamageLayout.Fragments(wall).Single(p => p.Kind == WallFragmentKind.Trim);
        float lane = WallDamageLayout.WalkwayOffset(wall);
        Assert.True(deck.Size.Z >= WallDamageLayout.WalkwayWidth);
        Assert.True(WallDamageLayout.ContainsWalkway(wall, wall.Center.X, wall.Center.Y + lane));
        Assert.False(WallDamageLayout.ContainsWalkway(wall, wall.Center.X,
            wall.Center.Y + lane + deck.Size.Z / 2 + .01f));
        Assert.True(Math.Abs(lane - deck.Center.Z) + .42f <= deck.Size.Z / 2);
        var parapet = WallDamageLayout.Fragments(wall).Where(p => p.Kind == WallFragmentKind.Body &&
            p.Center.Y > WallDamageLayout.WalkHeight(wall)).ToArray();
        Assert.All(parapet, part => Assert.True(Math.Abs(part.Center.Z - lane) - part.Size.Z / 2 >= .40f));
    }

    [Theory]
    [InlineData(SiegeDeathCause.Arrow)]
    [InlineData(SiegeDeathCause.Blade)]
    [InlineData(SiegeDeathCause.Crushing)]
    [InlineData(SiegeDeathCause.Oil)]
    public void KillsCaptureCauseAndDirectionOnceWithoutChangingDamageTiming(SiegeDeathCause cause)
    {
        var state = new SettlementState(new(10, 10, 40, 40));
        var enemy = new SiegeEnemy { Id = 1, X = 20, Y = 20, Health = 32, MaxHealth = 32, Kind = SiegeEnemyKind.Goblin };
        state.DamageEnemy(enemy, 16, cause, new(10, 20));
        Assert.Empty(state.Deaths);
        state.DamageEnemy(enemy, 16, cause, new(10, 20));
        var death = Assert.Single(state.Deaths);
        Assert.Equal(0, enemy.Health);
        Assert.Equal(cause, death.Cause);
        Assert.Equal(Vector2.UnitX, death.Direction);
        state.DamageEnemy(enemy, 100, cause, Vector2.Zero);
        Assert.Single(state.Deaths);
    }

    [Fact]
    public void CornerTowersAppearAndNearbyPointersSnapToTheirCenters()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        Assert.True(game.BuildWallRun(new(10.5f, 30.5f), new(30.5f, 30.5f)).Success);
        Assert.True(game.BuildWallRun(new(30.5f, 30.5f), new(30.5f, 38.5f)).Success);
        var tower = Assert.Single(game.Settlement!.Walls, w => w.JunctionTower);
        Assert.Equal(new Vector2(30.5f, 30.5f), tower.Center);
        Assert.Equal(tower.Center, game.SnapWallPoint(tower.Center + new Vector2(1.5f, .5f)));
    }

    [Fact]
    public void TowerFootprintsArePreviewedAndRejectTheEntireBlockedCorner()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        Assert.True(game.BuildWallRun(new(10.5f, 30.5f), new(30.5f, 30.5f)).Success);
        var plan = game.PlanWallRun(new(30.5f, 30.5f), new(30.5f, 38.5f));
        Assert.True(plan.Valid, plan.Error);
        Assert.Contains(new Vector2(30.5f, 30.5f), plan.Towers);
        game.Map.SetBuilding(28, 28, game.Map.Content.Buildings.Get("Cottage"));
        game.Touch();
        int gold = game.Money, count = game.Settlement!.Walls.Count;
        Assert.False(game.BuildWallRun(plan.Start, plan.End).Success);
        Assert.Equal(gold, game.Money);
        Assert.Equal(count, game.Settlement.Walls.Count);
    }

    [Fact]
    public void DeathPlaybackFreezesWithTheGameAndExpiresWithoutDelayingCombat()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        var state = game.Settlement!;
        state.WaveNumber = 1;
        var enemy = new SiegeEnemy { X = 20, Y = 30, Tier = 1, Health = 1, MaxHealth = 1 };
        state.DamageEnemy(enemy, 1, SiegeDeathCause.Arrow, Vector2.Zero);
        game.Paused = true;
        game.Update(1);
        Assert.Equal(0, Assert.Single(state.Deaths).Seconds);
        game.Paused = false;
        game.Update(.1);
        Assert.True(Assert.Single(state.Deaths).Seconds > 0);
        for (int i = 0; i < 60; i++) game.Update(.1);
        Assert.Empty(state.Deaths);
        Assert.Equal(0, enemy.Health);
    }
}
