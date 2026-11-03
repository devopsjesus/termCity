using System.Numerics;
using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class CombatImpactTests
{
    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        game.Paused = true;
        return game;
    }

    private static SiegeEnemy Enemy(SettlementState state, Vector2 position, double health = 420)
    {
        var enemy = new SiegeEnemy { Id = state.NextEnemyId++, X = position.X, Y = position.Y,
            Health = health, MaxHealth = health, Tier = 2, Kind = SiegeEnemyKind.Orc };
        state.EnemyList.Add(enemy);
        return enemy;
    }

    [Theory]
    [InlineData(WallDefenseKind.None, 24, 2)]
    [InlineData(WallDefenseKind.OilCauldron, 180, 10)]
    [InlineData(WallDefenseKind.Rocks, 140, 7)]
    [InlineData(WallDefenseKind.Ballista, 240, 6)]
    [InlineData(WallDefenseKind.Trebuchet, 360, 14)]
    public void ProfilesMatchTheirDistinctRoles(WallDefenseKind kind, double damage, double reload)
    {
        Assert.Equal(damage, WallDefenses.Get(kind).Damage);
        Assert.Equal(reload, WallDefenses.Get(kind).ReloadSeconds);
        Assert.Equal(40, CombatProfiles.Archer.Damage);
        Assert.Equal(1.5, CombatProfiles.Archer.Interval);
        Assert.Equal(60, CombatProfiles.Guard.Damage);
        Assert.Equal(1.2, CombatProfiles.Guard.Interval);
    }

    [Theory]
    [InlineData(WallDefenseKind.OilCauldron, 180)]
    [InlineData(WallDefenseKind.Rocks, 140)]
    public void DropsResolveOnceAtGroundContactAndMovingOutAvoidsThem(WallDefenseKind kind, double damage)
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower && w.Cell.X == state.Bounds.X && w.Cell.Y == state.Bounds.Y);
        wall.Defense = kind;
        var footprint = DropAttackLayout.Footprints(wall, kind)[0];
        var point = footprint.Center - footprint.Forward * .25f;
        var victim = Enemy(state, point);
        var escaping = Enemy(state, point + footprint.Right * .2f);
        Assert.True(state.ReleaseMountedAttack(game, wall, kind));
        var shot = Assert.Single(state.Projectiles);
        Assert.Equal(420, victim.Health);
        escaping.X = 10; escaping.Y = 10;
        state.AdvanceCommittedAttacks(game, shot.ContactSeconds - .001);
        Assert.Equal(420, victim.Health);
        state.AdvanceCommittedAttacks(game, .002);
        Assert.Equal(420 - damage, victim.Health);
        Assert.Equal(420, escaping.Health);
        state.AdvanceCommittedAttacks(game, .5);
        Assert.Equal(420 - damage, victim.Health);
    }

    [Fact]
    public void ReleasedOilSurvivesItsSourceAndPendingStateRoundTripsExactly()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y);
        wall.Defense = WallDefenseKind.OilCauldron;
        var footprint = DropAttackLayout.Footprints(wall, wall.Defense)[0];
        var enemy = Enemy(state, footprint.Center - footprint.Forward * .25f);
        Assert.True(state.ReleaseMountedAttack(game, wall, wall.Defense));
        var shot = Assert.Single(state.Projectiles);
        state.AdvanceCommittedAttacks(game, .4);
        var saved = SavedAttack.Capture(shot);
        var restored = saved.Restore(game, state.NextAttackId);
        Assert.Equal(saved, SavedAttack.Capture(restored));
        state.RemoveWall(wall);
        Assert.Contains(shot, state.Projectiles);
        state.AdvanceCommittedAttacks(game, shot.ContactSeconds);
        Assert.Equal(240, enemy.Health);
        var applied = SavedAttack.Capture(shot).Restore(game, state.NextAttackId);
        state.ProjectileList.Clear(); state.ProjectileList.Add(applied);
        state.AdvanceCommittedAttacks(game, .1);
        Assert.Equal(240, enemy.Health);
    }

    [Fact]
    public void OilFacesAndInterveningWallsPreventDamageBehindTheDefense()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => !w.IsTower && !w.IsGate && !w.IsGateWing && w.Cell.Y == state.Bounds.Y &&
            state.Walls.Where(t => t.IsTower).All(t => Vector2.Distance(t.Center, w.Center) > 4));
        wall.Defense = WallDefenseKind.OilCauldron;
        Enemy(state, wall.Center + new Vector2(0, 1));
        Assert.False(state.ReleaseMountedAttack(game, wall, wall.Defense));
        var footprint = DropAttackLayout.Footprints(wall, wall.Defense)[0];
        var target = footprint.Center - footprint.Forward * .2f;
        Enemy(state, target);
        Assert.True(state.ReleaseMountedAttack(game, wall, wall.Defense));
        Assert.True(state.DropTargetVisible(game, wall, footprint, target));
        Assert.False(state.DropTargetVisible(game, wall, footprint, footprint.Center + footprint.Right * 2));
    }

    [Fact]
    public void BallistaDamageWaitsForTravelAndDoesNotPierceASecondEnemy()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y);
        var first = Enemy(state, wall.Center + new Vector2(0, -6));
        var second = Enemy(state, wall.Center + new Vector2(0, -7));
        Assert.True(state.ReleaseMountedAttack(game, wall, WallDefenseKind.Ballista));
        Assert.Equal(420, first.Health);
        for (int i = 0; i < 10; i++) state.AdvanceCommittedAttacks(game, .1);
        Assert.Equal(180, first.Health);
        Assert.Equal(420, second.Health);
    }

    [Fact]
    public void MeleeRevalidatesTargetAtContactAndCancelsIfGuardDies()
    {
        var game = Founded(); var state = game.Settlement!;
        var guard = new SettlementDefender { Id = 1, X = 20, Y = 30, Kind = DefenderKind.Soldier };
        state.DefenderList.Add(guard);
        var enemy = Enemy(state, new(21, 30));
        var shot = new SiegeProjectile { Id = 1, Melee = true, SourceDefenderId = 1, TargetId = enemy.Id,
            Damage = 60, Windup = .3 };
        state.ProjectileList.Add(shot);
        state.AdvanceCommittedAttacks(game, .2);
        Assert.Equal(420, enemy.Health);
        guard.Health = 0;
        state.AdvanceCommittedAttacks(game, .2);
        Assert.Equal(420, enemy.Health);
        Assert.True(shot.Applied);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TowerDefenseArtwork.MaximumStations - 1)]
    public void CurrentNetworkSavePersistsPendingOrphanedAttackAndRejectsDuplicateIds(int station)
    {
        var game = Founded(); var state = game.Settlement!;
        state.NextAttackId = 2;
        state.ProjectileList.Add(new SiegeProjectile
        {
            Id = 1, SourceWallId = 99999, TargetId = 50, Kind = WallDefenseKind.OilCauldron, Damage = 180,
            StartX = 20, StartY = 30, EndX = 20, EndY = 31, WorldStartHeight = 7, WorldEndHeight = .03f,
            Windup = .3, TravelSeconds = 1.4, FootprintCenter = new(20, 31), FootprintForward = Vector2.UnitY,
            StationIndex = station, DropHeight = 6.97f, Elapsed = .5,
        });
        var json = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(json);
        Assert.Equal(json, SaveGameStore.Serialize(restored));
        Assert.Single(restored.Settlement!.Projectiles);
        var root = JsonNode.Parse(json)!;
        var attacks = root["Settlement"]!["Attacks"]!.AsArray();
        attacks.Add(attacks[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(root.ToJsonString()));
        root = JsonNode.Parse(json)!;
        root["Settlement"]!["Attacks"]![0]!["Station"] = TowerDefenseArtwork.MaximumStations;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(root.ToJsonString()));
    }

    [Theory]
    [InlineData(WallDefenseKind.OilCauldron)]
    [InlineData(WallDefenseKind.Rocks)]
    public void ExtraSideStationsReleaseAndApplyExactlyOneSavedContactStrike(WallDefenseKind kind)
    {
        var game = Founded();
        var state = game.Settlement!;
        var tower = state.Walls.First(w => w.IsTower && w.Cell.X == state.Bounds.X && w.Cell.Y == state.Bounds.Y);
        tower.Defense = kind;
        var footprint = DropAttackLayout.Footprints(tower, kind)[^1];
        var enemy = Enemy(state, footprint.Center - footprint.Forward * .25f);
        Assert.True(state.ReleaseMountedAttack(game, tower, kind));
        var shot = Assert.Single(state.Projectiles);
        Assert.Equal(TowerDefenseArtwork.MaximumStations - 1, shot.StationIndex);
        var saved = SavedAttack.Capture(shot);
        Assert.Equal(saved, SavedAttack.Capture(saved.Restore(game, state.NextAttackId)));
        state.AdvanceCommittedAttacks(game, shot.ContactSeconds + .01);
        Assert.Equal(420 - WallDefenses.Get(kind).Damage, enemy.Health);
        state.AdvanceCommittedAttacks(game, .3);
        Assert.Equal(420 - WallDefenses.Get(kind).Damage, enemy.Health);
    }

    [Fact]
    public void ArtilleryFalloffIsLocalAndWallsScreenVictims()
    {
        var game = Founded(); var state = game.Settlement!;
        var center = Enemy(state, new(20, 30), 1000);
        var edge = Enemy(state, new(22.5f, 30), 1000);
        var outside = Enemy(state, new(22.51f, 30), 1000);
        var shot = new SiegeProjectile { Kind = WallDefenseKind.Trebuchet, Damage = 360 };
        state.ArtilleryImpact(game, shot, new(20, .03f, 30));
        Assert.Equal(640, center.Health);
        Assert.Equal(910, edge.Health);
        Assert.Equal(1000, outside.Health);
        var wall = state.Walls.First(w => w.Cell.Y == state.Bounds.Y && !w.IsTower && w.Cell.X > state.Bounds.X + 4);
        var screened = Enemy(state, wall.Center + new Vector2(0, .8f), 1000);
        state.ArtilleryImpact(game, shot, new(wall.Center.X, .03f, wall.Center.Y - .8f));
        Assert.Equal(1000, screened.Health);
    }

    [Fact]
    public void CommittedMissilesCanMissAndDoNotTrackAChangedTargetPosition()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y);
        var target = Enemy(state, wall.Center + new Vector2(0, -6));
        Assert.True(state.ReleaseMountedAttack(game, wall, WallDefenseKind.Ballista));
        var shot = Assert.Single(state.Projectiles);
        double x = shot.EndX, y = shot.EndY;
        target.X += 5;
        state.AdvanceCommittedAttacks(game, 2);
        Assert.Equal(420, target.Health);
        Assert.Equal(x, shot.EndX); Assert.Equal(y, shot.EndY);
    }

    [Fact]
    public void DirectShotIsWithheldWhenAnotherWallScreensTheTarget()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => !w.IsTower && w.Cell.Y == state.Bounds.Y && w.Cell.X > state.Bounds.X + 4);
        var target = Enemy(state, wall.Center + new Vector2(0, -8));
        var blocker = WallDamageLayout.PreviewTower(wall.Center + new Vector2(0, -4), WallMaterial.Wood);
        blocker.Id = state.NextWallId;
        state.AddWall(blocker);
        Assert.False(state.ReleaseMountedAttack(game, wall, WallDefenseKind.Ballista));
        Assert.Empty(state.Projectiles);
        Assert.Equal(420, target.Health);
    }

    [Fact]
    public void FullReloadValuesAndAppliedAttackStateAreValidated()
    {
        var game = Founded(); var state = game.Settlement!;
        var wall = state.Walls.First(w => w.IsTower);
        game.HighestPopulation = game.HighestMilestone = 1000;
        state.WaveNumber = 1;
        wall.Defense = WallDefenseKind.Trebuchet; wall.DefenseCooldown = 14;
        string json = SaveGameStore.Serialize(game);
        Assert.Equal(14, SaveGameStore.Deserialize(json).Settlement!.Walls.First(w => w.Id == wall.Id).DefenseCooldown);
        var invalid = JsonNode.Parse(json)!;
        invalid["Settlement"]!["Walls"]![0]!["DefenseCooldown"] = 14.01;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(invalid.ToJsonString()));
    }
}
