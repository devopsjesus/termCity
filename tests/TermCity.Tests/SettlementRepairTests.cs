using System.Reflection;
using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementRepairTests
{
    private static void Set(object value, string property, object data) =>
        value.GetType().GetProperty(property)!.SetValue(value, data);

    private static List<SiegeEnemy> Attackers(SettlementState state) =>
        (List<SiegeEnemy>)typeof(SettlementState).GetField("EnemyList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private static CityGame Founded(bool populated = true, int population = 100)
    {
        var game = WorldScaleTests.Founded(populated ? population : 0);
        if (populated && population >= 100) Assert.True(game.Settlement!.WaveNumber > 0);
        Attackers(game.Settlement!).Clear();
        game.Speed = GameSpeed.Medium;
        game.Paused = false;
        return game;
    }

    [Fact]
    public void RepairCrewsStayHiddenUntilEveryEnemyIsRemovedWithoutPausingHealthRecovery()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.WallSections.First();
        wall.Health = wall.MaxHealth * .2;
        var first = new SiegeEnemy { Id = state.NextEnemyId++, X = 5, Y = 5, Health = 96, MaxHealth = 96 };
        var last = new SiegeEnemy { Id = state.NextEnemyId++, X = 6, Y = 5, Health = 96, MaxHealth = 96 };
        state.EnemyList.AddRange([first, last]);
        double before = wall.Health;
        state.AdvanceRepairs(game, .1);
        Assert.True(wall.Health > before);
        Assert.Same(wall, state.RepairingSection);
        Assert.True(wall.RepairAnimationActive);
        Assert.Null(state.VisibleRepairingSection);
        Assert.Null(state.VisibleRepairingWall);
        state.EnemyList.Remove(first);
        Assert.Null(state.VisibleRepairingSection);
        last.Health = 0;
        Assert.Null(state.VisibleRepairingSection);
        state.EnemyList.Clear();
        Assert.Same(wall, state.VisibleRepairingSection);
        wall.Health = wall.MaxHealth * .95;
        Assert.Same(wall, state.VisibleRepairingSection);
        state.AdvanceRepairs(game, 100);
        Assert.Equal(wall.MaxHealth, wall.Health);
        Assert.Null(state.VisibleRepairingSection);
    }

    [Fact]
    public void RepairAnimationStartsAtEightyPercentAndRemainsUntilFullHealth()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.WallSections.First();
        Set(wall, nameof(wall.Health), wall.MaxHealth * 0.95);
        double health = wall.Health;
        game.Update(.1);
        Assert.Equal(health + state.RepairRatePerSecond(game) * .1, wall.Health, 8);
        Assert.Equal(wall.Cell, state.RepairingWall);
        Assert.Null(state.VisibleRepairingWall);
        Set(wall, nameof(wall.Health), wall.MaxHealth * 0.800001);
        Assert.False(wall.RepairAnimationActive);
        Set(wall, nameof(wall.Health), wall.MaxHealth * 0.8);
        Assert.Equal(wall.Cell, state.VisibleRepairingWall);
        foreach (double fraction in new[] { 0.81, 0.85, 0.95, 0.999999 })
        {
            Set(wall, nameof(wall.Health), wall.MaxHealth * fraction);
            Assert.True(wall.RepairAnimationActive);
            Assert.Equal(wall.Cell, state.VisibleRepairingWall);
        }
        Set(wall, nameof(wall.Health), wall.MaxHealth);
        Assert.False(wall.RepairAnimationActive);
        Assert.Null(state.VisibleRepairingWall);
        Set(wall, nameof(wall.Health), wall.MaxHealth * .9);
        Assert.False(wall.RepairAnimationActive);
    }

    [Fact]
    public void RescalingMaximumHealthDoesNotInventARepairAnimation()
    {
        var game = Founded();
        var wall = game.Settlement!.WallSections.First();
        Set(wall, nameof(wall.Health), wall.MaxHealth * .95);
        Set(wall, nameof(wall.MaxHealth), wall.MaxHealth * 2);
        Set(wall, nameof(wall.Health), wall.MaxHealth * .95);
        Assert.False(wall.RepairAnimationActive);
        Set(wall, nameof(wall.Health), wall.MaxHealth * .8);
        Set(wall, nameof(wall.Health), wall.MaxHealth * .95);
        Set(wall, nameof(wall.MaxHealth), wall.MaxHealth * 2);
        Set(wall, nameof(wall.Health), wall.MaxHealth * .95);
        Assert.True(wall.RepairAnimationActive);
    }

    [Fact]
    public void SharedSectionsAndSavesRetainRepairAnimationHysteresis()
    {
        var game = Founded();
        var state = game.Settlement!;
        var member = state.Walls.First(w => w.SectionAnchor != w.Cell);
        Set(member, nameof(member.Health), member.MaxHealth * .8);
        game.Update(.1);
        Set(member, nameof(member.Health), member.MaxHealth * .95);
        Assert.All(state.Walls.Where(w => w.SectionAnchor == member.SectionAnchor),
            wall => Assert.True(wall.RepairAnimationActive));
        var save = SaveGameStore.Serialize(game);
        var loaded = SaveGameStore.Deserialize(save);
        Assert.Equal(save, SaveGameStore.Serialize(loaded));
        Assert.Equal(state.VisibleRepairingWall, loaded.Settlement!.VisibleRepairingWall);
        Assert.True(loaded.Settlement.Walls.First(w => w.Cell == member.Cell).RepairAnimationActive);

        var legacy = JsonNode.Parse(save)!;
        foreach (var wall in legacy["Settlement"]!["Walls"]!.AsArray())
            wall!.AsObject().Remove("RepairAnimationActive");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(legacy.ToJsonString()));
        Set(member, nameof(member.Health), member.MaxHealth);
        var invalid = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        invalid["Settlement"]!["Walls"]![0]!["RepairAnimationActive"] = true;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(invalid.ToJsonString()));
    }

    [Fact]
    public void RepairsAreGradualFreeAndKeepWallFeaturesAndMaterial()
    {
        var game = Founded();
        Set(game, "HighestPopulation", 250);
        Assert.True(game.UpgradeSettlementWalls().Success);
        SettlementUpgradeTests.AdvanceUpgrade(game, SettlementState.UpgradeSecondsPerSection *
            game.Settlement!.WallSections.Count());
        var state = game.Settlement!;
        var wall = state.Walls.Single(w => w.IsGate);
        Assert.True(game.PlaceWallDefense(wall.Cell, WallDefenseKind.Rocks).Success);
        Set(wall, "Health", 10d);
        int money = game.Money;
        int notifications = 0;
        game.Changed += () => notifications++;
        double rate = state.RepairRatePerSecond(game);
        game.Update(0.1);
        Assert.Equal(10 + rate * 0.1, wall.Health, 8);
        Assert.Equal(wall.Cell, state.RepairingWall);
        Assert.InRange(state.RepairAnimationPhase, 0, 1);
        Assert.Equal(money, game.Money);
        Assert.Equal(0, notifications);
        Assert.Equal(WallMaterial.Stone, state.Material);
        Assert.True(wall.IsGate);
        Assert.Equal(WallDefenseKind.Rocks, wall.Defense);
        Assert.Equal(450 * wall.SectionLength, wall.MaxHealth);
    }

    [Fact]
    public void BreachesComeFirstAndCrewFinishesChosenSectionBeforeMoving()
    {
        var game = Founded();
        var state = game.Settlement!;
        var sections = state.WallSections.ToArray();
        var first = sections[0];
        var breach = sections[1];
        var otherBreach = sections[2];
        Set(first, "Health", 1d);
        Set(breach, "Health", 0d);
        Set(otherBreach, "Health", 0d);
        game.Update(0.1);
        Assert.Equal(breach.Cell, state.RepairingWall);
        Assert.True(breach.Health > 0);
        Assert.Equal(1, first.Health);
        Assert.Equal(0, otherBreach.Health);
        for (int i = 0; i < 10; i++) game.Update(0.1);
        Assert.Equal(breach.Cell, state.RepairingWall);
        Assert.Equal(1, first.Health);
        Assert.Equal(0, otherBreach.Health);
        Set(breach, "Health", breach.MaxHealth - 0.01);
        game.Update(0.1);
        Assert.Equal(breach.MaxHealth, breach.Health);
        Assert.Equal(otherBreach.Cell, state.RepairingWall);
        Assert.True(otherBreach.Health > 0);
        Assert.Equal(1, first.Health);
    }

    [Fact]
    public void NextTargetUsesHealthPercentageRatherThanAbsoluteHealth()
    {
        var game = Founded();
        var state = game.Settlement!;
        var earlier = state.Walls[0];
        var lowerPercentage = state.Walls[1];
        Set(earlier, "Health", 100d);
        Set(lowerPercentage, "MaxHealth", 1_000d);
        Set(lowerPercentage, "Health", 150d);
        game.Update(0.1);
        Assert.Equal(lowerPercentage.Cell, state.RepairingWall);
        Assert.Equal(100, earlier.Health);
    }

    [Fact]
    public void LaborScalesWithPopulationAndCountsMultiCellWorkplacesAndServicesOnce()
    {
        var game = Founded();
        var state = game.Settlement!;
        double initial = state.RepairRatePerSecond(game);
        Assert.Equal(12 + .24 * game.Stats.Population +
            9 * game.Stats.Industrial.Occupied + 3 * game.Services.ActiveCounts.Sum(), initial, 8);
        var industrial = game.Map.Content.Buildings.ForZone(ZoneType.Industrial)!;
        var plot = new CellRect(3, 8, 2, 2);
        foreach (var cell in plot.Cells()) game.Map.SetZone(cell.X, cell.Y, ZoneType.Industrial);
        game.Map.SetBuildingFootprint(industrial, plot);
        game.Touch();
        Assert.Equal(initial + 9, state.RepairRatePerSecond(game), 8);
        var well = game.Map.Content.Buildings.Get("Town Well");
        game.Map.SetBuildingFootprint(well, new(3, 3, 3, 3));
        game.Touch();
        Assert.Equal(initial + 12, state.RepairRatePerSecond(game), 8);
        int home = game.Map.ZoneCells(ZoneType.Residential).First(i => game.Map.HouseholdLayer[i].Total > 0);
        var household = game.Map.HouseholdLayer[home];
        game.Map.SetHousehold(home % game.Map.Width, home / game.Map.Width, new(0, 0, 0));
        game.Touch();
        Assert.Equal(initial + 12 - .24 * household.Total, state.RepairRatePerSecond(game), 8);
    }

    [Fact]
    public void RepairsContinueInCombatButPauseAndAnEmptyTownDoNotRepair()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.Walls[0];
        Set(wall, "Health", 50d);
        game.Update(0.1);
        double health = wall.Health;
        game.Paused = true;
        game.Update(1);
        game.AdvanceDay();
        Assert.Equal(health, wall.Health);
        game.Paused = false;
        Set(state, "NextWaveSeconds", 0d);
        game.Update(.1);
        Assert.NotEmpty(state.Enemies);
        game.Speed = GameSpeed.Medium;
        game.Update(0.1);
        Assert.True(wall.Health > health);
        Assert.Equal(wall.Cell, state.RepairingWall);
        health = wall.Health;
        Attackers(state).Clear();
        foreach (int i in game.Map.ZoneCells(ZoneType.Residential).ToArray())
            game.Map.SetHousehold(i % game.Map.Width, i / game.Map.Width, new(0, 0, 0));
        game.Touch();
        Assert.Equal(0, state.RepairRatePerSecond(game));
        game.Update(0.1);
        Assert.Equal(health, wall.Health);
        Assert.Null(state.RepairingWall);
    }

    [Fact]
    public void BeforeTheFirstWaveNoRepairCrewIsInvented()
    {
        var game = Founded(population: 50);
        var state = game.Settlement!;
        Set(state.Walls[0], "Health", 0d);
        Assert.True(state.RepairRatePerSecond(game) > 0);
        game.Update(1);
        Assert.Equal(0, state.Walls[0].Health);
        Assert.Null(state.RepairingWall);
        Assert.Equal(0, state.RepairAnimationPhase);
    }

    [Fact]
    public void RepairsAdvanceWithCalendarDaysAndSaveLoadKeepsExactTargetAndProgress()
    {
        var game = Founded();
        var state = game.Settlement!;
        Set(state.Walls[0], "Health", 1d);
        Set(state.Walls[1], "Health", 6d);
        game.Update(.37);
        Set(state.Walls[1], "Health", 0d);
        string save = SaveGameStore.Serialize(game);
        var copy = SaveGameStore.Deserialize(save);
        Assert.Equal(save, SaveGameStore.Serialize(copy));
        Assert.Equal(state.Walls[0].Cell, copy.Settlement!.RepairingWall);
        Assert.Equal(state.RepairAnimationPhase, copy.Settlement.RepairAnimationPhase);
        double before = state.Walls[0].Health;
        game.AdvanceDay();
        copy.AdvanceDay();
        Assert.True(state.Walls[0].Health > before);
        for (int i = 0; i < 10; i++)
        {
            game.Update(.13);
            copy.Update(.13);
        }
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(copy));
    }

    [Fact]
    public void CompletionAndManualRepairsEndCrewWithoutOverHealingOrChargingAutomaticWork()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.Walls[0];
        Set(wall, "Health", wall.MaxHealth - .01);
        int money = game.Money;
        game.Update(.1);
        Assert.Equal(wall.MaxHealth, wall.Health);
        Assert.Equal(money, game.Money);
        Assert.Null(state.RepairingWall);
        Assert.Equal(0, state.RepairAnimationPhase);
        Set(wall, "Health", 1d);
        game.Update(.1);
        var paid = game.RepairSettlementWalls();
        Assert.True(paid.Success);
        Assert.Equal(money - paid.Cost, game.Money);
        Assert.Null(state.RepairingWall);
        Assert.Equal(0, state.RepairAnimationPhase);
        game.Update(.1);
        Assert.All(state.Walls, w => Assert.Equal(w.MaxHealth, w.Health));
        if (CityGame.DebugToolsAvailable)
        {
            Set(wall, "Health", 1d);
            game.Update(.1);
            Assert.True(game.DebugRepairAllBuildings().Success);
            Assert.Null(state.RepairingWall);
            Assert.Equal(0, state.RepairAnimationPhase);
        }
        var copy = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Null(copy.Settlement!.RepairingWall);
    }

    [Fact]
    public void StartingANewWaveDoesNotInterruptCivilianRepairs()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.Walls[0];
        Set(wall, "Health", 50d);
        game.Update(.1);
        double before = wall.Health;
        Set(state, "NextWaveSeconds", 0d);
        game.Update(.1);
        Assert.NotEmpty(state.Enemies);
        Assert.Equal(before + state.RepairRatePerSecond(game) * .1, wall.Health, 8);
        Assert.Equal(wall.Cell, state.RepairingWall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualRepairsDoNotLeaveAStaleTargetForLaterDamage(bool debug)
    {
        var game = Founded();
        var state = game.Settlement!;
        var previous = state.Walls[0];
        var breach = state.Walls[1];
        Set(previous, "Health", 1d);
        game.Update(.1);
        Assert.Equal(previous.Cell, state.RepairingWall);
        if (debug && !CityGame.DebugToolsAvailable) Assert.False(game.DebugRepairAllBuildings().Success);
        Assert.True((debug && CityGame.DebugToolsAvailable
            ? game.DebugRepairAllBuildings() : game.RepairSettlementWalls()).Success);
        Assert.Null(state.RepairingWall);
        Set(previous, "Health", 50d);
        Set(breach, "Health", 0d);
        game.Update(.1);
        Assert.Equal(breach.Cell, state.RepairingWall);
        Assert.Equal(50, previous.Health);
    }

    [Fact]
    public void RepairSaveFieldsAreConsistentAndStrictlyValidated()
    {
        var game = Founded();
        Set(game.Settlement!.Walls[0], "Health", 0d);
        game.Update(.1);
        string save = SaveGameStore.Serialize(game);
        var legacy = JsonNode.Parse(save)!;
        legacy["Settlement"]!.AsObject().Remove("RepairTarget");
        legacy["Settlement"]!.AsObject().Remove("RepairPhase");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(legacy.ToJsonString()));
        Action<JsonObject>[] corruptions =
        [
            s => s["RepairPhase"] = -0.1,
            s => s["RepairPhase"] = 1,
            s => s["RepairPhase"] = "not a phase",
            s => s["RepairPhase"] = null,
            s => s["RepairTarget"] = new JsonObject { ["X"] = -1, ["Y"] = -1 },
            s => s["RepairTarget"] = JsonSerializerNode(game.Settlement.Walls[1].Cell),
            s => s["RepairTarget"] = null,
            s => s["RepairTargetId"] = 0,
        ];
        foreach (var corrupt in corruptions)
        {
            var json = JsonNode.Parse(save)!;
            corrupt(json["Settlement"]!.AsObject());
            Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
        }
    }

    private static JsonObject JsonSerializerNode(Pos cell) => new() { ["X"] = cell.X, ["Y"] = cell.Y };
}
