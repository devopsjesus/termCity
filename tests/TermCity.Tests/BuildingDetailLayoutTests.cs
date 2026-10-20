using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class BuildingDetailLayoutTests
{
    [Theory]
    [InlineData(0.468)]
    [InlineData(0.55)]
    [InlineData(0.84)]
    [InlineData(1.68)]
    [InlineData(2.52)]
    [InlineData(3.36)]
    [InlineData(4.2)]
    public void FacadeWindowsAreCenteredAndNeverCrossTimberPosts(double width)
    {
        var bays = BuildingDetailLayout.Facade((float)width);
        Assert.Equal(1, bays.Count % 2);
        Assert.Equal(0, bays.Center(bays.Count / 2), 5);
        for (int bay = 0; bay < bays.Count; bay++)
        {
            float center = bays.Center(bay);
            Assert.Equal(-center, bays.Center(bays.Count - 1 - bay), 5);
            Assert.True(center - BuildingDetailLayout.WindowWidth / 2 >
                bays.Post(bay) + BuildingDetailLayout.BeamWidth / 2);
            Assert.True(center + BuildingDetailLayout.WindowWidth / 2 <
                bays.Post(bay + 1) - BuildingDetailLayout.BeamWidth / 2);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => bays.Center(bays.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => bays.Post(-1));
    }

    [Fact]
    public void TowerMerlonsFormAnEvenOpenPerimeterWithoutOverlap()
    {
        var centers = BuildingDetailLayout.Centers(BuildingDetailLayout.TowerCapWidth,
            BuildingDetailLayout.MerlonWidth, 0.25f);
        Assert.Equal(3, centers.Length);
        Assert.Equal(-0.25f, centers[0], 5);
        Assert.Equal(0.25f, centers[^1], 5);
        var positions = (from x in centers from z in centers where x != 0 || z != 0 select (X: x, Z: z)).ToArray();
        Assert.Equal(8, positions.Length);
        Assert.Equal(8, positions.Distinct().Count());
        foreach (var p in positions)
        {
            Assert.True(Math.Abs(p.X) + BuildingDetailLayout.MerlonWidth / 2 <= BuildingDetailLayout.TowerCapWidth / 2 + 0.00001f);
            Assert.True(Math.Abs(p.Z) + BuildingDetailLayout.MerlonWidth / 2 <= BuildingDetailLayout.TowerCapWidth / 2 + 0.00001f);
        }
        for (int i = 0; i < positions.Length; i++)
            for (int j = i + 1; j < positions.Length; j++)
                Assert.True(Math.Abs(positions[i].X - positions[j].X) >= BuildingDetailLayout.MerlonWidth ||
                    Math.Abs(positions[i].Z - positions[j].Z) >= BuildingDetailLayout.MerlonWidth);
    }

    [Theory]
    [InlineData(0.55)]
    [InlineData(1.68)]
    [InlineData(3.36)]
    public void RoofAndWallDetailsAreSymmetricAndFitTheirAvailableSpan(double span)
    {
        var centers = BuildingDetailLayout.Centers((float)span, 0.22f, 0.24f);
        Assert.NotEmpty(centers);
        for (int i = 0; i < centers.Length; i++)
        {
            Assert.Equal(-centers[i], centers[centers.Length - 1 - i], 5);
            Assert.True(Math.Abs(centers[i]) + 0.11f <= span / 2 + 0.00001f);
        }
    }

    [Theory]
    [InlineData("Watch House")]
    [InlineData("Apothecary")]
    [InlineData("Chapel")]
    [InlineData("Tavern")]
    public void CrampedCivicBuildingsUseRoomierPlotsAndKeepTheirLegacySaves(string name)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 1000;
        var type = game.Map.Content.Buildings.Get(name);
        Assert.Equal(2, type.Width);
        Assert.Equal(1, type.Height);
        game.Map.SetBuildingFootprint(type, new(30, 19, 1, 1));
        game.Map.SetTerrain(31, 19, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(new CellRect(30, 19, 1, 1), loaded.Map.BuildingFootprintAt(30, 19));
        Assert.Null(loaded.Map.BuildingAt(31, 19));
        Assert.Equal("Water", loaded.Map.TerrainAt(31, 19).Name);
        Assert.Single(loaded.Map.ServiceCells);
        Assert.True(loaded.PlaceBuilding(loaded.Map.Content.Buildings.Get(name), new(40, 19, 1, 1)).Success);
        Assert.Equal(new CellRect(40, 19, 2, 1), loaded.Map.BuildingFootprintAt(41, 19));
        Assert.Equal(2, loaded.Services.BuildingCounts[(int)type.Service]);
    }

    [Fact]
    public void DetailLayoutRejectsInvalidDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDetailLayout.Facade(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDetailLayout.Facade(0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingDetailLayout.Centers(1, 0.2f, 0.1f));
        Assert.Empty(BuildingDetailLayout.Centers(0.1f, 0.2f, 0.3f));
    }
}
