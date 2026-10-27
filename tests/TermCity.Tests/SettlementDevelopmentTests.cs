using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementDevelopmentTests
{
    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 64, MapHeight = 64, StartingMoney = 100_000,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0,
            FloodChancePerWeek = 0, OutbreakChancePerWeek = 0,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(25, 25, 32, 32)).Success);
        game.Map.BuildingEstablishedDays.Clear();
        game.Touch();
        return game;
    }

    [Fact]
    public void FoundingLeavesSharedUnreservedLandAndDemandExplainsOrganicGrowth()
    {
        var game = Founded();
        Assert.All(Zones.Placeable, zone => Assert.Empty(game.Map.ZoneCells(zone)));
        var homes = game.VacantPlots(ZoneType.Residential);
        Assert.NotEmpty(homes);
        Assert.Equal(homes, game.VacantPlots(ZoneType.Commercial));
        Assert.Equal(homes, game.VacantPlots(ZoneType.Industrial));
        Assert.Contains("no zoning", GrowthDiagnostics.ForZone(game, ZoneType.Residential).Message);
    }

    [Fact]
    public void DemandDrivenGrowthHasMixedTradesAndCompleteWealthyPlots()
    {
        var game = Founded();
        for (int n = 0; n < 16; n++)
        {
            Assert.NotNull(game.DevelopSettlementBuilding(ZoneType.Residential));
            if (n < 6)
            {
                Assert.NotNull(game.DevelopSettlementBuilding(ZoneType.Commercial));
                Assert.NotNull(game.DevelopSettlementBuilding(ZoneType.Industrial));
            }
        }
        var map = game.Map;
        var names = map.BuildingFootprints.Values.Select(a => map.BuildingAt(a.X, a.Y)!.Name).ToHashSet();
        Assert.Contains("Courtyard House", names);
        Assert.Contains("Blacksmith", names);
        Assert.Contains("Woodsmith", names);
        Assert.Contains("Armory", names);
        Assert.Contains("Supply Shop", names);
        Assert.Contains("Market", names);
        foreach (var area in map.BuildingFootprints.Values)
        {
            var type = map.BuildingAt(area.X, area.Y)!;
            Assert.All(area.Cells(), p => Assert.True(game.InsideSettlement(p)));
            Assert.All(area.Cells(), p => Assert.Equal(type.Zone, map.ZoneAt(p.X, p.Y)));
            if (type.Name == "Courtyard House")
            {
                Assert.Equal(4, area.Area);
                Assert.True(game.SettlementLandValue(new(area.X, area.Y)) >= 47);
            }
        }
        Assert.Equal(0, game.Stats.Population);
        Assert.All(Zones.Placeable, zone => Assert.All(map.ZoneCells(zone),
            i => Assert.NotNull(map.BuildingAt(i % map.Width, i / map.Width))));
    }

    private static CityGame Crowded()
    {
        var game = Founded();
        var map = game.Map;
        var bounds = game.Settlement!.Bounds;
        var cottage = map.Content.Buildings.Get("Cottage");
        var services = map.BuildingFootprints.Values.ToArray();
        for (int y = bounds.Y + 3; y <= bounds.Bottom - 3; y++)
            for (int x = bounds.X + 3; x <= bounds.Right - 3; x++)
            {
                if (map.BuildingAt(x, y) is not null) continue;
                if (services.Any(a => new CellRect(a.X - 1, a.Y - 1, a.Width + 2, a.Height + 2).Contains(x, y)))
                {
                    map.SetPathWear(x, y, 4);
                    continue;
                }
                if ((y - bounds.Y) % 3 == 2) { map.SetPathWear(x, y, 4); continue; }
                map.SetBuildingFootprint(cottage, new(x, y, 1, 1));
                map.SetHousehold(x, y, new Household(3, 2, 0));
            }
        game.Touch();
        Assert.True(game.SettlementCrowded);
        return game;
    }

    [Fact]
    public void CrowdingStacksPoorHousingWithoutInventingPeopleAndAttachesHomesToWalls()
    {
        var game = Crowded();
        int people = game.Stats.Population;
        game.RedevelopSettlement();
        game.Touch();
        Assert.Equal(people, game.Stats.Population);
        Assert.Contains(game.Map.BuildingFootprints.Values,
            area => game.Map.BuildingAt(area.X, area.Y)!.Name == "Shanty Tower");
        var walls = game.Settlement!.Walls.Select(w => (w.Cell, w.Health, w.Defense)).ToArray();
        int cell = Assert.IsType<int>(game.DevelopSettlementBuilding(ZoneType.Residential));
        var p = game.Map.PosOf(cell);
        Assert.Equal("Wall House", game.Map.BuildingAt(p.X, p.Y)!.Name);
        Assert.Equal(walls, game.Settlement.Walls.Select(w => (w.Cell, w.Health, w.Defense)));
        Assert.All(game.Settlement.Walls, w => Assert.Null(game.Map.BuildingAt(w.Cell.X, w.Cell.Y)));
        var bounds = game.Settlement.Bounds;
        Assert.True(p.X == bounds.X + 1 || p.X == bounds.Right - 1 ||
            p.Y == bounds.Y + 1 || p.Y == bounds.Bottom - 1);
    }

    [Fact]
    public void SaveMigrationClearsOnlyVacantReservationsAndContinuesDeterministically()
    {
        var game = Founded();
        for (int n = 0; n < 8; n++) game.DevelopSettlementBuilding(ZoneType.Residential);
        var empty = game.VacantPlots(ZoneType.Residential)[0];
        game.Map.SetZone(empty.X, empty.Y, ZoneType.Industrial);
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        Assert.Equal(ZoneType.None, loaded.Map.ZoneAt(empty.X, empty.Y));
        game.ClearSettlementReservations();
        for (int n = 0; n < 3; n++)
            foreach (var zone in Zones.Placeable)
                Assert.Equal(game.DevelopSettlementBuilding(zone), loaded.DevelopSettlementBuilding(zone));
        Assert.Equal(SaveGameStore.Serialize(game), SaveGameStore.Serialize(loaded));
    }

    [Theory]
    [InlineData("Courtyard House", 1, 0.6)]
    [InlineData("Shanty Tower", 4, 1.5)]
    [InlineData("Wall House", 3, 1.1)]
    public void HousingGeometryExpressesHorizontalWealthAndVerticalCrowding(string name, int stories, double maxHeight)
    {
        var type = Founded().Map.Content.Buildings.Get(name);
        var look = HouseAppearance.At(type, 10, 10);
        var blocks = look.BlocksOnPlot(type.Width, type.Height, type.Level);
        Assert.All(blocks, block =>
        {
            Assert.Equal(stories, block.Stories);
            Assert.True(block.Height <= maxHeight);
            Assert.NotEmpty(look.Parts(block));
        });
        Assert.Equal(name == "Courtyard House" ? 3 : 1, blocks.Count);
        var sprite = BuildingSprite.Render(type, type.Width, type.Height);
        Assert.Contains(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha == 255);
    }
}
