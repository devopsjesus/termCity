using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class AreaOfEffectTests
{
    [Fact]
    public void RingsAreMoreVisibleAndIconsAreFullyOpaque()
    {
        Assert.Equal(0.38f, AreaOfEffect.RingAlpha);
        Assert.Equal(1f, AreaOfEffect.IconAlpha);
    }

    [Fact]
    public void ServiceAndSmokeRingsUseSimulationDistances()
    {
        var game = TestCity.Flat();
        var green = game.Map.Content.Buildings.Get("Village Green");
        var areas = AreaOfEffect.ForBuilding(green, new(10, 10));
        Assert.Equal(new[] { CityServices.PollutionRadius(green.Pollution), green.Radius }, areas.Select(a => a.Radius));
        Assert.All(areas, a => Assert.Equal(new Pos(10, 10), a.Center));
        Assert.Single(AreaOfEffect.ForBuilding(game.Map.Content.Buildings.Get("Charcoal Burners"), new(10, 10)));
        Assert.Empty(AreaOfEffect.ForBuilding(game.Map.Content.Buildings.Get("Town Well"), new(10, 10)));
    }

    [Fact]
    public void MultipleEffectsAtSameRadiusShareCircleWithSideBySideGlyphs()
    {
        var type = new BuildingType
        {
            Name = "Garden", Glyphs = ["+"], Foreground = Rgb.Hex(0xffffff),
            Service = ServiceKind.Recreation, Radius = 7, Pollution = -2,
        };
        var area = Assert.Single(AreaOfEffect.ForBuilding(type, new(2, 2)));
        Assert.Equal(2, area.Glyphs.Count);
        Assert.Equal(7, area.Radius);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    [InlineData(1, 16)]
    public void PixelatedOutlineTracksExactRadiusAcrossZooms(int scale, int stride)
    {
        const int radius = 16, pixel = 3;
        int width = 12 * scale, height = 22 * scale;
        var points = AreaOfEffect.Outline(radius, width, height, stride, pixel);
        Assert.All(points, p =>
        {
            Assert.Equal(0, p.X % pixel);
            Assert.Equal(0, p.Y % pixel);
            double distance = Math.Sqrt(Math.Pow(p.X * stride / (double)width, 2) +
                Math.Pow(p.Y * stride / (double)height, 2));
            double tolerance = pixel * stride * Math.Sqrt(1.0 / (width * width) + 1.0 / (height * height)) / 2;
            Assert.InRange(distance, radius - tolerance, radius + tolerance);
        });
        Assert.Contains(new Pos((int)Math.Round(radius * width / (double)(stride * pixel)) * pixel, 0), points);
        Assert.Contains(new Pos(0, (int)Math.Round(radius * height / (double)(stride * pixel)) * pixel), points);
    }

    [Fact]
    public void RingsIncludeOffscreenSourcesAndIndustrialSmokeWithoutDuplicateFootprintCells()
    {
        var game = TestCity.Flat();
        var type = game.Map.Content.Buildings.Get("Sheriff's Hall");
        game.Map.SetBuildingFootprint(type, new(10, 19, 2, 1));
        var selection = new CellRect(10, 19, 13, 1);
        Assert.Single(AreaOfEffect.InView(game, new(20, 19, 2, 2), selection));
        Assert.Empty(AreaOfEffect.InView(game, new(60, 40, 2, 2), selection));
        game.Map.SetZone(22, 19, TermCity.Core.World.ZoneType.Industrial);
        game.Map.SetBuilding(22, 19, game.Map.Content.Buildings.ForZone(TermCity.Core.World.ZoneType.Industrial));
        Assert.Equal(2, AreaOfEffect.InView(game, new(20, 19, 2, 2), selection).Count());
    }

    [Fact]
    public void RingsOnlyAppearForSelectedElementsNotNearbySources()
    {
        var game = TestCity.Flat();
        var sheriff = game.Map.Content.Buildings.Get("Sheriff's Hall");
        game.Map.SetBuildingFootprint(sheriff, new(10, 19, 2, 1));
        game.Map.SetBuildingFootprint(sheriff, new(14, 19, 2, 1));
        var view = new CellRect(0, 0, 40, 40);

        Assert.Empty(AreaOfEffect.InView(game, view, new(12, 19, 1, 1)));
        var selected = Assert.Single(AreaOfEffect.InView(game, view, new(11, 19, 1, 1)));
        Assert.Equal(new Pos(10, 19), selected.Center);
        Assert.Equal(2, AreaOfEffect.InView(game, view, new(11, 19, 4, 1)).Count());
    }

    [Fact]
    public void SelectingAnyFootprintCellShowsAllItsEffects()
    {
        var game = TestCity.Flat();
        var green = game.Map.Content.Buildings.Get("Village Green");
        game.Map.SetBuildingFootprint(green, new(10, 17, 2, 2));
        var areas = AreaOfEffect.InView(game, new(0, 0, 40, 40), new(11, 18, 1, 1)).ToArray();
        Assert.Equal(2, areas.Length);
        Assert.All(areas, area => Assert.Equal(new Pos(10, 17), area.Center));
    }
}
