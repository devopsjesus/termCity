using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class DebugMonumentTests
{
    [Fact]
    public void DuckSculptureHasAGoldenYellowPaletteInItsSpriteAndCatalog()
    {
        var type = TestCity.Flat().Map.Content.Buildings.Get("Bewildered Duck Monument");
        Assert.Equal(Rgb.Hex(0xe3be48), type.Foreground);
        var sprite = BuildingSprite.Render(type, type.Width, type.Height);
        int goldPixels = Enumerable.Range(0, sprite.Width * sprite.Height).Count(i =>
            sprite.Pixels[i * 4] > 170 && sprite.Pixels[i * 4 + 1] > 130 &&
            sprite.Pixels[i * 4 + 2] < 100 && sprite.Pixels[i * 4 + 3] == 255);
        Assert.True(goldPixels > 1000, $"Expected a visibly yellow sculpture, found {goldPixels} gold pixels.");
    }

    [Fact]
    public void OriginalDuckMonumentIsImmediatelyAvailableOnlyInDebugBuilds()
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get("Bewildered Duck Monument");
        Assert.True(type.DebugOnly);
        Assert.True(type.IsMonument);
        Assert.Equal(100_000, type.MinPopulation);
        Assert.Equal((8, 6), (type.Width, type.Height));
        Assert.Equal(CityGame.DebugToolsAvailable, CityProgression.IsUnlocked(game, type));
        Assert.Equal(CityGame.DebugToolsAvailable, game.CanPlaceBuilding(type, 30, 40));
        var result = game.PlaceBuilding(type, new(30, 40, 1, 1));
        Assert.Equal(CityGame.DebugToolsAvailable, result.Success);
        if (!CityGame.DebugToolsAvailable)
        {
            game.Map.SetBuildingFootprint(type, new(30, 40, 8, 6));
            Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(SaveGameStore.Serialize(game)));
            return;
        }
        Assert.Equal(50_000, game.Money);
        Assert.Equal(new CellRect(30, 40, 8, 6), game.Map.BuildingFootprintAt(37, 45));
        Assert.Single(game.Map.BuildingFootprints);
        Assert.Empty(game.Map.ServiceCells);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(type.Name, loaded.Map.BuildingAt(37, 45)!.Name);
        Assert.True(loaded.Demolish(new CellRect(37, 45, 1, 1)).Success);
        Assert.Empty(loaded.Map.BuildingFootprints);
    }

    [Fact]
    public void MonumentPlacementIsAtomicWhenAnyPartIsBlocked()
    {
        var game = TestCity.Flat();
        game.Map.SetTerrain(37, 45, game.Map.Content.Terrains.Get("Water"));
        var type = game.Map.Content.Buildings.Get("Bewildered Duck Monument");
        Assert.False(game.PlaceBuilding(type, new(30, 40, 1, 1)).Success);
        Assert.Empty(game.Map.BuildingFootprints);
        Assert.Null(game.Map.BuildingAt(30, 40));
    }

    [Fact]
    public void DebugConstructionMenusHideThePrototypeInReleaseBuilds()
    {
        var game = TestCity.Flat();
        var session = new GameSession(game);
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        bool listed = session.Prompt!.Choices.Any(choice => choice.Label == "Bewildered Duck Monument");
        Assert.Equal(CityGame.DebugToolsAvailable, listed);
        if (!CityGame.DebugToolsAvailable)
            Assert.Contains("Debug", game.BuildingPlacementError(game.Map.Content.Buildings.Get("Bewildered Duck Monument"), 30, 40));
    }

    [Fact]
    public void PeakPopulationBeyondNormalMilestonesIsPreservedForFutureMonumentGates()
    {
        var game = TestCity.Flat();
        for (int y = 40; y < 50; y++)
            for (int x = 0; x < 20; x++)
            {
                game.Map.SetZone(x, y, ZoneType.Residential);
                game.Map.SetBuilding(x, y, game.Map.Content.Buildings.Get("Tenement"));
                game.Map.SetHousehold(x, y, new Household(250, 200, 50));
            }
        game.Touch();
        Assert.Equal(100_000, game.HighestPopulation);
        Assert.Equal(10_000, game.HighestMilestone);
        for (int y = 40; y < 50; y++)
            for (int x = 0; x < 20; x++) game.Map.SetHousehold(x, y, default);
        game.Touch();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(0, loaded.Stats.Population);
        Assert.Equal(100_000, loaded.HighestPopulation);
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(TestCity.Flat()))!.AsObject();
        json.Remove("HighestPopulation");
        Assert.Equal(0, SaveGameStore.Deserialize(json.ToJsonString()).HighestPopulation);
    }
}
