using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class FootpathTopologyTests
{
    [Fact]
    public void LegacyParallelLanesDoNotInferCrossLinksBetweenNeighboringWornCells()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [];
        for (int x = 10; x <= 16; x++)
            foreach (int y in new[] { 10, 11 }) map.SetPathWear(x, y, 6);
        var strokes = FootpathLayout.Strokes(map);
        Assert.Equal(2, strokes.Count);
        Assert.All(strokes, stroke => Assert.All(stroke.Points,
            point => Assert.Equal(stroke.Points[0].Y, point.Y)));
    }

    [Fact]
    public void HistoricalLadderRemovesWholeRedundantRungsRatherThanLeavingStubs()
    {
        var map = TestCity.Flat().Map;
        map.BuildingPaths = [];
        for (int x = 10; x <= 16; x++)
            foreach (int y in new[] { 10, 13 }) map.SetPathWear(x, y, 6);
        foreach (int x in new[] { 10, 13, 16 })
            for (int y = 10; y <= 13; y++) map.SetPathWear(x, y, 6);
        var strokes = FootpathLayout.Strokes(map);
        Assert.NotEmpty(strokes);
        Assert.DoesNotContain(strokes.SelectMany(stroke => stroke.Points), point =>
            point.Y > 10.75f && point.Y < 13.25f &&
            (Math.Abs(point.X - 13.5f) < 0.01f || Math.Abs(point.X - 16.5f) < 0.01f));
        Assert.Contains(strokes.SelectMany(stroke => stroke.Points),
            point => Math.Abs(point.X - 10.5f) < 0.01f && point.Y > 11 && point.Y < 13);
        Assert.All(Enumerable.Range(10, 7), x =>
        {
            Assert.Equal(6, map.PathWearAt(x, 10));
            Assert.Equal(6, map.PathWearAt(x, 13));
        });
    }

    [Fact]
    public void SavedActualPathEdgesKeepParallelTrailsSeparateAfterTheirJourneyDisappears()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        map.BuildingPaths = [];
        for (int x = 10; x <= 16; x++)
            foreach (int y in new[] { 10, 11 })
            {
                map.SetPathWear(x, y, 6);
                if (x > 10) map.LinkPathCells(map.Index(x - 1, y), map.Index(x, y));
            }
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(map.PathLinkLayer, loaded.Map.PathLinkLayer);
        var strokes = FootpathLayout.Strokes(loaded.Map);
        Assert.Equal(2, strokes.Count);
        Assert.All(strokes, stroke => Assert.All(stroke.Points,
            point => Assert.Equal(stroke.Points[0].Y, point.Y)));
    }

    [Fact]
    public void SimsRecordOnlyTheAdjacentStepsTheyActuallyUse()
    {
        var map = TestCity.Flat().Map;
        var home = map.Content.Buildings.Get("Cottage");
        foreach (int x in new[] { 10, 11 })
        {
            map.SetZone(x, 15, ZoneType.Residential);
            map.SetBuildingFootprint(home, new(x, 15, 1, 1));
        }
        for (int day = 0; day < 16; day++) TravelRoutes.WearPaths(map);
        Assert.True(map.HasPathLink(map.Index(10, 17), map.Index(10, 18)));
        Assert.True(map.HasPathLink(map.Index(11, 17), map.Index(11, 18)));
        Assert.False(map.HasPathLink(map.Index(10, 17), map.Index(11, 17)));
        Assert.False(map.HasPathLink(map.Index(10, 18), map.Index(11, 18)));
        map.ClearCell(10, 18);
        Assert.False(map.HasPathLink(map.Index(10, 17), map.Index(10, 18)));
        Assert.Equal(0, map.PathLinkLayer[map.Index(10, 18)]);
    }

    [Fact]
    public void AsymmetricSavedPathLinksAreRejectedAndOldSavesRemainSupported()
    {
        var game = TestCity.Flat();
        game.Map.SetPathWear(10, 10, 6);
        game.Map.SetPathWear(11, 10, 6);
        game.Map.LinkPathCells(game.Map.Index(10, 10), game.Map.Index(11, 10));
        var json = System.Text.Json.Nodes.JsonNode.Parse(SaveGameStore.Serialize(game))!;
        json.AsObject().Remove("PathLinks");
        Assert.All(SaveGameStore.Deserialize(json.ToJsonString()).Map.PathLinkLayer, mask => Assert.Equal(0, mask));
        game.Map.PathLinkLayer[game.Map.Index(11, 10)] = 0;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(SaveGameStore.Serialize(game)));
        game.Map.PathLinkLayer[game.Map.Index(10, 10)] = 16;
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(SaveGameStore.Serialize(game)));
    }
}
