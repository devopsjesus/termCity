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

    private static List<Goblin> Attackers(SettlementState state) =>
        (List<Goblin>)typeof(SettlementState).GetField("GoblinList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private static CityGame Founded(bool populated = true, int population = 100)
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 80, MapHeight = 72, StartingMoney = 50_000,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0,
            FloodChancePerWeek = 0, OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(15, 25, 32, 32)).Success);
        if (populated)
        {
            int remaining = population;
            var home = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
            foreach (var plot in game.VacantPlots(ZoneType.Residential))
            {
                if (remaining == 0) break;
                int people = Math.Min(remaining, home.Capacity);
                game.Map.SetBuildingFootprint(home, plot);
                game.Map.SetHousehold(plot.X, plot.Y,
                    new Household((byte)Math.Min(4, people), (byte)Math.Max(0, people - 4), 0));
                remaining -= people;
            }
            Assert.Equal(0, remaining);
            game.Touch();
            if (population >= 100) Assert.True(game.Settlement!.WaveNumber > 0);
            Attackers(game.Settlement!).Clear();
        }
        game.Speed = GameSpeed.Medium;
        game.Paused = false;
        return game;
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
        Assert.Equal(450, wall.MaxHealth);
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
        Assert.Equal(1 + .02 * game.Stats.Population +
            .75 * game.Stats.Industrial.Occupied + .25 * game.Services.ActiveCounts.Sum(), initial, 8);
        var industrial = game.Map.Content.Buildings.ForZone(ZoneType.Industrial)!;
        var plot = new CellRect(3, 8, 2, 2);
        foreach (var cell in plot.Cells()) game.Map.SetZone(cell.X, cell.Y, ZoneType.Industrial);
        game.Map.SetBuildingFootprint(industrial, plot);
        game.Touch();
        Assert.Equal(initial + .75, state.RepairRatePerSecond(game), 8);
        var well = game.Map.Content.Buildings.Get("Town Well");
        game.Map.SetBuildingFootprint(well, new(3, 3, 3, 3));
        game.Touch();
        Assert.Equal(initial + 1, state.RepairRatePerSecond(game), 8);
        int home = game.Map.ZoneCells(ZoneType.Residential).First(i => game.Map.HouseholdLayer[i].Total > 0);
        var household = game.Map.HouseholdLayer[home];
        game.Map.SetHousehold(home % game.Map.Width, home / game.Map.Width, new(0, 0, 0));
        game.Touch();
        Assert.Equal(initial + 1 - .02 * household.Total, state.RepairRatePerSecond(game), 8);
    }

    [Fact]
    public void CombatPauseAndAnEmptyTownDoNotRepair()
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
        Assert.NotEmpty(state.Goblins);
        game.Speed = GameSpeed.Medium;
        game.Update(0.1);
        Assert.Equal(health, wall.Health);
        Assert.Null(state.RepairingWall);
        Assert.Equal(0, state.RepairAnimationPhase);
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
    public void StartingANewWaveDoesNotRepairOrDamageWallsAtTheBoundary()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.Walls[0];
        Set(wall, "Health", 50d);
        game.Update(.1);
        double before = wall.Health;
        Set(state, "NextWaveSeconds", 0d);
        game.Update(.1);
        Assert.NotEmpty(state.Goblins);
        Assert.Equal(before, wall.Health);
        Assert.Null(state.RepairingWall);
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
    public void OptionalRepairSaveFieldsAreBackwardCompatibleAndStrictlyValidated()
    {
        var game = Founded();
        Set(game.Settlement!.Walls[0], "Health", 0d);
        game.Update(.1);
        string save = SaveGameStore.Serialize(game);
        var legacy = JsonNode.Parse(save)!;
        legacy["Settlement"]!.AsObject().Remove("RepairTarget");
        legacy["Settlement"]!.AsObject().Remove("RepairPhase");
        var loaded = SaveGameStore.Deserialize(legacy.ToJsonString());
        Assert.Null(loaded.Settlement!.RepairingWall);
        loaded.Update(.1);
        Assert.NotNull(loaded.Settlement.RepairingWall);
        Action<JsonObject>[] corruptions =
        [
            s => s["RepairPhase"] = -0.1,
            s => s["RepairPhase"] = 1,
            s => s["RepairPhase"] = "not a phase",
            s => s["RepairPhase"] = null,
            s => s["RepairTarget"] = new JsonObject { ["X"] = -1, ["Y"] = -1 },
            s => s["RepairTarget"] = JsonSerializerNode(game.Settlement.Walls[1].Cell),
            s => s["RepairTarget"] = null,
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
