using TermCity.Core.Buildings;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ServiceSnapshotCacheTests
{
    [Fact]
    public void UnlockThresholdCacheTracksNewBuildingDefinitions()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        Assert.False(CityProgression.IsServiceUnlocked(game, ServiceKind.Fire, 0));
        game.Map.Content.Buildings.Register(new BuildingType
        {
            Name = "Cache test watch", Service = ServiceKind.Fire, PlayerPlaceable = true, Glyphs = ["+"],
            Foreground = TermCity.Core.Util.Rgb.Hex(0xffffff),
        });
        Assert.Equal(0, CityProgression.ServiceUnlockPopulation(game, ServiceKind.Fire));
        Assert.True(CityProgression.IsServiceUnlocked(game, ServiceKind.Fire, 0));
    }

    [Fact]
    public void ReusedEmissionPatchesMatchFreshPollutionAndKeepOldSnapshotsImmutable()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 160, MapHeight = 160 }, rules: CityRules.Full);
        var map = game.Map;
        var positive = map.Content.Buildings.First(type => type.Service == ServiceKind.Power && type.Pollution > 0);
        var cleaner = map.Content.Buildings.First(type => type.Pollution < 0);
        map.SetBuildingFootprint(positive, positive.FootprintAt(3, 3));
        map.SetBuildingFootprint(cleaner, cleaner.FootprintAt(80, 80));
        game.Touch();
        var before = CityServices.Compute(map, game.Network, game.Config, _ => 1);
        int[] original = Enumerable.Range(0, map.Width * map.Height).Select(before.Pollution).ToArray();
        map.SetBuildingFootprint(positive, positive.FootprintAt(100, 3));
        game.Touch();
        var cached = CityServices.Compute(map, game.Network, game.Config, _ => 1, previous: before);
        var fresh = CityServices.Compute(map, game.Network, game.Config, _ => 1);
        for (int cell = 0; cell < original.Length; cell++)
        {
            Assert.Equal(original[cell], before.Pollution(cell));
            Assert.Equal(fresh.Pollution(cell), cached.Pollution(cell));
        }
    }

    [Fact]
    public void LazyCoverageRetainsTheExactTravelAndCapacityFormulaWithoutARadiusCutoff()
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var map = game.Map;
        var watch = map.Content.Buildings.Get("Fire Watch");
        map.SetBuildingFootprint(watch, watch.FootprintAt(5, 5));
        game.Touch();
        var services = CityServices.Compute(map, game.Network, game.Config, _ => 1);
        for (int cell = 0; cell < map.Width * map.Height; cell++)
        {
            double workload = Math.Max(1, services.WorkloadAt(ServiceKind.Fire, cell));
            double travel = services.ResponseDays(ServiceKind.Fire, cell) / workload;
            int expected = double.IsFinite(travel) ? Math.Clamp((int)Math.Round(watch.Strength *
                Math.Min(1, watch.ResponseTargetDays / Math.Max(1 / TravelRoutes.UnitsPerDay, travel)) / workload), 1, 100) : 0;
            Assert.Equal(expected, services.Coverage(ServiceKind.Fire, cell));
        }
        Assert.True(services.Coverage(ServiceKind.Fire, map.Index(map.Width - 2, map.Height - 2)) > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => services.Coverage(ServiceKind.Fire, -1));
    }
}
