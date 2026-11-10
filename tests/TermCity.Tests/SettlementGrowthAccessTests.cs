using System.Numerics;
using System.Reflection;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class SettlementGrowthAccessTests
{
    private static CityGame Extended()
    {
        var game = TestCity.Flat(config: new GameConfig
        {
            MapWidth = 160, MapHeight = 160, StartingMoney = 2_000_000,
        }, rules: CityRules.Full);
        foreach (int cell in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(cell % game.Map.Width, cell / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        foreach (var (a, b) in new (Vector2, Vector2)[]
        {
            (new(79.5f, 40.5f), new(110.5f, 40.5f)),
            (new(110.5f, 40.5f), new(110.5f, 79.5f)),
            (new(110.5f, 79.5f), new(79.5f, 79.5f)),
            (new(40.5f, 79.5f), new(40.5f, 110.5f)),
            (new(40.5f, 110.5f), new(79.5f, 110.5f)),
            (new(79.5f, 110.5f), new(79.5f, 79.5f)),
        })
            Assert.True(game.BuildWallRun(a, b).Success);
        var gate = game.PlanGate(new(60.5f, 79.5f), 0);
        Assert.True(game.BuildWallRun(gate.Start, gate.End, true).Success);
        var chapel = game.Map.Content.Buildings.Get("Chapel");
        game.Map.SetBuildingFootprint(chapel, chapel.FootprintAt(50, 90));
        game.Touch();
        return game;
    }

    private static bool Access(CityGame game, CellRect area) =>
        (bool)typeof(CityGame).GetMethod("SettlementAccessSurvives", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(game, [area, -1])!;

    [Fact]
    public void GateBoundaryTransitDoesNotFalselyStrandOtherRegionsOrBlockNewHomes()
    {
        var game = Extended();
        var area = new CellRect(90, 50, 5, 5);
        Assert.All(area.Cells(), cell => Assert.True(game.Settlement!.AcceptsArrivals(game, cell)));
        Assert.True(Access(game, area));
        string saved = SaveGameStore.Serialize(game);
        var restored = SaveGameStore.Deserialize(saved);
        Assert.True(Access(restored, area));
        Assert.Equal(saved, SaveGameStore.Serialize(restored));
        Assert.All(game.Settlement!.Walls.Where(w => w.IsGate),
            gate => Assert.False(game.Settlement.AcceptsArrivals(game, gate.Cell)));
    }

    [Fact]
    public void EmptyUnzonedLandExplainsEligibilityWithoutPromisingImmediateDevelopment()
    {
        var game = Extended();
        var cell = new Pos(92, 52);
        Assert.Equal(ZoneType.None, game.Map.ZoneAt(cell.X, cell.Y));
        string saved = SaveGameStore.Serialize(game);
        var diagnostic = GrowthDiagnostics.ForSettlementCell(game, cell);
        Assert.Equal(GrowthStatus.Ready, diagnostic.Status);
        Assert.Contains("may choose another", diagnostic.Message);
        Assert.Contains("No zoning", diagnostic.Message);
        Assert.Contains("Settlement growth:", CellInspector.Summary(game, cell));
        Assert.True(GrowthDiagnostics.ForZone(game, ZoneType.Residential).EligibleVacancies > 0);
        Assert.Equal(saved, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void GrowthUsesEligibleExtensionSitesWithoutChangingTheScoringRules()
    {
        var game = Extended();
        foreach (var cell in game.Settlement!.WallBounds.Cells().Where(p => p.X <= game.Settlement.Bounds.Right))
            if (game.Map.BuildingAt(cell.X, cell.Y) is null)
                game.Map.PathLayer[game.Map.Index(cell.X, cell.Y)] = 3;
        game.Touch();
        bool developed = false;
        for (int attempt = 0; attempt < 30 && !developed; attempt++)
        {
            var index = game.DevelopSettlementBuilding(ZoneType.Residential);
            Assert.NotNull(index);
            var cell = game.Map.PosOf(index.Value);
            if (cell.X <= game.Settlement!.Bounds.Right) continue;
            developed = true;
            Assert.All(game.Map.BuildingFootprintAt(cell.X, cell.Y).Cells(),
                p => Assert.True(game.Settlement.AcceptsArrivals(game, p)));
        }
        Assert.True(developed, "Organic site scoring must be able to choose the eligible eastern extension.");
    }

    [Fact]
    public void BlockedGatesAndBreachesHaveSpecificFeedbackAndInvalidateCachedEligibility()
    {
        var game = Extended();
        var state = game.Settlement!;
        var cell = new Pos(92, 52);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForSettlementCell(game, cell).Status);
        var gate = state.Walls.First(w => w.IsOriginal && w.IsGate);
        var obstruction = new CellRect(gate.Cell.X - 1, gate.Cell.Y - 4, 3, 9);
        foreach (var p in obstruction.Cells())
            game.Map.SetTerrain(p.X, p.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var blocked = GrowthDiagnostics.ForSettlementCell(game, cell);
        Assert.Equal(GrowthStatus.NoGateAccess, blocked.Status);
        Assert.Contains("gate route", blocked.Message);
        foreach (var p in obstruction.Cells())
            game.Map.SetTerrain(p.X, p.Y, game.Map.Content.Terrains.Base);
        game.Touch();
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForSettlementCell(game, cell).Status);
        var wall = state.Walls.First(w => w.Start is not null && !w.IsTower && !w.IsGate && w.Center.X > 109);
        wall.Health = 0;
        Assert.Equal(GrowthStatus.NeedsEnclosure, GrowthDiagnostics.ForSettlementCell(game, cell).Status);
        Assert.Equal(ZoneType.None, game.Map.ZoneAt(gate.Cell.X, gate.Cell.Y));
    }

    [Fact]
    public void ExistingStrandedBuildingsBlockGrowthWithAnActionableNamedReason()
    {
        var game = Extended();
        var point = new Pos(92, 52);
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForSettlementCell(game, point).Status);
        var chapel = game.Map.Content.Buildings.Get("Chapel").FootprintAt(50, 90);
        var border = new CellRect(chapel.X - 1, chapel.Y - 1, chapel.Width + 2, chapel.Height + 2)
            .Cells().Where(p => !chapel.Contains(p)).ToArray();
        foreach (var cell in border)
            game.Map.SetTerrain(cell.X, cell.Y, game.Map.Content.Terrains.Get("Water"));
        game.Touch();
        var blocked = GrowthDiagnostics.ForSettlementCell(game, point);
        Assert.Equal(GrowthStatus.BlockedAccess, blocked.Status);
        Assert.Contains("Chapel", blocked.Message);
        Assert.Contains("(50,90)", blocked.Message);
        var global = GrowthDiagnostics.ForZone(game, ZoneType.Residential);
        Assert.Equal(GrowthStatus.BlockedAccess, global.Status);
        Assert.Equal(0, global.EligibleVacancies);
        Assert.Contains("Chapel", global.Message);
        foreach (var cell in border)
            game.Map.SetTerrain(cell.X, cell.Y, game.Map.Content.Terrains.Base);
        game.Touch();
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForSettlementCell(game, point).Status);
        Assert.True(GrowthDiagnostics.ForZone(game, ZoneType.Residential).EligibleVacancies > 0);
    }

    [Fact]
    public void CrampedPlotsAndUnsafeConstructionRemainRejected()
    {
        var game = Extended();
        var state = game.Settlement!;
        var cell = new Pos(109, 50);
        Assert.Equal(GrowthStatus.NeedsSpace, GrowthDiagnostics.ForSettlementCell(game, cell).Status);
        var home = game.Map.Content.Buildings.Get("Cottage");
        game.Map.SetBuildingFootprint(home, home.FootprintAt(90, 60));
        game.Touch();
        Assert.False(Access(game, new(75, 40, 9, 40)));
        Assert.Contains("Cottage", game.SettlementAccessError(new(75, 40, 9, 40))!);
        Assert.False(state.AcceptsArrivals(game, state.Walls.First(w => w.IsGate).Cell));
    }

    [Fact]
    public void AngledGatesAndOverlandAccessToConnectedOutpostsUseTheSameSafetyRules()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        foreach (int cell in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(cell % game.Map.Width, cell / game.Map.Width, false);
        game.Touch();
        Assert.True(game.FoundSettlement(new(40, 40, 40, 40)).Success);
        Vector2[] points = [new(20.5f, 20.5f), new(35.5f, 35.5f), new(20.5f, 50.5f), new(5.5f, 35.5f)];
        for (int i = 0; i < points.Length; i++)
            Assert.True(game.BuildWallRun(points[i], points[(i + 1) % points.Length]).Success);
        var sample = new Pos(20, 35);
        Assert.Equal(GrowthStatus.NeedsEnclosure, GrowthDiagnostics.ForSettlementCell(game, sample).Status);
        var gate = game.PlanGate(new(28, 28), MathF.PI / 4);
        Assert.True(game.BuildWallRun(gate.Start, gate.End, true).Success);
        Assert.True(game.BuildWallRun(new(35.5f, 35.5f), new(40.5f, 45.5f)).Success);
        Assert.True(game.Settlement!.AcceptsArrivals(game, sample));
        Assert.True(Access(game, new(18, 33, 5, 5)));
        Assert.Equal(GrowthStatus.Ready, GrowthDiagnostics.ForSettlementCell(game, sample).Status);
    }
}
