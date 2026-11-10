using System.Text.Json.Nodes;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WorldScaleTests
{
    [Fact]
    public void EveryEnclosureSizeKeepsCornerTowerMeshesSeparateAndApproachesCovered()
    {
        for (int size = SettlementState.MinimumEnclosureSize; size <= SettlementState.MaximumEnclosureSize; size++)
            foreach (var side in Enum.GetValues<BaileyGateSide>())
            {
                var bounds = new CellRect(10, 10, size, 12 + (size * 7) % 149);
                var siege = new SettlementState(bounds, side);
                var towers = siege.Walls.Where(w => w.IsTower).ToArray();
                for (int a = 0; a < towers.Length; a++)
                    for (int b = a + 1; b < towers.Length; b++)
                    {
                        var first = WallDamageLayout.HalfSize(towers[a]) + new System.Numerics.Vector2(.02f);
                        var second = WallDamageLayout.HalfSize(towers[b]) + new System.Numerics.Vector2(.02f);
                        Assert.False(Math.Abs(towers[a].Cell.X - towers[b].Cell.X) < first.X + second.X &&
                            Math.Abs(towers[a].Cell.Y - towers[b].Cell.Y) < first.Y + second.Y,
                            $"Tower meshes overlap in {bounds}, gate={side}: {towers[a].Cell}, {towers[b].Cell}.");
                    }
                Assert.All(siege.Walls, wall => Assert.Contains(towers, tower =>
                    Math.Sqrt(Math.Pow(tower.Cell.X - wall.Cell.X, 2) + Math.Pow(tower.Cell.Y - wall.Cell.Y, 2)) <=
                        WallDefenses.Get(WallDefenseKind.None).Range));
            }
    }

    [Fact]
    public void ScenarioLayoutDefinitionsAreCopiedWithoutMutatingTheLiveRegistry()
    {
        var current = new GameContent();
        var extension = current.Buildings.Register(new BuildingType
        {
            Name = "Custom shrine", Glyphs = ["+"], Foreground = Rgb.Hex(0xffffff),
            PlayerPlaceable = true, Cost = 777,
        });
        var legacy = GameContent.ForScenario(CityScenario.Chicago, current);
        Assert.Equal(5, current.Buildings.Get("Cottage").Width);
        Assert.Equal(1, legacy.Buildings.Get("Cottage").Width);
        Assert.Equal(777, legacy.Buildings.Get(extension.Name).Cost);
        Assert.Equal(extension.Id, legacy.Buildings.Get(extension.Name).Id);
        Assert.Equal(4, current.Roads.Get("King's Road").WidthCells);
        Assert.Equal(2, legacy.Roads.Get("King's Road").WidthCells);
        var restored = GameContent.ForScenario(CityScenario.Random, legacy);
        Assert.Equal(5, restored.Buildings.Get(extension.Name).Width);
        Assert.Equal(1, legacy.Buildings.Get(extension.Name).Width);
        Assert.Same(current, GameContent.ForScenario(CityScenario.Random, current));
    }

    [Fact]
    public void LargeSpeciesWaitWhenOnlyAGoblinSizedApproachRemains()
    {
        var game = Founded(1_000);
        var siege = game.Settlement!;
        siege.OrcsIntroduced = false;
        int lane = siege.Bounds.Y + siege.Bounds.Height / 2;
        var water = game.Map.Content.Terrains.Get("Water");
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
                if (!siege.Bounds.Contains(x, y) && !(x > siege.Bounds.Right && y == lane))
                    game.Map.SetTerrain(x, y, water);
        siege.EnemyList.Clear();
        siege.PlannedOrcWave = siege.PlannedTrollWave = siege.WaveNumber + 1;
        siege.PlannedOrcSide = BaileyGateSide.Right;
        game.Touch();
        Assert.True(siege.SpawnWave(game));
        Assert.NotEmpty(siege.Enemies);
        Assert.All(siege.Enemies, enemy => Assert.Equal(SiegeEnemyKind.Goblin, enemy.Kind));
        Assert.True(siege.PlannedTrollWave > siege.WaveNumber);
        Assert.True(siege.PlannedOrcWave > siege.WaveNumber);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(
            SaveGameStore.Deserialize(SaveGameStore.Serialize(game))));
    }

    [Fact]
    public void ForcedGoblinWavesDeferPendingMonsterIntroductionsAndRemainLoadable()
    {
        var game = Founded(1_000);
        var siege = game.Settlement!;
        siege.EnemyList.Clear();
        siege.OrcsIntroduced = false;
        siege.Advance(game, 0.1);
        Assert.True(siege.SpawnWave(game, 24));
        Assert.All(siege.Enemies, enemy => Assert.Equal(SiegeEnemyKind.Goblin, enemy.Kind));
        Assert.True(siege.PlannedTrollWave > siege.WaveNumber);
        Assert.True(siege.PlannedOrcWave > siege.WaveNumber);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(
            SaveGameStore.Deserialize(SaveGameStore.Serialize(game))));
    }

    [Fact]
    public void TowerReservationsLeaveGridAlignedHumanAccessAndGatesHaveRealDecks()
    {
        var game = Founded(0);
        var siege = game.Settlement!;
        var tower = siege.Walls.First(w => w.IsTower && w.Cell.X == siege.Bounds.Right &&
            w.Cell.Y > siege.Bounds.Y + 5 && w.Cell.Y < siege.Bounds.Bottom - 5);
        var access = tower.Cell.Offset(-3, 0);
        Assert.False(game.CanBuildOn(access.X, access.Y));
        Assert.False(WallDamageLayout.BlocksGround(tower, access.X + 0.5, access.Y + 0.5, WorldScale.HumanRadius));
        var approach = siege.Walls.Single(w => w.Cell == tower.Cell.Offset(0, 1));
        Assert.True(WallDamageLayout.ActorWalkHeight(approach, game.Map) > WallDamageLayout.WalkHeight(approach));
        Assert.Equal(WallDamageLayout.ActorWalkHeight(tower, game.Map),
            WallDamageLayout.ActorWalkHeight(approach, game.Map));
        var gate = siege.Walls.Single(w => w.IsGate);
        Assert.False(WallDamageLayout.BlocksGround(gate, gate.Cell.X + 0.5, gate.Cell.Y + 0.5,
            WorldScale.HumanRadius, throughGate: true));
        Assert.False(WallDamageLayout.BlocksGround(gate, gate.Cell.X + 0.5, gate.Cell.Y + 1.5,
            WorldScale.HumanRadius, throughGate: true));
        Assert.True(WallDamageLayout.BlocksGround(gate, gate.Cell.X + 0.5, gate.Cell.Y + 2.2,
            WorldScale.HumanRadius, throughGate: true));
        Assert.Contains(WallDamageLayout.Fragments(gate), part => part.Center.X == 0 &&
            part.Center.Z == WallDamageLayout.WalkwayOffset(gate) &&
            Math.Abs(part.Center.Y + part.Size.Y / 2 - WallDamageLayout.WalkHeight(gate)) < 0.00001);
    }

    internal static CityGame Founded(int population = 100)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 160, StartingMoney = 500_000,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0, FloodChancePerWeek = 0, OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray()) game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        var result = game.FoundSettlement(new(16, 16, 128, 128));
        Assert.True(result.Success, result.Message);
        var home = game.Map.Content.Buildings.Get("Cottage");
        foreach (var area in game.VacantPlots(ZoneType.Residential))
        {
            if (population == 0) break;
            int people = Math.Min(home.Capacity, population);
            game.Map.SetBuildingFootprint(home, area);
            game.Map.SetHousehold(area.X, area.Y,
                new Household((byte)Math.Min(4, people), (byte)Math.Max(0, people - 4), 0));
            population -= people;
        }
        Assert.Equal(0, population);
        game.Map.BuildingEstablishedDays.Clear();
        game.Touch();
        game.Paused = false;
        return game;
    }

    [Fact]
    public void AnatomyIsIndependentOfCombatTier()
    {
        for (int tier = 1; tier <= 5; tier++)
        {
            Assert.Equal(1.0125, SiegeEnemies.Goblin.BodyScale(tier) * 1.35, 6);
            Assert.Equal(6, SiegeEnemies.Orc.BodyScale(tier) * 1.35 / WorldScale.UnitsPerFoot, 5);
            Assert.Equal(96 + 36 * (tier - 1), SiegeEnemies.Goblin.MaxHealth(tier, 1));
            Assert.Equal(420 + 65 * (tier - 2), SiegeEnemies.Orc.MaxHealth(tier, 1));
            Assert.Equal(1, SiegeEnemies.Goblin.AttackIntervalSeconds);
            Assert.Equal(1, SiegeEnemies.Orc.AttackIntervalSeconds);
        }
    }

    [Theory]
    [InlineData(SiegeEnemyKind.TreeTroll)]
    [InlineData(SiegeEnemyKind.StoneTroll)]
    [InlineData(SiegeEnemyKind.MudTroll)]
    public void TrollEmittedAnatomyMatchesItsSavedPhysicalHeight(SiegeEnemyKind kind)
    {
        foreach (int seed in new[] { 0, 150, 300, 999_999 })
        {
            var enemy = new SiegeEnemy { Kind = kind, Tier = 4, AppearanceSeed = seed };
            var parts = TrollArtwork.Parts(enemy);
            double top = parts.Max(p => p.Center.Y + p.Size.Y / 2) * enemy.BodyScale;
            Assert.InRange(enemy.HeightFeet, 8, 11);
            Assert.Equal(enemy.HeightFeet, top / WorldScale.UnitsPerFoot, 4);
            Assert.Equal(parts, TrollArtwork.Parts(enemy));
            Assert.All(parts, p => Assert.True(p.Center.Y - p.Size.Y / 2 >= -0.00001f));
        }
    }

    [Fact]
    public void WallsFollowMaterialAndReserveFutureUpgradeSpace()
    {
        var game = Founded();
        var siege = game.Settlement!;
        var wall = siege.Walls.First(w => !w.IsGate && !w.IsTower && !w.IsGateWing);
        foreach (var (material, feet) in new[] { (WallMaterial.Wood, 20), (WallMaterial.Stone, 26),
                     (WallMaterial.ReinforcedStone, 30) })
        {
            siege.Material = material;
            Assert.Equal(WorldScale.Feet(feet), WallDamageLayout.NominalHeight(wall));
            Assert.True(WallDamageLayout.Fragments(wall).Max(p => p.Center.Y + p.Size.Y / 2) >= WorldScale.Feet(feet));
        }
        Assert.False(game.CanBuildOn(wall.Cell.X, wall.Cell.Y + 1));
        Assert.Equal(3, siege.Walls.Single(w => w.IsGate).SectionLength);
    }

    [Fact]
    public void LargeNewSettlementReachesTrollMilestoneAndRoundTripsAllSpecies()
    {
        var game = Founded(1_000);
        Assert.Equal(1_000, game.Stats.Population);
        Assert.Equal(1_000, game.HighestPopulation);
        var siege = game.Settlement!;
        siege.Advance(game, 0.1);
        Assert.False(siege.TrollsIntroduced);
        Assert.True(siege.TrollApproachExpected);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(
            SaveGameStore.Deserialize(SaveGameStore.Serialize(game))));
        siege.EnemyList.Clear();
        siege.ProjectileList.Clear();
        siege.NextWaveSeconds = 0;
        siege.Advance(game, 0.1);
        Assert.True(siege.TrollsIntroduced);
        Assert.Equal(3, siege.Enemies.Count(e => e.Definition.IsTroll));
        Assert.InRange(siege.Enemies.Count, 1, SettlementState.MaximumEnemySwarm);
        Assert.Equal(3, siege.Enemies.Where(e => e.Definition.IsTroll).Select(e => e.Kind).Distinct().Count());
        for (int tick = 0; tick < 80; tick++)
        {
            siege.Advance(game, 0.1);
            Assert.NotEmpty(siege.Enemies);
            foreach (var enemy in siege.Enemies)
            {
                Assert.False(siege.ContainsInterior(new((int)enemy.X, (int)enemy.Y)));
                foreach (var other in siege.Enemies.Where(e => e.Id > enemy.Id))
                    Assert.True(Math.Pow(enemy.X - other.X, 2) + Math.Pow(enemy.Y - other.Y, 2) + 1e-8 >=
                        Math.Pow(enemy.CollisionRadius + other.CollisionRadius, 2));
            }
        }
        string saved = SaveGameStore.Serialize(game);
        var json = JsonNode.Parse(saved)!["Settlement"]!;
        Assert.NotNull(json["Goblins"]);
        Assert.NotNull(json["NextGoblinId"]);
        Assert.Null(json["Enemies"]);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        Assert.True(restored.Settlement!.TrollsIntroduced);
        var troll = json["Goblins"]!.AsArray().First(node =>
            node!["Kind"]!.GetValue<string>() == nameof(SiegeEnemyKind.TreeTroll))!;
        troll["AppearanceSeed"] = -1;
        var invalid = JsonNode.Parse(saved)!;
        invalid["Settlement"] = json.DeepClone();
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(invalid.ToJsonString()));
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1_000, true)]
    public void LateTrollUnlockDefersIntroductionByOneFullWave(int population, bool expected)
    {
        var game = Founded(population);
        var siege = game.Settlement!;
        siege.NextWaveSeconds = 10;
        siege.Advance(game, 0.1);
        Assert.Equal(expected, siege.PlannedTrollWave > 0);
        if (expected) Assert.Equal(siege.WaveNumber + 2, siege.PlannedTrollWave);
        Assert.DoesNotContain(siege.Enemies, enemy => enemy.Definition.IsTroll);
    }

    [Fact]
    public void BuildingsReserveRealPlotsAndRoadWidthsHavePhysicalOrdering()
    {
        var game = Founded();
        var type = game.Map.Content.Buildings.Get("Cottage");
        Assert.Equal(5, type.Width);
        Assert.Equal(5, type.Height);
        Assert.All(game.Map.BuildingFootprints.Values, area => Assert.True(area.Area >= 25));
        Assert.Equal(new float[] { 1, 2, 4 }, game.Map.Content.Roads.OrderBy(r => r.Rank).Select(r => r.WidthCells));
    }
}
