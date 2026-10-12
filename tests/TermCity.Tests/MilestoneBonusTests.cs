using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class MilestoneBonusTests
{
    private static void Population(CityGame game, int population)
    {
        for (int x = 10; x < 30; x++)
        {
            game.Map.SetZone(x, 21, ZoneType.Residential);
            game.Map.SetBuilding(x, 21, game.Map.Content.Buildings.ForZone(ZoneType.Residential));
            int people = Math.Clamp(population - (x - 10) * 50, 0, 50);
            game.Map.SetHousehold(x, 21, new Household((byte)people, 0, 0));
        }
        game.Touch();
    }

    [Fact]
    public void GrantFundsTwoNewBuildingsAndNeverShrinksForLargerSizes()
    {
        var game = TestCity.Flat();
        int previous = 0;
        foreach (var milestone in CityProgression.Milestones)
        {
            int grant = CityProgression.GoldBonus(game, milestone);
            long costs = game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
                building.MinPopulation > 0 && CityProgression.RequiredPopulation(building) == milestone.Population)
                .OrderBy(building => building.Cost).Take(2).Sum(building => (long)building.Cost);
            Assert.True(grant >= costs);
            Assert.True(grant >= previous);
            previous = grant;
        }
    }

    [Fact]
    public void CrossingMultipleMilestonesPaysEachOnceAndLoadDoesNotRepay()
    {
        var game = TestCity.Flat();
        int startingGold = game.Money;
        Population(game, 500);
        int expectedGrant = CityProgression.Milestones.Where(m => m.Population <= 500)
            .Sum(m => CityProgression.GoldBonus(game, m));
        Assert.Equal(startingGold + expectedGrant, game.Money);
        Population(game, 10);
        Population(game, 500);
        Assert.Equal(startingGold + expectedGrant, game.Money);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        loaded.Touch();
        Assert.Equal(game.Money, loaded.Money);
        Population(loaded, 1_000);
        Assert.Equal(game.Money + CityProgression.GoldBonus(loaded,
            CityProgression.Milestones.Single(m => m.Population == 1_000)), loaded.Money);
    }

    [Fact]
    public void GrantCapsSafelyAtMaximumGold()
    {
        var game = TestCity.Flat();
        game.Money = int.MaxValue - 1;
        Population(game, 100);
        Assert.Equal(int.MaxValue, game.Money);
    }
}
