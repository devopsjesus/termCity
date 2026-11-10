using System.Reflection;
using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementSiteCacheTests
{
    [Fact]
    public void DirectPathAndBuildingChangesInvalidateCachedSiteMasks()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        Assert.True(game.FoundSettlement(new(30, 30, 64, 64)).Success);
        var bounds = game.Settlement!.GrowthBounds(game);
        var type = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        var sites = typeof(CityGame).GetMethod("SettlementSites", BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<CellRect> Current() => (List<CellRect>)sites.Invoke(game, [bounds, type, null, false, null])!;
        var area = Current()[0];
        game.Map.SetPathWear(area.X, area.Y, 3);
        Assert.DoesNotContain(area, Current());
        game.Map.SetPathWear(area.X, area.Y, 0);
        Assert.Contains(area, Current());
        game.Map.SetBuildingFootprint(type, area);
        Assert.DoesNotContain(area, Current());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefixSummedSitesMatchDirectFootprintChecks(bool extension)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        foreach (int road in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(road % game.Map.Width, road / game.Map.Width, false);
        Assert.True(game.FoundSettlement(new(30, 30, 64, 64)).Success);
        if (extension)
        {
            System.Numerics.Vector2 a = new(93.5f, 55.5f), b = new(110.5f, 55.5f),
                c = new(110.5f, 75.5f), d = new(93.5f, 75.5f);
            foreach (var (start, end) in new[] { (a, b), (b, c), (c, d) })
            {
                var result = game.BuildWallRun(start, end);
                Assert.True(result.Success, result.Message);
            }
        }
        var bounds = game.Settlement!.GrowthBounds(game);
        var sites = typeof(CityGame).GetMethod("SettlementSites", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var available = typeof(CityGame).GetMethod("SettlementSiteAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var type in game.Map.Content.Buildings.Where(type =>
            type.SettlementOnly && type.Zone is ZoneType.Residential or ZoneType.Commercial or ZoneType.Industrial))
        {
            var actual = (List<CellRect>)sites.Invoke(game, [bounds, type, null, false, null])!;
            var expected = new HashSet<CellRect>();
            for (int y = bounds.Y + 1; y < bounds.Bottom; y++)
                for (int x = bounds.X + 1; x < bounds.Right; x++)
                {
                    var area = type.FootprintAt(x, y);
                    if ((bool)available.Invoke(game, [bounds, area, type.Name == "Wall House", -1, null])!)
                        expected.Add(area);
                }
            Assert.True(expected.SetEquals(actual), $"{type.Name}: direct={expected.Count}, prefix={actual.Count}; " +
                $"missing={string.Join(",", expected.Except(actual).Take(3))}; extra={string.Join(",", actual.Except(expected).Take(3))}");
        }
    }
}
