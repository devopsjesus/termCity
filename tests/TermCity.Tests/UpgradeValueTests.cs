using TermCity.Core.Buildings;
using TermCity.Core.Roads;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using Xunit.Abstractions;

namespace TermCity.Tests;

public class UpgradeValueTests(ITestOutputHelper output)
{
    private const int PaybackWeeks = 52;

    [Fact]
    public void EveryServiceUpgradeHasBetterRunningAndOneYearValue()
    {
        var content = new GameContent();
        var tiers = content.Buildings.Where(type => type.PlayerPlaceable && type.Service != ServiceKind.None)
            .GroupBy(type => type.Service)
            .SelectMany(group =>
            {
                var ordered = group.OrderBy(type => type.Cost).ToArray();
                return ordered.Zip(ordered.Skip(1));
            }).ToArray();
        var delivered = tiers.SelectMany(pair => new[] { pair.First, pair.Second }).Distinct()
            .ToDictionary(type => type, DeliveredOutput);
        foreach (var (starter, upgrade) in tiers)
        {
            double before = delivered[starter], after = delivered[upgrade];
            double capitalPremium = upgrade.Cost / after - starter.Cost / before;
            double weeklySaving = starter.WeeklyUpkeep / before - upgrade.WeeklyUpkeep / after;
            output.WriteLine($"{starter.Name} -> {upgrade.Name}: output {before:F2} -> {after:F2}, " +
                $"running cost/unit {starter.WeeklyUpkeep / before:F4} -> {upgrade.WeeklyUpkeep / after:F4}, " +
                $"payback {(weeklySaving > 0 ? Math.Max(0, capitalPremium / weeklySaving) : double.PositiveInfinity):F2} weeks");
        }
        Assert.All(tiers, pair =>
        {
            var (starter, upgrade) = pair;
            double before = delivered[starter], after = delivered[upgrade];
            string name = $"{starter.Name} -> {upgrade.Name}";
            Assert.True(upgrade.Cost > starter.Cost, $"{name}: upgrade must cost more to buy.");
            Assert.True(after > before, $"{name}: upgrade must deliver more useful output.");
            Assert.True(upgrade.WeeklyUpkeep / after < starter.WeeklyUpkeep / before,
                $"{name}: upgrade must have better running efficiency.");
            Assert.True((upgrade.Cost + PaybackWeeks * upgrade.WeeklyUpkeep) / after <
                (starter.Cost + PaybackWeeks * starter.WeeklyUpkeep) / before,
                $"{name}: upgrade must become better value within {PaybackWeeks} weeks.");
        });
    }

    [Fact]
    public void EveryRoadUpgradeHasBetterRunningAndOneYearValue()
    {
        var game = TestCity.Flat();
        var roads = game.Map.Content.Roads.OrderBy(road => road.Rank).ToArray();
        Assert.All(roads.Zip(roads.Skip(1)), pair =>
        {
            var (starter, upgrade) = pair;
            Assert.True(upgrade.CostMultiplier > starter.CostMultiplier);
            Assert.True(upgrade.TrafficCapacity > starter.TrafficCapacity);
            Assert.True((double)upgrade.WeeklyUpkeep / upgrade.TrafficCapacity <
                (double)starter.WeeklyUpkeep / starter.TrafficCapacity);
            Assert.True((game.Config.RoadCostPerCell * upgrade.CostMultiplier + PaybackWeeks * upgrade.WeeklyUpkeep) /
                upgrade.TrafficCapacity <
                (game.Config.RoadCostPerCell * starter.CostMultiplier + PaybackWeeks * starter.WeeklyUpkeep) /
                starter.TrafficCapacity);
        });
    }

    private static double DeliveredOutput(BuildingType type)
    {
        if (type.Service.IsUtility()) return type.Capacity;
        var config = new GameConfig();
        var map = new GameMap(160, 96, new GameContent());
        for (int x = 0; x < map.Width; x++) map.SetRoad(x, 47, map.Content.Roads.Get(DefaultRoads.KingsRoadName));
        map.SetBuildingFootprint(map.Content.Buildings.Get(type.Name), new CellRect(80, 48, type.Width, type.Height));
        var services = CityServices.Compute(map, RoadNetwork.Compute(map, config.RoadServiceReach), config, _ => 1);
        Assert.Equal(1, services.ActiveCounts[(int)type.Service]);
        return Enumerable.Range(0, map.Width * map.Height).Sum(index => services.Coverage(type.Service, index)) / 100.0;
    }
}
