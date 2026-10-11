using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class AreaFalloffTests
{
    [Fact]
    public void CommonsCoverageMatchesPreviewCircleInScreenPixels()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 500;
        var green = game.Map.Content.Buildings.Get("Village Green");
        Assert.True(game.PlaceBuilding(green, new(80, 18, 1, 1)).Success);
        for (int dy = -7; dy <= 7; dy++)
            for (int dx = -11; dx <= 11; dx++)
            {
                double distance = Math.Sqrt(Math.Pow(dx * RoadCurves.CellWidth, 2) +
                    Math.Pow(dy * RoadCurves.CellHeight, 2)) / RoadCurves.CellWidth;
                int expected = (int)Math.Round(green.Strength * AreaFalloff.Strength(distance / green.Radius));
                Assert.Equal(expected, game.Services.Coverage(ServiceKind.Recreation, game.Map.Index(80 + dx, 18 + dy)));
                Assert.Equal((float)(AreaOfEffect.OverlayAlpha * AreaFalloff.Strength(distance / green.Radius)),
                    AreaOfEffect.FadeAlpha(distance / green.Radius));
            }
        Assert.Equal(AreaFalloff.Distance(11, 0), AreaFalloff.Distance(0, 6), 6);
    }

    [Fact]
    public void PollutionUsesTheSameCircularScreenSpaceDistance()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var burners = game.Map.Content.Buildings.Get("Charcoal Burners");
        Assert.True(game.PlaceBuilding(burners, new(80, 18, 1, 1)).Success);
        int radius = CityServices.PollutionRadius(burners.Pollution);
        for (int dy = -13; dy <= 13; dy++)
        {
            int expected = Math.Min(100, (int)Math.Round((burners.Pollution * 9 + 14) *
                AreaFalloff.Strength(AreaFalloff.Distance(0, dy) / radius)));
            Assert.Equal(expected, game.Services.Pollution(game.Map.Index(80, 18 + dy)));
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1.125, 0.5)]
    [InlineData(1.25, 0)]
    [InlineData(2, 0)]
    public void SimulationAndOverlayShareTheSameFalloff(double distance, double strength)
    {
        Assert.Equal(strength, AreaFalloff.Strength(distance), 6);
        Assert.Equal((float)(strength * AreaOfEffect.OverlayAlpha), AreaOfEffect.FadeAlpha(distance));
    }

    [Theory]
    [InlineData("Fire Watch", ServiceKind.Fire)]
    [InlineData("Sheriff's Hall", ServiceKind.Police)]
    [InlineData("Apothecary", ServiceKind.Health)]
    [InlineData("Market Cross", ServiceKind.Trade)]
    public void ServicesStayFullInsideRadiusAndFadeBeyond(string name, ServiceKind kind)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 10_000;
        var building = game.Map.Content.Buildings.Get(name);
        Assert.True(game.PlaceBuilding(building, new(80, 18, 1, 1)).Success);
        int At(int distance) => game.Services.Coverage(kind, game.Map.Index(80 + distance, 18));
        Assert.Equal(building.Strength, At(0));
        Assert.Equal(building.Strength, At(building.Radius));
        Assert.InRange(At(building.Radius + 1), 1, building.Strength - 1);
        Assert.Equal(0, At((int)Math.Ceiling(building.Radius * AreaFalloff.Extent)));
    }

    [Fact]
    public void PollutionHasFullCoreAndFadingFringe()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var type = game.Map.Content.Buildings.Get("Charcoal Burners");
        Assert.True(game.PlaceBuilding(type, new(80, 18, 1, 1)).Success);
        int radius = CityServices.PollutionRadius(type.Pollution);
        int At(int distance) => game.Services.Pollution(game.Map.Index(80 + distance, 18));
        Assert.Equal(At(0), At(radius));
        Assert.InRange(At(radius + 1), 1, At(radius) - 1);
        Assert.Equal(0, At((int)Math.Ceiling(radius * AreaFalloff.Extent)));
    }

    [Fact]
    public void AirCleanupReducesPollutionThroughItsCoreAndFadingFringe()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        game.HighestMilestone = 500;
        Assert.True(game.PlaceBuilding(game.Map.Content.Buildings.Get("Charcoal Burners"), new(80, 18, 1, 1)).Success);
        var before = game.Services;
        var green = game.Map.Content.Buildings.Get("Village Green");
        Assert.True(game.PlaceBuilding(green, new(90, 18, 1, 1)).Success);
        int Removed(int distance) => before.Pollution(game.Map.Index(90 + distance, 18)) -
            game.Services.Pollution(game.Map.Index(90 + distance, 18));
        int radius = CityServices.PollutionRadius(green.Pollution);
        Assert.True(Removed(0) > 0);
        Assert.Equal(Removed(0), Removed(radius));
        Assert.InRange(Removed(radius + 1), 1, Removed(radius) - 1);
        Assert.Equal(0, Removed((int)Math.Ceiling(radius * AreaFalloff.Extent)));
    }

    [Fact]
    public void MarketCrossHasWiderCoverageWithoutChangingCostOrStrength()
    {
        var game = TestCity.Flat();
        var market = game.Map.Content.Buildings.Get("Market Cross");
        Assert.Equal(18, market.Radius);
        Assert.Equal(80, market.Strength);
        Assert.Equal(6_000, market.Cost);
        Assert.Equal(60, market.WeeklyUpkeep);
        Assert.Equal(22.5, market.Radius * AreaFalloff.Extent);
    }
}
