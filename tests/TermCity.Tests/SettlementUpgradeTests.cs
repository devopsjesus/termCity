using System.Reflection;
using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementUpgradeTests
{
    private static void Set(object value, string property, object data) =>
        value.GetType().GetProperty(property)!.SetValue(value, data);

    internal static void AdvanceUpgrade(CityGame game, double seconds) =>
        typeof(SettlementState).GetMethod("AdvanceUpgrades", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game.Settlement, [game, seconds]);

    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 80, MapHeight = 72, StartingMoney = 500_000,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0,
            FloodChancePerWeek = 0, OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(15, 25, 16, 16)).Success);
        game.Paused = false;
        game.Speed = GameSpeed.Medium;
        return game;
    }

    private static void Purchase(CityGame game, int milestone = 250)
    {
        Set(game, "HighestPopulation", milestone);
        Assert.True(game.UpgradeSettlementWalls().Success);
    }

    [Fact]
    public void PurchaseIsAtomicMilestoneLockedAndCannotBeRepeated()
    {
        var game = Founded();
        var state = game.Settlement!;
        int money = game.Money;
        Assert.False(game.UpgradeSettlementWalls().Success);
        Assert.Equal(money, game.Money);
        Set(game, "HighestPopulation", 250);
        int cost = game.WallUpgradeCost;
        Set(game, "Money", cost - 1);
        Assert.False(game.UpgradeSettlementWalls().Success);
        Assert.Null(state.UpgradeTargetMaterial);
        Set(game, "Money", money);
        var purchase = game.UpgradeSettlementWalls();
        Assert.True(purchase.Success);
        Assert.Equal(cost, purchase.Cost);
        Assert.Equal(money - cost, game.Money);
        Assert.Equal(WallMaterial.Wood, state.Material);
        Assert.Equal(WallMaterial.Stone, state.UpgradeTargetMaterial);
        Assert.All(state.Walls, w => Assert.Equal(200 * w.SectionLength, w.MaxHealth));
        Set(game, "HighestPopulation", 1_000);
        Assert.False(game.UpgradeSettlementWalls().Success);
        Assert.Equal(money - cost, game.Money);
    }

    [Fact]
    public void WorkIsSequentialAndConvertsEachSharedHealthPoolExactlyOnce()
    {
        var game = Founded();
        var state = game.Settlement!;
        foreach (var wall in state.WallSections) Set(wall, "Health", 50d);
        var sections = state.WallSections.ToArray();
        Purchase(game);
        AdvanceUpgrade(game, 1.5);
        Assert.Equal(sections[0].Cell, state.UpgradingWall);
        Assert.Equal(.5, state.ProgressFor(sections[0]), 8);
        Assert.Equal(0, state.ProgressFor(sections[1]));
        Assert.Equal(WallMaterial.Wood, state.MaterialFor(sections[0]));
        Assert.Equal(.5 / sections.Length, state.OverallUpgradeProgress, 8);
        AdvanceUpgrade(game, 1.5);
        Assert.Equal(112.5, sections[0].Health);
        Assert.Equal(450, sections[0].MaxHealth);
        Assert.Equal(WallMaterial.Stone, state.MaterialFor(sections[0]));
        Assert.Equal(WallMaterial.Wood, state.MaterialFor(sections[1]));
        AdvanceUpgrade(game, sections.Length * SettlementState.UpgradeSecondsPerSection);
        Assert.Null(state.UpgradingWall);
        Assert.Null(state.UpgradeTargetMaterial);
        Assert.Equal(WallMaterial.Stone, state.Material);
        Assert.All(state.Walls, w =>
        {
            Assert.Equal(450 * w.SectionLength, w.MaxHealth);
            Assert.Equal(112.5, w.Health);
            Assert.Equal(WallMaterial.Stone, state.WallMaterialAt(w.Cell));
        });
        Assert.Contains(state.Walls, w => w.SectionLength == 3 && w.SectionAnchor != w.Cell);
        Assert.False(game.UpgradeSettlementWalls().Success);
    }

    [Fact]
    public void BreachesStayBreachedAndReinforcementIsAnotherPaidGradualPass()
    {
        var game = Founded();
        var state = game.Settlement!;
        var wall = state.WallSections.First();
        Set(wall, "Health", 0d);
        Purchase(game);
        AdvanceUpgrade(game, state.WallSections.Count() * SettlementState.UpgradeSecondsPerSection);
        Assert.Equal(0, wall.Health);
        Purchase(game, 1_000);
        Assert.Equal(WallMaterial.Stone, state.MaterialFor(wall));
        AdvanceUpgrade(game, 1);
        Assert.Equal(450, wall.MaxHealth);
        AdvanceUpgrade(game, 2);
        Assert.Equal(800, wall.MaxHealth);
        Assert.Equal(0, wall.Health);
        Assert.Equal(WallMaterial.ReinforcedStone, state.MaterialFor(wall));
        AdvanceUpgrade(game, state.WallSections.Count() * SettlementState.UpgradeSecondsPerSection);
        Assert.Equal(WallMaterial.ReinforcedStone, state.Material);
        Assert.False(game.UpgradeSettlementWalls().Success);
    }

    [Fact]
    public void PausingFreezesWorkAndPreWaveSettlementsCanBuildWithoutResidents()
    {
        var game = Founded();
        var state = game.Settlement!;
        typeof(SettlementState).GetMethod("BeginUpgrade", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(state, null);
        Assert.Equal(0, state.WaveNumber);
        Assert.Equal(0, game.Stats.Population);
        AdvanceUpgrade(game, .75);
        game.Paused = true;
        AdvanceUpgrade(game, 20);
        Assert.Equal(.25, state.ProgressFor(state.WallSections.First()), 8);
        game.Paused = false;
        AdvanceUpgrade(game, .75);
        Assert.Equal(.5, state.ProgressFor(state.WallSections.First()), 8);
    }

    [Fact]
    public void NormalSimulationAdvancesPurchasedWork()
    {
        var game = Founded();
        Purchase(game);
        game.Update(.1);
        Assert.True(game.Settlement!.OverallUpgradeProgress > 0);
    }

    [Fact]
    public void SaveAndUndoKeepPurchasedWorkAndResumeExactSectionProgress()
    {
        var game = Founded();
        Set(game, "HighestPopulation", 250);
        game.Update(.1);
        game.Paused = true;
        var session = new GameSession(game);
        int money = game.Money;
        Assert.True(session.UpgradeWalls().Success);
        game.Paused = false;
        AdvanceUpgrade(game, 4.25);
        game.Paused = true;
        string save = SaveGameStore.Serialize(game);
        var copy = SaveGameStore.Deserialize(save);
        Assert.Equal(save, SaveGameStore.Serialize(copy));
        Assert.Equal(game.Settlement!.UpgradingWall, copy.Settlement!.UpgradingWall);
        Assert.Equal(game.Settlement.OverallUpgradeProgress, copy.Settlement.OverallUpgradeProgress);
        game.Paused = copy.Paused = false;
        AdvanceUpgrade(game, .75);
        AdvanceUpgrade(copy, .75);
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(copy));
        game.Paused = true;
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Null(session.Game.Settlement!.UpgradeTargetMaterial);
        Assert.Equal(money, session.Game.Money);
        Assert.All(session.Game.Settlement.Walls, w => Assert.Equal(200 * w.SectionLength, w.MaxHealth));
    }

    [Fact]
    public void OldUngroupedSaveLoadsAndInvalidUpgradeProgressIsRejected()
    {
        var game = Founded();
        var root = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        var settlement = root["Settlement"]!;
        foreach (var wall in settlement["Walls"]!.AsArray())
        {
            wall!["Health"] = 200;
            wall["MaxHealth"] = 200;
        }
        foreach (string field in new[] { "GroupedWallSections", "UpgradeTargetMaterial", "UpgradedSections", "UpgradeSectionSeconds" })
            settlement.AsObject().Remove(field);
        var copy = SaveGameStore.Deserialize(root.ToJsonString());
        Assert.Null(copy.Settlement!.UpgradeTargetMaterial);
        Assert.All(copy.Settlement.Walls, w => Assert.Equal(WallMaterial.Wood, copy.Settlement.MaterialFor(w)));
        Set(game, "HighestPopulation", 250);
        game.Update(.1);
        Assert.True(game.UpgradeSettlementWalls().Success);
        root = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        root["Settlement"]!["UpgradeSectionSeconds"] = 3;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void StoneCoursesGrowOverExistingWoodAndRespectDamageAndGateOpening()
    {
        var game = Founded();
        var state = game.Settlement!;
        Purchase(game);
        var first = state.WallSections.First();
        Assert.Empty(WallUpgradeArtwork.Blocks(state, first));
        AdvanceUpgrade(game, .75);
        var early = WallUpgradeArtwork.Blocks(state, first).ToArray();
        Assert.NotEmpty(early);
        AdvanceUpgrade(game, 1.5);
        var later = WallUpgradeArtwork.Blocks(state, first).ToArray();
        Assert.True(later.Max(b => b.Center.Y + b.Size.Y / 2) >
            early.Max(b => b.Center.Y + b.Size.Y / 2));
        Set(first, "Health", 0d);
        Assert.Empty(WallUpgradeArtwork.Blocks(state, first));
        Assert.Equal(WallMaterial.Wood, state.MaterialFor(first));
        Assert.Null(state.WallMaterialAt(new Pos(-1, -1)));
        Assert.Equal(0, state.WallUpgradeProgressAt(new Pos(-1, -1)));
    }

    [Fact]
    public void GateCoursesLeaveTheDoorwayOpenAndIronIsLaidOverStone()
    {
        var game = Founded();
        var state = game.Settlement!;
        Purchase(game);
        var sections = state.WallSections.ToArray();
        int gateIndex = Array.FindIndex(sections, w => w.IsGate);
        var gate = sections[gateIndex];
        AdvanceUpgrade(game, (gateIndex + .5) * SettlementState.UpgradeSecondsPerSection);
        var gateBlocks = WallUpgradeArtwork.Blocks(state, gate).ToArray();
        Assert.NotEmpty(gateBlocks);
        Assert.All(gateBlocks, block => Assert.True(
            block.Center.Y - block.Size.Y / 2 >= 1.3f ||
            block.Center.X - block.Size.X / 2 >= .27f ||
            block.Center.X + block.Size.X / 2 <= -.27f));
        AdvanceUpgrade(game, sections.Length * SettlementState.UpgradeSecondsPerSection);
        Purchase(game, 1_000);
        AdvanceUpgrade(game, 1.5);
        Assert.Equal(WallMaterial.Stone, state.MaterialFor(sections[0]));
        var iron = WallUpgradeArtwork.Blocks(state, sections[0]).ToArray();
        Assert.NotEmpty(iron);
        Assert.All(iron, block => Assert.Equal(0x586b71, block.Color));
        AdvanceUpgrade(game, sections.Length * SettlementState.UpgradeSecondsPerSection);
        Assert.NotEmpty(WallUpgradeArtwork.Blocks(state, sections[0]));
    }
}
