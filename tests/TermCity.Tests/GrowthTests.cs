using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class GrowthTests
{
    // Zones sit directly beside the road on row 20 so they are served.
    private static void ZoneRow(CityGame game, int y, int x, int count, ZoneType zone) =>
        Assert.True(game.Designate(new CellRect(x, y, count, 1), zone).Success);

    [Fact]
    public void NobodyMovesInWithoutAConnectedRoad()
    {
        var game = TestCity.Flat();
        // Remove the road to the outside world and build a disconnected stub.
        for (int x = 0; x < game.Map.Width; x++)
        {
            game.Map.SetRoad(x, 20, false);
        }

        for (int x = 40; x < 60; x++)
        {
            game.Map.SetRoad(x, 20, true);
        }

        game.Touch();
        ZoneRow(game, 21, 40, 20, ZoneType.Residential);
        TestCity.Advance(game, 20);
        Assert.Equal(0, game.Stats.Residential.Filled);
        Assert.Equal(0, game.Network.ConnectedRoadCount);
    }

    [Fact]
    public void ConnectingTheRoadLetsResidentsMoveIn()
    {
        var game = TestCity.Flat();
        for (int x = 0; x < game.Map.Width; x++)
        {
            game.Map.SetRoad(x, 20, x is >= 10 and < 60);
        }

        game.Touch();
        ZoneRow(game, 21, 10, 20, ZoneType.Residential);
        TestCity.Advance(game, 5);
        Assert.Equal(0, game.Stats.Residential.Filled);

        Assert.True(game.BuildRoad(new CellRect(0, 20, 10, 1)).Success);
        TestCity.Advance(game, 3);
        Assert.True(game.Stats.Residential.Filled > 0);
    }
    [Fact]
    public void CommercialAndIndustrialWaitForTenResidentialCells()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 21, 0, 9, ZoneType.Residential);
        ZoneRow(game, 19, 0, 5, ZoneType.Commercial);
        ZoneRow(game, 19, 10, 5, ZoneType.Industrial);

        TestCity.Advance(game, 30);
        Assert.Equal(9, game.Stats.Residential.Filled);
        Assert.Equal(0, game.Stats.Commercial.Filled);
        Assert.Equal(0, game.Stats.Industrial.Filled);

        ZoneRow(game, 21, 9, 1, ZoneType.Residential);
        TestCity.Advance(game, 30);
        Assert.Equal(10, game.Stats.Residential.Filled);
        Assert.Equal(1, game.Stats.Commercial.Filled);
        Assert.Equal(1, game.Stats.Industrial.Filled);
    }

    [Fact]
    public void RatiosAreOneCommercialPerTwentyAndOneIndustrialPerTenResidential()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 21, 0, 60, ZoneType.Residential);
        ZoneRow(game, 19, 0, 30, ZoneType.Commercial);
        ZoneRow(game, 19, 40, 30, ZoneType.Industrial);

        TestCity.Advance(game, 80);
        Assert.Equal(60, game.Stats.Residential.Filled);
        Assert.Equal(3, game.Stats.Commercial.Filled);
        Assert.Equal(6, game.Stats.Industrial.Filled);
    }

    [Fact]
    public void GrowthRateIsLimitedPerWeek()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 21, 0, 40, ZoneType.Residential);
        TestCity.Advance(game, 1);
        Assert.Equal(3, game.Stats.Residential.Filled);
    }

    [Fact]
    public void ZonesFarFromRoadsAreNotServed()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 30, 0, 10, ZoneType.Residential);
        TestCity.Advance(game, 10);
        Assert.Equal(0, game.Stats.Residential.Filled);
    }

    [Fact]
    public void HouseholdsAverageTwoAdultsTwoChildrenAndSeniorsAreCommon()
    {
        var rng = new GameRandom(2024);
        const int n = 20_000;
        double adults = 0, children = 0;
        int withSeniors = 0, twoSeniors = 0;
        for (int i = 0; i < n; i++)
        {
            var h = Household.Random(rng);
            adults += h.Adults;
            children += h.Children;
            if (h.Seniors > 0) withSeniors++;
            if (h.Seniors == 2) twoSeniors++;
            Assert.True(h.Adults >= 1);
        }

        Assert.InRange(adults / n, 1.9, 2.1);
        Assert.InRange(children / n, 1.9, 2.1);
        Assert.InRange(withSeniors / (double)n, 0.25, 0.45);
        Assert.True(twoSeniors > n / 20);
    }

    [Fact]
    public void FamiliesVaryInSize()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 21, 0, 30, ZoneType.Residential);
        TestCity.Advance(game, 15);
        var sizes = Enumerable.Range(0, 30).Select(x => game.Map.HouseholdAt(x, 21).Total).Distinct().Count();
        Assert.True(sizes > 2);
    }

    [Fact]
    public void SameSeedGivesSameSimulation()
    {
        int Run()
        {
            var game = TestCity.Flat(seed: 9);
            ZoneRow(game, 21, 0, 40, ZoneType.Residential);
            TestCity.Advance(game, 10);
            return game.Stats.Population * 1000 + game.Money;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void DemandAsksForResidentialFirstThenCommerceAndIndustry()
    {
        var game = TestCity.Flat();
        Assert.Equal(1.0, game.Demand.Residential);
        Assert.Equal(0.0, game.Demand.Commercial);

        ZoneRow(game, 21, 0, 40, ZoneType.Residential);
        TestCity.Advance(game, 20);
        var demand = game.Demand;
        Assert.True(demand.Commercial > 0.5);
        Assert.True(demand.Industrial > 0.5);

        ZoneRow(game, 19, 0, 5, ZoneType.Commercial);
        ZoneRow(game, 19, 10, 5, ZoneType.Industrial);
        Assert.Equal(0.0, game.Demand.Commercial);
        Assert.Equal(0.0, game.Demand.Industrial);
    }

    [Fact]
    public void TheWeeklyCapGrowsWithTheCity()
    {
        var game = TestCity.Flat();
        Assert.Equal(3, game.WeeklyCap(3, 0));
        Assert.Equal(4, game.WeeklyCap(3, 50));    // 3 + ceil(1.0)
        Assert.Equal(5, game.WeeklyCap(3, 100));   // 3 + 2
        Assert.Equal(23, game.WeeklyCap(3, 1000));
        Assert.Equal(1 + 100, game.WeeklyCap(1, 5000));
        Assert.Equal(3, game.WeeklyCap(3, -5));
    }

    [Fact]
    public void EachWeekAddsTheBaseCapPlusAShareOfTheHomesAlreadyThere()
    {
        var game = TestCity.Flat();
        ZoneRow(game, 21, 0, 160, ZoneType.Residential);
        ZoneRow(game, 22, 0, 160, ZoneType.Residential);
        ZoneRow(game, 19, 0, 160, ZoneType.Residential);
        ZoneRow(game, 18, 0, 160, ZoneType.Residential);

        for (int week = 0; week < 40; week++)
        {
            int before = game.Stats.Residential.Filled;
            var report = game.AdvanceWeek();
            Assert.Equal(game.WeeklyCap(3, before), report.NewHouseholds);
        }

        // Faster than the flat three a week would have managed.
        Assert.True(game.Stats.Residential.Filled > 40 * 3 + 30, $"filled={game.Stats.Residential.Filled}");
    }

    [Fact]
    public void ACityOfThousandsKeepsGrowingByDozensAWeek()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 640, MapHeight = 192 });
        // Four zoned rows along each of three long roads give a couple of thousand served cells.
        for (int y = 40; y <= 120; y += 40)
        {
            for (int x = 0; x < 640; x++)
            {
                game.Map.SetRoad(x, y, true);
            }

            game.Designate(new CellRect(0, y + 1, 640, 2), ZoneType.Residential);
            game.Designate(new CellRect(0, y - 2, 640, 2), ZoneType.Residential);
        }

        game.Touch();
        int newest = 0;
        for (int week = 0; week < 150; week++)
        {
            newest = game.AdvanceWeek().NewHouseholds;
        }

        Assert.True(game.Stats.Residential.Filled > 1000, $"filled={game.Stats.Residential.Filled}");
        Assert.True(newest > 10, $"only {newest} new homes in the latest week");
    }

    [Fact]
    public void CommercialAndIndustrialCapsGrowWithTheRoomTheResidentsCreate()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 640, MapHeight = 192 });
        var map = game.Map;
        for (int x = 0; x < 640; x++)
        {
            map.SetRoad(x, 60, true);
        }

        // A big established city: 2,560 homes already filled somewhere else on the map.
        var house = map.Content.Buildings.ForZone(ZoneType.Residential)!;
        for (int y = 100; y < 104; y++)
        {
            for (int x = 0; x < 640; x++)
            {
                map.SetZone(x, y, ZoneType.Residential);
                map.SetBuilding(x, y, house);
                map.SetHousehold(x, y, new Household(2, 2, 1));
            }
        }

        // Room beside the road for 400 shops and 400 factories (two rows of 200 columns each).
        game.Designate(new CellRect(0, 58, 200, 2), ZoneType.Commercial);
        game.Designate(new CellRect(300, 58, 200, 2), ZoneType.Industrial);
        game.Touch();

        int homes = game.Stats.Residential.Filled;
        Assert.Equal(2560, homes);
        var report = game.AdvanceWeek();

        // The residents allow 128 shops and 256 factories; the weekly caps scale with those, not stay at one.
        Assert.Equal(game.WeeklyCap(1, 128), report.NewCommercial);
        Assert.Equal(game.WeeklyCap(1, 256), report.NewIndustrial);
        Assert.True(report.NewCommercial > 1 && report.NewIndustrial > report.NewCommercial);

        // And the ratios still hold once everything has had time to arrive.
        TestCity.Advance(game, 80);
        Assert.Equal(128, game.Stats.Commercial.Filled);
        Assert.Equal(256, game.Stats.Industrial.Filled);
    }
    [Fact]
    public void AGrowthRateOfZeroKeepsTheOldFlatCap()
    {
        var game = TestCity.Flat(config: new GameConfig { GrowthRatePerWeek = 0 });
        Assert.Equal(3, game.WeeklyCap(3, 100_000));
    }

    [Fact]
    public void SavedGrowthRateIsRequired()
    {
        var game = TestCity.Flat();
        var root = System.Text.Json.Nodes.JsonNode.Parse(TermCity.Core.Persistence.SaveGameStore.Serialize(game))!.AsObject();
        root["Config"]!.AsObject().Remove("GrowthRatePerWeek");
        Assert.Throws<InvalidDataException>(() => TermCity.Core.Persistence.SaveGameStore.Deserialize(root.ToJsonString()));
    }
}