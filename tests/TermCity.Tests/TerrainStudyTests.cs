using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;
using TermCity.GodotApp;
using System.Text.Json.Nodes;

namespace TermCity.Tests;

public class TerrainStudyTests
{
    [Fact]
    public void TerrainComparisonIsExplicitAndRejectsLiveCityOptions()
    {
        Assert.False(GodotOptions.Parse([]).TerrainComparison);
        if (!CityGame.DebugToolsAvailable)
        {
            Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--terrain-comparison"]));
            return;
        }
        var options = GodotOptions.Parse(["--terrain-comparison-smoke-test", "--capture", "/tmp/terrain.png"]);
        Assert.True(options.TerrainComparison && options.DepthComparison && options.DepthComparisonSmokeTest);
        Assert.False(options.SmokeTest);
        Assert.Equal(TerrainStudyTreatment.DoubleHills, GodotOptions.Parse([
            "--perspective-performance-test", "--terrain-performance-treatment", "DoubleHills"]).TerrainPerformanceTreatment);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--terrain-performance-treatment", "Balanced"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([
            "--perspective-performance-test", "--terrain-performance-treatment", "Balanced", "--load"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse([
            "--perspective-performance-test", "--terrain-performance-treatment", "invalid"]));
        foreach (string conflict in new[] { "--load", "--smoke-test", "--debug-settlement", "--dump-map" })
            Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--terrain-comparison", conflict]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HeightCandidatesShareLayoutButNotElevationsOrCaches(int subject)
    {
        if (!CityGame.DebugToolsAvailable) return;
        var baseline = TerrainStudyFixture.Create(subject, TerrainStudyTreatment.Current).Game;
        string saved = SaveGameStore.Serialize(baseline);
        float top = TerrainSprite.Elevation(baseline.Map, 24, 22);
        float water = TerrainSprite.Flow(baseline.Map).At(32, 26).Level;
        foreach (var treatment in Enum.GetValues<TerrainStudyTreatment>())
        {
            var game = TerrainStudyFixture.Create(subject, treatment).Game;
            float scale = (int)TerrainStudyFixture.Profile(treatment);
            Assert.Equal(top * scale, TerrainSprite.Elevation(game.Map, 24, 22), precision: 4);
            Assert.Equal(TerrainSprite.MaximumHillHeight * scale, TerrainSprite.MaximumHillFor(game.Map), precision: 4);
            Assert.Equal(water, TerrainSprite.Flow(game.Map).At(32, 26).Level);
            var candidateSave = JsonNode.Parse(SaveGameStore.Serialize(game))!.AsObject();
            candidateSave.Remove("ElevationProfile");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(saved), candidateSave));
            Assert.Equal(saved, SaveGameStore.Serialize(baseline));
            if (subject == 1)
            {
                var roads = new RoadMeshLayout(game.Map, RoadCurves.Extract(game.Map, (_, _) => true));
                Assert.Contains(roads.Segments, segment => segment.Bridge);
                Assert.Single(game.Map.Footbridges);
                Assert.True(game.Map.Footbridges[0].DeckHeight(game.Map) >=
                    TerrainSprite.Elevation(game.Map, 30, 34));
            }
        }
        Assert.Equal(TerrainElevationProfile.Current, baseline.Map.PreviewElevationProfile);
    }

    [Fact]
    public void ProfileChangesInvalidateMapGeometryWithoutChangingNormalDefaults()
    {
        if (!CityGame.DebugToolsAvailable) return;
        var map = TerrainStudyFixture.Create(0, TerrainStudyTreatment.Current).Game.Map;
        int version = map.TerrainVersion;
        map.SetPreviewElevationProfile(TerrainElevationProfile.Triple);
        Assert.True(map.TerrainVersion > version);
        Assert.Equal(4.05f, TerrainSprite.Elevation(map, 24, 22), precision: 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetPreviewElevationProfile((TerrainElevationProfile)7));
        map.SetPreviewElevationProfile(TerrainElevationProfile.Current);
        Assert.Equal(1.35f, TerrainSprite.Elevation(map, 24, 22), precision: 4);
        Assert.Equal(TerrainElevationProfile.Current, TestCity.Flat().Map.PreviewElevationProfile);
    }
}
