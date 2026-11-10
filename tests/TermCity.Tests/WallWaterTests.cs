using System.Numerics;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class WallWaterTests
{
    private static CityGame Flat()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000,
        }, rules: CityRules.Full);
        foreach (int i in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(i % game.Map.Width, i / game.Map.Width, false);
        game.Touch();
        return game;
    }

    [Theory]
    [InlineData(BaileyGateSide.Right)]
    [InlineData(BaileyGateSide.Bottom)]
    [InlineData(BaileyGateSide.Left)]
    [InlineData(BaileyGateSide.Top)]
    public void FoundingCanCrossWaterButStarterBuildingsStayDryAndGateReachable(BaileyGateSide side)
    {
        var game = Flat();
        for (int x = 35; x < 90; x++)
            for (int y = 56; y <= 59; y++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var bounds = new CellRect(40, 40, 40, 40);
        var quote = game.QuoteSettlement(bounds, side);
        Assert.Equal(0, quote.Skipped);
        var founded = game.FoundSettlement(bounds, side);
        Assert.True(founded.Success, founded.Message);
        foreach (var area in game.Map.BuildingFootprints.Values)
            Assert.All(area.Cells(), p =>
            {
                Assert.True(game.Map.TerrainAt(p.X, p.Y).Buildable);
                Assert.True(game.Settlement!.AcceptsArrivals(game, p));
            });
        Assert.False(game.Settlement!.AcceptsArrivals(game, new(50, 57)));
        Assert.All(game.Settlement.Walls.Where(w => w.IsTower),
            tower => Assert.True(WorldScale.HasDryTowerFootprint(game.Map, tower.Center)));
        Assert.Contains(game.Settlement.Walls, wall =>
            WallDamageLayout.Fragments(wall).Any(part => part.Kind == WallFragmentKind.Grate));
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Theory]
    [InlineData(WallMaterial.Wood)]
    [InlineData(WallMaterial.Stone)]
    [InlineData(WallMaterial.ReinforcedStone)]
    public void ArchAndIronGrateFollowWaterAndDoNotAlterDryWallArtwork(WallMaterial material)
    {
        var game = Flat();
        for (int y = 20; y <= 23; y++)
            for (int x = 22; x <= 25; x++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var wall = WallDamageLayout.Preview(new(20.5f, 21.5f), new(28.5f, 21.5f), material);
        var dry = WallDamageLayout.Preview(new(20.5f, 30.5f), new(28.5f, 30.5f), material);
        Assert.Equal(WallDamageLayout.Fragments(dry), WallDamageLayout.Fragments(dry, game.Map));
        var parts = WallDamageLayout.Fragments(wall, game.Map);
        var grate = parts.Where(part => part.Kind == WallFragmentKind.Grate).ToArray();
        Assert.NotEmpty(grate);
        float water = WallWaterLayout.Surface(game.Map, new(24, 21.5f)) - WallDamageLayout.GroundHeight(wall, game.Map);
        Assert.True(grate.Min(part => part.Center.Y - part.Size.Y / 2) <= water - WallWaterLayout.GrateDepth + .001f);
        var bars = grate.Where(part => part.Size.Y > .1f).ToArray();
        Assert.True(bars.Max(part => part.Center.Y + part.Size.Y / 2) -
            bars.Min(part => part.Center.Y + part.Size.Y / 2) > .25f);
        foreach (var bar in bars)
            Assert.DoesNotContain(parts, part => part.Kind == WallFragmentKind.Body &&
                Math.Abs(part.Center.X - bar.Center.X) < (part.Size.X + bar.Size.X) / 2 - .001f &&
                part.Center.Y - part.Size.Y / 2 < bar.Center.Y + bar.Size.Y / 2 - .001f);
    }

    [Fact]
    public void DrawnWaterWallsPersistWithoutCreatingBridgesOrWetGates()
    {
        var game = Flat();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        for (int y = 20; y < 25; y++)
            for (int x = 18; x < 23; x++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        game.Paused = true;
        var start = new Vector2(10.5f, 22.5f);
        var end = new Vector2(30.5f, 22.5f);
        var plan = game.PlanWallRun(start, end);
        Assert.True(plan.Valid, plan.Error);
        Assert.True(game.BuildWallRun(start, end).Success);
        Assert.False(game.Map.TerrainAt(20, 22).Buildable);
        Assert.False(game.Settlement!.CanWalk(new(20.5f, 21.5f), new(20.5f, 23.5f)));
        Assert.False(game.PlanWallRun(new(18.5f, 22.5f), new(22.5f, 22.5f), gate: true).Valid);
        string json = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(json);
        Assert.Equal(json, SaveGameStore.Serialize(restored));
        Assert.Contains(restored.Settlement!.Walls, wall => wall.Start is not null &&
            WallDamageLayout.Fragments(wall).Any(part => part.Kind == WallFragmentKind.Grate));
    }

    [Fact]
    public void WaterWallPreviewMatchesPurchasedGeometryAndPausedUndoRestoresTheRun()
    {
        var game = Flat();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        for (int x = 18; x < 23; x++)
            for (int y = 18; y < 25; y++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        game.Paused = true;
        var session = new TermCity.Core.Session.GameSession(game, showGuide: false);
        session.PlaceCursor(new(10, 22));
        session.BeginWallPlacement();
        Assert.True(session.ConfirmPreview().Success);
        session.MoveFixedPlacement(new(30, 22));
        Assert.Null(session.FixedPlacementError);
        var plan = session.Preview!.WallPlan!;
        var ghost = WallDamageLayout.Preview(plan.Start, plan.End, WallMaterial.Wood);
        Assert.Contains(WallDamageLayout.Fragments(ghost, game.Map), part => part.Kind == WallFragmentKind.Grate);
        int money = game.Money, walls = game.Settlement!.Walls.Count;
        Assert.True(session.ConfirmPreview().Success);
        Assert.True(game.Money < money);
        Assert.Contains(game.Settlement.Walls, wall =>
            WallDamageLayout.Fragments(wall).Any(part => part.Kind == WallFragmentKind.Grate));
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(money, session.Game.Money);
        Assert.Equal(walls, session.Game.Settlement!.Walls.Count);
    }

    [Fact]
    public void SiegeApproachesWaterCrossingSettlementsThroughDryWallSectorsAndSavesNormally()
    {
        var game = Flat();
        for (int x = 68; x <= 73; x++)
            for (int y = 0; y < game.Map.Height; y++)
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        Assert.True(game.FoundSettlement(new(40, 40, 64, 64)).Success);
        game.HighestPopulation = 100;
        game.HighestMilestone = 100;
        Assert.True(game.DebugStartSiegeWave(24).Success);
        Assert.All(game.Settlement!.Enemies, enemy => Assert.All(enemy.Route,
            p => Assert.True(game.Map.TerrainAt(p.X, p.Y).Buildable)));
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Fact]
    public void FoundingKeepsTheGratedWallButSkipsTowersWhoseFootprintTouchesWater()
    {
        var game = Flat();
        var bounds = new CellRect(40, 40, 40, 40);
        var nominal = new SettlementState(bounds);
        var site = nominal.Walls.First(w => w.IsTower && w.Cell.Y == bounds.Y && w.Cell.X > bounds.X + 4);
        game.Map.SetTerrain(site.Cell.X + 1, site.Cell.Y, game.Map.Content.Terrains.Get("Water"));
        game.Map.SetTerrain(bounds.X, bounds.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        Assert.True(game.Map.TerrainAt(site.Cell.X, site.Cell.Y).Buildable);
        var result = game.FoundSettlement(bounds);
        Assert.True(result.Success, result.Message);
        Assert.False(game.Settlement!.Walls.Single(w => w.Cell == site.Cell).IsTower);
        Assert.False(game.Settlement.Walls.Single(w => w.Cell == new Pos(bounds.X, bounds.Y)).IsTower);
        Assert.All(game.Settlement.Walls.Where(w => w.IsTower),
            tower => Assert.True(WorldScale.HasDryTowerFootprint(game.Map, tower.Center)));
        Assert.Equal(nominal.Walls.Count, game.Settlement.Walls.Count);
        Assert.True(game.Settlement.WallNetwork.Contains(new(50.5f, 50.5f)));
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DrawnWetCornersRemainWallsWithoutPreviewingOrBuyingATower(bool wetCenter)
    {
        var game = Flat();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        var water = game.Map.Content.Terrains.Get("Water");
        game.Map.SetTerrain(wetCenter ? 30 : 31, 30, water);
        game.Touch();
        Assert.True(game.BuildWallRun(new(10.5f, 30.5f), new(30.5f, 30.5f)).Success);
        var plan = game.PlanWallRun(new(30.5f, 30.5f), new(30.5f, 38.5f));
        Assert.True(plan.Valid, plan.Error);
        Assert.Empty(plan.Towers);
        int money = game.Money;
        Assert.True(game.BuildWallRun(plan.Start, plan.End).Success);
        Assert.Equal(money - plan.Cost, game.Money);
        Assert.DoesNotContain(game.Settlement!.Walls, wall => wall.JunctionTower);
        if (wetCenter)
            Assert.Contains(game.Settlement.Walls, wall => wall.Start is not null &&
                WallDamageLayout.Fragments(wall).Any(part => part.Kind == WallFragmentKind.Grate));
        string saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Fact]
    public void PreviouslySavedWaterTowersRemainLoadableWithoutMutatingTheirState()
    {
        var game = Flat();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        var tower = game.Settlement!.Walls.First(w => w.IsTower);
        game.Map.SetTerrain(tower.Cell.X, tower.Cell.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        Assert.True(restored.Settlement!.Walls.Single(w => w.Id == tower.Id).IsTower);
    }

    [Fact]
    public void FoundingRejectsAWetGateOrNoDryStarterSitesWithoutSpendingGold()
    {
        var game = Flat();
        var water = game.Map.Content.Terrains.Get("Water");
        game.Map.SetTerrain(79, 60, water);
        game.Touch();
        int money = game.Money;
        Assert.False(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        Assert.Null(game.Settlement);
        Assert.Equal(money, game.Money);
    }
}
