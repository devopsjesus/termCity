using System.Text.Json.Nodes;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class DezoneTests
{
    private static readonly Pos Cell = new(10, 18);
    private static readonly CellRect Area = CellRect.Single(Cell);

    private static CityGame Occupied(ZoneType zone = ZoneType.Residential, GameConfig? config = null)
    {
        var game = TestCity.Flat(config: config);
        game.Designate(Area, zone);
        game.Map.SetBuilding(Cell.X, Cell.Y, game.Map.Content.Buildings.ForZone(zone)!);
        if (zone == ZoneType.Residential)
        {
            game.Map.SetHousehold(Cell.X, Cell.Y, new Household(2, 2, 1));
        }

        game.Touch();
        return game;
    }

    [Fact]
    public void VacantZonesDisappearImmediatelyWithoutTouchingRoadsFeaturesMoneyOrRandomState()
    {
        var game = TestCity.Flat();
        game.Designate(new CellRect(10, 18, 2, 1), ZoneType.Residential);
        var feature = game.Map.Content.Features.First(f => f.Id != 0);
        game.Map.SetFeature(10, 18, feature);
        game.Touch();
        int money = game.Money;
        ulong rng = game.Rng.State;
        Assert.Equal(2, game.Dezone(new CellRect(10, 18, 2, 3)).Cells);
        Assert.Equal(ZoneType.None, game.Map.ZoneAt(10, 18));
        Assert.Equal(feature, game.Map.FeatureAt(10, 18));
        Assert.True(game.Map.HasRoad(10, 20));
        Assert.Empty(game.Map.ZoneRemovals);
        Assert.Equal(0, game.Stats.Residential.Zoned);
        Assert.Equal(money, game.Money);
        Assert.Equal(rng, game.Rng.State);
        Assert.False(game.Dezone(Area).Success);
        Assert.Equal(rng, game.Rng.State);
    }

    [Theory]
    [InlineData(ZoneType.Residential, 10, 5)]
    [InlineData(ZoneType.Commercial, 18, 0)]
    [InlineData(ZoneType.Industrial, 25, 0)]
    public void OccupiedBuildingsKeepResidentsAndTaxesUntilTheirDeadline(ZoneType zone, int income, int population)
    {
        var game = Occupied(zone);
        var building = game.Map.BuildingAt(Cell.X, Cell.Y);
        var terrain = game.Map.TerrainAt(Cell.X, Cell.Y);
        int money = game.Money;
        Assert.True(game.Dezone(Area).Success);
        var removal = game.Map.ZoneRemovalAt(Cell.X, Cell.Y)!;
        Assert.InRange(removal.RemoveAtDay, 14, 21);
        Assert.Equal(zone, removal.Zone);
        Assert.Equal(ZoneType.None, game.Map.ZoneAt(Cell.X, Cell.Y));
        Assert.Equal(building, game.Map.BuildingAt(Cell.X, Cell.Y));
        Assert.Equal(new ZoneCount(0, 0, 0, 1), game.Stats.For(zone));
        Assert.Equal(population, game.Stats.Population);
        Assert.Equal(income, game.Stats.WeeklyIncome);
        Assert.Contains("Unzoned: removal in", CellInspector.Summary(game, Cell));

        while (game.ElapsedDays < removal.RemoveAtDay - 1)
        {
            game.AdvanceDay();
            Assert.Equal(building, game.Map.BuildingAt(Cell.X, Cell.Y));
            Assert.Equal(population, game.Stats.Population);
        }

        int version = game.MapVersion;
        game.AdvanceDay();
        Assert.Null(game.Map.BuildingAt(Cell.X, Cell.Y));
        Assert.Null(game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        Assert.True(game.Map.HouseholdAt(Cell.X, Cell.Y).IsEmpty);
        Assert.Equal(terrain, game.Map.TerrainAt(Cell.X, Cell.Y));
        Assert.Equal(0, game.Stats.Population);
        Assert.Equal(0, game.Stats.WeeklyIncome);
        Assert.True(game.MapVersion > version);
        Assert.True(game.Money > money);
    }

    [Fact]
    public void RandomDelayCoversInclusiveFourteenToTwentyOneDaysForEachBuilding()
    {
        var game = TestCity.Flat();
        var area = new CellRect(10, 18, 64, 1);
        game.Designate(area, ZoneType.Residential);
        foreach (var p in area.Cells())
        {
            game.Map.SetBuilding(p.X, p.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
        }

        game.Touch();
        game.Dezone(area);
        Assert.Equal(64, game.Map.ZoneRemovals.Count);
        var deadlines = game.Map.ZoneRemovals.Values.Select(r => r.RemoveAtDay).ToArray();
        Assert.All(deadlines, d => Assert.InRange(d, 14, 21));
        Assert.Contains(14d, deadlines);
        Assert.Contains(21d, deadlines);
        for (int day = 1; day <= 21; day++)
        {
            game.AdvanceDay();
            Assert.Equal(deadlines.Count(d => d > day), game.Map.ZoneRemovals.Count);
        }
    }

    [Fact]
    public void RemovalHonorsFractionalActionTimeAndPausedUpdates()
    {
        var game = Occupied(config: new GameConfig { MediumSecondsPerWeek = 7 });
        game.Update(0.5);
        Assert.Equal(0.5, game.ElapsedDays);
        game.Dezone(Area);
        double deadline = game.Map.ZoneRemovalAt(Cell.X, Cell.Y)!.RemoveAtDay;
        game.Paused = true;
        for (int i = 0; i < 100; i++) game.Update(0.5);
        Assert.Equal(0.5, game.ElapsedDays);
        Assert.NotNull(game.Map.BuildingAt(Cell.X, Cell.Y));
        game.Paused = false;
        while (game.ElapsedDays < deadline - 0.5) game.Update(0.5);
        Assert.NotNull(game.Map.BuildingAt(Cell.X, Cell.Y));
        game.Update(0.5);
        Assert.Equal(deadline, game.ElapsedDays);
        Assert.Null(game.Map.BuildingAt(Cell.X, Cell.Y));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    public void CountdownUsesConfiguredGameWeekLength(int days)
    {
        var game = Occupied(config: new GameConfig { DaysPerWeek = days });
        game.Dezone(Area);
        Assert.InRange(game.Map.ZoneRemovalAt(Cell.X, Cell.Y)!.RemoveAtDay, 2 * days, 3 * days);
    }

    [Fact]
    public void RestoringOriginalZoneCancelsRemovalButDifferentOccupiedZoneIsBlocked()
    {
        var game = Occupied();
        game.Dezone(Area);
        var removal = game.Map.ZoneRemovalAt(Cell.X, Cell.Y);
        game.AdvanceWeek();
        Assert.False(game.Designate(Area, ZoneType.Industrial).Success);
        Assert.Equal(removal, game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        Assert.True(game.Designate(Area, ZoneType.Residential).Success);
        Assert.Null(game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        Assert.Equal(1, game.Stats.Residential.Filled);
        Assert.Equal(0, game.Stats.Residential.AwaitingRemoval);
        TestCity.Advance(game, 4);
        Assert.NotNull(game.Map.BuildingAt(Cell.X, Cell.Y));
        Assert.Equal(5, game.Stats.Population);
    }

    [Fact]
    public void RestoringVacantLandCanUseAnyZoneAndNoneDesignationUsesDezoningRules()
    {
        var game = Occupied();
        Assert.True(game.Designate(Area, ZoneType.None).Success);
        Assert.NotNull(game.Map.BuildingAt(Cell.X, Cell.Y));
        Assert.NotNull(game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        game.Demolish(Area);
        Assert.True(game.Designate(Area, ZoneType.Commercial).Success);
        Assert.Equal(ZoneType.Commercial, game.Map.ZoneAt(Cell.X, Cell.Y));
        Assert.Empty(game.Map.ZoneRemovals);
    }

    [Fact]
    public void RemovalQueueIsClearedWhenBuildingIsDemolishedOrReplaced()
    {
        var game = Occupied();
        game.Dezone(Area);
        game.Demolish(Area);
        Assert.Empty(game.Map.ZoneRemovals);
        game.Designate(Area, ZoneType.Residential);
        game.Map.SetBuilding(Cell.X, Cell.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
        game.Touch();
        game.Dezone(Area);
        game.Map.SetBuilding(Cell.X, Cell.Y, game.Map.Content.Buildings.ForZone(ZoneType.Commercial)!);
        game.Touch();
        Assert.Empty(game.Map.ZoneRemovals);
        TestCity.Advance(game, 4);
        Assert.NotNull(game.Map.BuildingAt(Cell.X, Cell.Y));
    }

    [Fact]
    public void PendingHomesStillUnlockGrowthAndPendingBusinessesStillUseCapacity()
    {
        var game = TestCity.Flat();
        var homes = new CellRect(10, 18, 10, 1);
        game.Designate(homes, ZoneType.Residential);
        foreach (var p in homes.Cells())
        {
            game.Map.SetBuilding(p.X, p.Y, game.Map.Content.Buildings.ForZone(ZoneType.Residential)!);
            game.Map.SetHousehold(p.X, p.Y, new Household(2, 2, 0));
        }

        var shop = CellRect.Single(new Pos(30, 19));
        game.Designate(shop, ZoneType.Commercial);
        game.Map.SetBuilding(30, 19, game.Map.Content.Buildings.ForZone(ZoneType.Commercial)!);
        game.Designate(CellRect.Single(new Pos(31, 19)), ZoneType.Commercial);
        game.Touch();
        game.Dezone(homes);
        game.Dezone(shop);
        Assert.Equal(10, game.Stats.Households);
        Assert.Equal(40, game.Stats.Population);
        Assert.Equal(1, game.SupportedCells(ZoneType.Commercial));
        Assert.Equal(GrowthStatus.CapacityReached, GrowthDiagnostics.ForZone(game, ZoneType.Commercial).Status);
        game.AdvanceWeek();
        Assert.Null(game.Map.BuildingAt(31, 19));
        Assert.Equal(1, game.Stats.Commercial.Occupied);
    }

    [Fact]
    public void SaveLoadPreservesCountdownOccupantsTaxesAndFutureSimulation()
    {
        var game = Occupied();
        game.Dezone(Area);
        game.AdvanceWeek();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(game.Map.ZoneRemovalAt(Cell.X, Cell.Y), loaded.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        Assert.Equal(game.Stats, loaded.Stats);
        Assert.Equal(game.Rng.State, loaded.Rng.State);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(game.AdvanceWeek(), loaded.AdvanceWeek());
            Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(loaded));
        }
    }

    [Fact]
    public void UndoRestoresZoneAndUndoOfRezoningRestoresCountdown()
    {
        var game = Occupied();
        game.Paused = true;
        var session = new GameSession(game);
        session.PlaceCursor(Cell);
        string before = SaveGameStore.Serialize(game);
        Assert.True(session.Dezone().Success);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
        session.Dezone();
        var removal = session.Game.Map.ZoneRemovalAt(Cell.X, Cell.Y);
        session.Zone(ZoneType.Residential);
        Assert.Null(session.Game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(removal, session.Game.Map.ZoneRemovalAt(Cell.X, Cell.Y));
        Assert.Equal(ZoneType.None, session.Game.Map.ZoneAt(Cell.X, Cell.Y));
    }

    [Fact]
    public void InvalidCountdownDataIsRejectedExplicitly()
    {
        var game = Occupied();
        game.Dezone(Area);
        var data = JsonNode.Parse(SaveGameStore.Serialize(game))!;
        var entry = data["ZoneRemovals"]![game.Map.Index(Cell.X, Cell.Y).ToString()]!;
        entry["RemoveAtDay"] = -1;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(data.ToJsonString()));
        entry["RemoveAtDay"] = 14;
        entry["Zone"] = "None";
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(data.ToJsonString()));
    }

    [Fact]
    public void UnzonedBuildingsRequireTheRemovalMetadataField()
    {
        var game = Occupied();
        game.Map.SetZone(Cell.X, Cell.Y, ZoneType.None);
        game.Touch();
        var data = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
        data.Remove("ZoneRemovals");
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(data.ToJsonString()));
    }

    [Fact]
    public void DezoningIsFreeAndAFailedDezoneDoesNotReplaceUndo()
    {
        var game = Occupied();
        game.Money = 0;
        game.Paused = true;
        var session = new GameSession(game);
        session.PlaceCursor(Cell);
        string before = SaveGameStore.Serialize(game);
        Assert.True(session.Dezone().Success);
        ulong rng = game.Rng.State;
        Assert.False(session.Dezone().Success);
        Assert.Equal(rng, game.Rng.State);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(before, SaveGameStore.Serialize(session.Game));
    }
}
