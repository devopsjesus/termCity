using System.Numerics;
using System.Text.Json.Nodes;
using System.Reflection;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Features;

namespace TermCity.Tests;

public class WallConstructionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(225)]
    [InlineData(300)]
    public void WallToolSnapsDirectionsAndCommitsTheExactPreview(int degrees)
    {
        var game = Founded();
        foreach (int cell in game.Map.RoadCells.ToArray())
            game.Map.SetRoad(cell % game.Map.Width, cell / game.Map.Width, false);
        game.Touch();
        var start = new Vector2(18.5f, 18.5f);
        float angle = degrees * MathF.PI / 180;
        var pointer = start + new Vector2(MathF.Cos(angle + .04f), MathF.Sin(angle + .04f)) * 10;
        var snapped = game.SnapWallEndpoint(start, pointer);
        var expected = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        Assert.True(Math.Abs(Vector2.Dot(Vector2.Normalize(snapped - start), expected) - 1) < .00001f);
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(new(18, 18));
        session.BeginWallPlacement();
        Assert.True(session.ConfirmPreview().Success);
        session.MoveFixedPlacement(new((int)pointer.X, (int)pointer.Y));
        var preview = session.Preview!.WallPlan!;
        float actual = MathF.Atan2(preview.End.Y - preview.Start.Y, preview.End.X - preview.Start.X) * 180 / MathF.PI;
        float quadrant = (actual % 90 + 90) % 90;
        Assert.Contains(new[] { 0f, 30f, 45f, 60f, 90f }, value => Math.Abs(value - quadrant) < .001f);
        Assert.True(session.ConfirmPreview().Success, session.Message);
        Assert.All(game.Settlement!.Walls.Where(w => !w.IsOriginal && !w.IsTower),
            wall => Assert.True(Vector2.Dot(wall.Tangent, Vector2.Normalize(preview.End - preview.Start)) > .99999f));
        Assert.Equal(preview.End, session.Preview!.WallPlan!.Start);
    }

    private static CityGame Founded()
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 128, MapHeight = 128, StartingMoney = 2_000_000 });
        var result = game.FoundSettlement(new(40, 40, 40, 40));
        Assert.True(result.Success, result.Message);
        game.Paused = true;
        return game;
    }

    private static void Build(CityGame game, Vector2 a, Vector2 b, bool gate = false)
    {
        var result = game.BuildWallRun(a, b, gate);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void ShortWallPurchasePreservesUnchangedCityGeometryAndRoadCaches()
    {
        var game = Founded();
        var roads = game.Network;
        int version = game.MapVersion;
        Build(game, new(10.5f, 30.5f), new(13.5f, 30.5f));
        Assert.Equal(version, game.MapVersion);
        Assert.Same(roads, game.Network);
        Assert.False(game.Settlement!.CanWalk(new(12.5f, 29.5f), new(12.5f, 31.5f)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(30)]
    public void AWallPurchaseCommitsOneTopologyInvalidationRegardlessOfRunLength(int length)
    {
        var game = Founded();
        var siege = game.Settlement!;
        int topology = siege.TopologyRevision;
        Build(game, new(10.5f, 30.5f), new(10.5f + length, 30.5f));
        Assert.Equal(topology + 1, siege.TopologyRevision);
        Assert.Equal(length, siege.Walls.Count(wall => !wall.IsOriginal && !wall.IsTower));
        int count = siege.Walls.Count;
        Assert.False(game.BuildWallRun(new(10.5f, 30.5f), new(10.5f + length, 30.5f)).Success);
        Assert.Equal(count, siege.Walls.Count);
        Assert.Equal(topology + 1, siege.TopologyRevision);
    }

    [Fact]
    public void GateInsertionBatchesRemovedAndReplacementSpansWithoutLosingSharedHealth()
    {
        var game = Founded();
        var siege = game.Settlement!;
        var plan = game.PlanGate(new(40.5f, 60.5f), MathF.PI / 2);
        Assert.True(plan.Valid, plan.Error);
        int topology = siege.TopologyRevision;
        Build(game, plan.Start, plan.End, gate: true);
        Assert.Equal(topology + 1, siege.TopologyRevision);
        Assert.True(siege.CanWalk(new(39.5f, 60.5f), new(41.5f, 60.5f)));
        var saved = SaveGameStore.Serialize(game);
        Assert.Equal(saved, SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)));
    }

    [Fact]
    public void WallPurchaseStillInvalidatesMapArtworkWhenClearingTrees()
    {
        var game = Founded();
        game.Map.SetFeature(12, 30, game.Map.Content.Features.Get(DefaultFeatures.TreeName));
        game.Touch();
        int version = game.MapVersion;
        Build(game, new(10.5f, 30.5f), new(13.5f, 30.5f));
        Assert.True(game.MapVersion > version);
        Assert.Null(game.Map.FeatureAt(12, 30));
    }

    [Fact]
    public void UnchangedWallPointerReusesItsPreviewButMoneyAndTopologyChangesRefreshIt()
    {
        var game = Founded();
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(new(10, 30));
        session.BeginWallPlacement();
        Assert.True(session.ConfirmPreview().Success);
        session.MoveFixedPlacement(new(13, 30));
        var preview = session.Preview;
        session.MoveFixedPlacement(session.Cursor);
        Assert.Same(preview, session.Preview);
        game.Money = 0;
        session.MoveFixedPlacement(session.Cursor);
        Assert.NotSame(preview, session.Preview);
        Assert.False(session.Preview!.WallPlan!.Valid);
        game.Money = 2_000_000;
        session.MoveFixedPlacement(session.Cursor);
        preview = session.Preview;
        Build(game, new(20.5f, 30.5f), new(23.5f, 30.5f));
        session.MoveFixedPlacement(session.Cursor);
        Assert.NotSame(preview, session.Preview);
        Assert.True(session.Preview!.WallPlan!.Valid);
    }

    [Fact]
    public void TowerConnectionsTakePriorityOverDirectionSnapping()
    {
        var game = Founded();
        var tower = game.Settlement!.Walls.First(w => w.IsTower);
        var anchor = tower.Center + new Vector2(9, 4);
        Assert.Equal(tower.Center, game.SnapWallEndpoint(anchor, tower.Center + new Vector2(.5f, .5f)));
        Assert.Equal(anchor, game.SnapWallEndpoint(anchor, anchor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TowerAnchorPivotsTowardThePointerEvenWithinItsFootprint(int degrees)
    {
        var game = Founded();
        var tower = game.Settlement!.Walls.First(wall => wall.IsTower);
        float radians = degrees * MathF.PI / 180;
        var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var pointer = tower.Center + direction * 1.5f;
        var endpoint = game.SnapWallEndpoint(tower.Center, pointer);
        Assert.InRange(Vector2.Distance(endpoint, pointer), 0, .0001f);
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(tower.Cell);
        session.RememberWallPick(tower.Id);
        session.BeginWallPlacement();
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(tower.Center, session.Preview!.WallPlan!.Start);
        Assert.Same(tower, session.WallPlacementStartTower);
        session.MoveFixedPlacement(new((int)(tower.Center.X + direction.X * 8),
            (int)(tower.Center.Y + direction.Y * 8)));
        Assert.Equal(tower.Center, session.Preview!.WallPlan!.Start);
        Assert.True(Vector2.Dot(Vector2.Normalize(session.Preview.WallPlan.End - tower.Center), direction) > .999f);
    }

    [Fact]
    public void OriginalTownAcceptsArrivalsWithoutAnyRoadInsideItsWalls()
    {
        var game = Founded();
        Assert.True(game.Settlement!.AcceptsArrivals(game, new(50, 50)));
        Assert.False(game.Settlement.AcceptsArrivals(game, new(30, 50)));
    }

    [Fact]
    public void DiagonalRunsUsePhysicalLengthAndOverlapIsNotChargedTwice()
    {
        var game = Founded();
        var plan = game.PlanWallRun(new(10.5f, 30.5f), new(13.5f, 34.5f));
        Assert.True(plan.Valid, plan.Error);
        Assert.Equal(5, plan.Length, 5);
        Assert.Equal(100, plan.Cost);
        int before = game.Money;
        Build(game, plan.Start, plan.End);
        Assert.Equal(before - 100, game.Money);
        Assert.False(game.BuildWallRun(plan.Start, plan.End).Success);
        var extension = game.PlanWallRun(plan.Start, new(16.5f, 38.5f));
        Assert.Equal(5, extension.Length, 4);
        Assert.Equal(100, extension.Cost);
    }

    [Fact]
    public void AttachedClosedExtensionNeedsAnActualGateThroughTheOldWall()
    {
        var game = Founded();
        Build(game, new(40.5f, 45.5f), new(10.5f, 45.5f));
        Build(game, new(10.5f, 45.5f), new(10.5f, 74.5f));
        Assert.False(game.Settlement!.AcceptsArrivals(game, new(25, 60)));
        Build(game, new(10.5f, 74.5f), new(40.5f, 74.5f));
        Assert.False(game.Settlement.AcceptsArrivals(game, new(25, 60)));
        var gate = game.PlanGate(new(40.5f, 60.5f), MathF.PI / 2);
        Assert.True(gate.Valid, gate.Error);
        Build(game, gate.Start, gate.End, true);
        Assert.True(game.Settlement.AcceptsArrivals(game, new(25, 60)));
        Assert.True(game.Settlement.AcceptsArrivals(game, new(50, 50)));
        var wall = game.Settlement.Walls.First(w => w.Start is not null && !w.IsGate);
        wall.Health = 0;
        Assert.False(game.Settlement.AcceptsArrivals(game, new(25, 60)));
        Assert.True(game.Settlement.EstablishedAt(new(25, 60)));
        Assert.True(game.Settlement.AcceptsArrivals(game, new(50, 50)));
    }

    [Fact]
    public void ADisconnectedGatedOutpostWaitsForAWallConnectionNotARoad()
    {
        var game = Founded();
        Build(game, new(10.5f, 90.5f), new(30.5f, 90.5f));
        Build(game, new(30.5f, 90.5f), new(30.5f, 110.5f));
        Build(game, new(30.5f, 110.5f), new(10.5f, 110.5f));
        Build(game, new(10.5f, 110.5f), new(10.5f, 90.5f));
        var gate = game.PlanGate(new(20.5f, 90.5f), 0);
        Build(game, gate.Start, gate.End, true);
        Assert.False(game.Settlement!.AcceptsArrivals(game, new(20, 100)));
        Build(game, new(40.5f, 70.5f), new(30.5f, 90.5f));
        Assert.True(game.Settlement.AcceptsArrivals(game, new(20, 100)));
    }

    [Fact]
    public void ShortDrawnWallSpansStillRejectMountedWeapons()
    {
        var game = Founded();
        typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 1_000);
        Build(game, new(10.5f, 30.5f), new(30.5f, 30.5f));
        var span = game.Settlement!.Walls.First(w => !w.IsOriginal && !w.IsTower && w.Length < 1.2f);
        int money = game.Money;
        Assert.Contains("too short", game.WallDefensePlacementError(span.Cell, WallDefenseKind.OilCauldron, span.Id)!);
        Assert.False(game.PlaceWallDefense(span.Cell, WallDefenseKind.OilCauldron, span.Id).Success);
        Assert.Equal(money, game.Money);
        Assert.Equal(WallDefenseKind.None, span.Defense);
    }

    [Theory]
    [InlineData(WallDefenseKind.OilCauldron)]
    [InlineData(WallDefenseKind.Rocks)]
    [InlineData(WallDefenseKind.Ballista)]
    [InlineData(WallDefenseKind.Trebuchet)]
    public void GeneratedCornerTowersSupportWeaponPreviewPurchaseSaveAndUndo(WallDefenseKind kind)
    {
        var game = Founded();
        typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 1_000);
        typeof(CityGame).GetProperty(nameof(CityGame.HighestMilestone))!.SetValue(game, 1_000);
        Build(game, new(10.5f, 30.5f), new(30.5f, 30.5f));
        Build(game, new(30.5f, 30.5f), new(30.5f, 38.5f));
        var tower = Assert.Single(game.Settlement!.Walls, w => w.JunctionTower);
        Assert.True(tower.IsTower);
        Assert.True(tower.Length < 1.2f);
        Assert.Equal(WorldScale.TowerWidth, WallDamageLayout.Width(tower));
        var session = new GameSession(game, showGuide: false);
        session.PlaceCursor(tower.Cell);
        session.RememberWallPick(tower.Id);
        session.SelectCell(tower.Cell);
        session.PreviewWallDefense(kind);
        Assert.True(session.WallDefenseToolActive);
        Assert.Same(tower, session.SelectedWallComponent);
        Assert.Null(session.FixedPlacementError);
        Assert.Equal((tower.Cell, kind), session.ActiveWallDefenseGuide!.Value);
        int rotation = session.Preview!.Rotation;
        session.FlipWallDefenseFacing();
        Assert.NotEqual(rotation, session.Preview.Rotation);
        Assert.Null(session.FixedPlacementError);
        int money = game.Money;
        var result = session.ConfirmPreview();
        Assert.True(result.Success, result.Message);
        Assert.Equal(kind, tower.Defense);
        Assert.Equal(money - WallDefenses.Get(kind).Cost, game.Money);
        Assert.All(game.Settlement.Walls.Where(w => w.Id != tower.Id),
            wall => Assert.Equal(WallDefenseKind.None, wall.Defense));
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        var savedTower = restored.Settlement!.Walls.Single(w => w.Id == tower.Id);
        Assert.True(savedTower.IsTower && savedTower.JunctionTower);
        Assert.Equal(kind, savedTower.Defense);
        Assert.Equal(tower.DefenseFacing, savedTower.DefenseFacing);
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.Equal(WallDefenseKind.None, session.Game.Settlement!.Walls.Single(w => w.Id == tower.Id).Defense);
        Assert.Equal(money, session.Game.Money);
    }

    [Fact]
    public void InvalidStrokeIsAtomicAndDoesNotPlaceValidFragmentsAroundBuildings()
    {
        var game = Founded();
        game.Map.SetBuilding(20, 30, game.Map.Content.Buildings.Get("Cottage"));
        game.Touch();
        int before = game.Money, walls = game.Settlement!.Walls.Count;
        Assert.False(game.BuildWallRun(new(10.5f, 30.5f), new(30.5f, 30.5f)).Success);
        Assert.Equal(before, game.Money);
        Assert.Equal(walls, game.Settlement.Walls.Count);
    }

    [Fact]
    public void ExpandedHomesUseOneCompleteZonedPlotAndBreachesDoNotRefillTheirVacancies()
    {
        var game = Founded();
        Build(game, new(40.5f, 45.5f), new(10.5f, 45.5f));
        Build(game, new(10.5f, 45.5f), new(10.5f, 74.5f));
        Build(game, new(10.5f, 74.5f), new(40.5f, 74.5f));
        var gate = game.PlanGate(new(40.5f, 60.5f), MathF.PI / 2);
        Build(game, gate.Start, gate.End, true);
        var type = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
        var area = type.FootprintAt(18, 55);
        game.Map.SetBuildingFootprint(type, area);
        var family = new Household(2, 1, 0);
        game.Map.SetHousehold(area.X, area.Y, family);
        game.Touch();
        Assert.All(area.Cells(), p => Assert.Equal(ZoneType.Residential, game.Map.ZoneAt(p.X, p.Y)));
        Assert.Equal(area, game.Map.BuildingFootprintAt(area.Right, area.Bottom));
        Assert.True(area.Cells().All(p => game.Settlement!.AcceptsArrivals(game, p)));
        var wall = game.Settlement!.Walls.First(w => w.Start is not null && !w.IsGate);
        wall.Health = 0;
        Assert.Equal(family, game.Map.HouseholdLayer[game.Map.Index(area.X, area.Y)]);
        Assert.False(game.Settlement.AcceptsArrivals(game, new(area.X, area.Y)));
        typeof(CityGame).GetMethod("MoveIn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(game, [10]);
        Assert.Equal(family, game.Map.HouseholdLayer[game.Map.Index(area.X, area.Y)]);
    }

    [Fact]
    public void GateOrientationFollowsTheActualArbitraryWallAngle()
    {
        var game = Founded();
        Build(game, new(10.5f, 30.5f), new(30.5f, 37.5f));
        var direction = Vector2.Normalize(new Vector2(20, 7));
        var center = new Vector2(20.5f, 34);
        var gate = game.PlanGate(center, game.GateAngleAt(center));
        Assert.True(gate.Valid, gate.Error);
        Assert.True(Math.Abs(Vector2.Dot(Vector2.Normalize(gate.End - gate.Start), direction)) > .99999f);
    }

    [Fact]
    public void NewRunsDuringAnUpgradeUseThePaidTargetWithoutChangingTheUpgradeQueue()
    {
        var game = Founded();
        typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 250);
        game.Paused = false;
        game.Update(.1);
        Assert.True(game.UpgradeSettlementWalls().Success);
        var order = game.Settlement!.UpgradeOrder!.ToArray();
        int before = game.Money;
        Build(game, new(10.5f, 30.5f), new(13.5f, 34.5f));
        Assert.Equal(before - 400, game.Money);
        Assert.Equal(order, game.Settlement.UpgradeOrder);
        Assert.All(game.Settlement.Walls.Where(w => !w.IsOriginal), w =>
        {
            Assert.Equal(WallMaterial.Stone, w.Material);
            Assert.Equal(450 * w.Length, w.MaxHealth, 3);
        });
        game.Settlement.AdvanceUpgrades(game, (order.Length + 1) * 3);
        Assert.Null(game.Settlement.UpgradeTargetMaterial);
        Assert.All(game.Settlement.Walls, w => Assert.Equal(WallMaterial.Stone, w.Material));
    }

    [Fact]
    public void ArchersWalkOntoADiagonalExtensionAndFinishAtItsActualSurface()
    {
        var game = Founded();
        typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 250);
        typeof(CityGame).GetProperty(nameof(CityGame.HighestMilestone))!.SetValue(game, 250);
        var state = game.Settlement!;
        game.SetFunding(ServiceKind.Defence, 0);
        var type = game.Map.Content.Buildings.Get("Archer Barracks");
        var home = state.Bounds.Cells().First(p => game.CanPlaceBuilding(type, p.X, p.Y));
        Assert.True(game.PlaceBuilding(type, type.FootprintAt(home.X, home.Y)).Success);
        Assert.True(game.DebugRemoveGoblins().Success);
        Build(game, new(79.5f, 50.5f), new(94.5f, 43.5f));
        var target = state.Walls.Last();
        var gate = state.Walls.First(w => w.IsGate);
        var unit = new SettlementDefender { Id = 1, Home = home, Kind = DefenderKind.Archer,
            X = gate.Center.X, Y = gate.Center.Y };
        state.DefenderList.Add(unit);
        state.NextDefenderId = 2;
        game.SetFunding(ServiceKind.Defence, 0);
        Assert.True(state.DefenderCanReach(game, unit, target.Cell));
        Assert.True(state.CommandDefenders(game, [unit.Id], target.Cell, null).Success);
        game.Paused = false;
        var advance = typeof(SettlementState).GetMethod("AdvanceDefenders", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 600 && unit.Order == DefenderOrder.Move; i++) advance.Invoke(state, [game, .1]);
        Assert.True(unit.Order == DefenderOrder.Automatic,
            $"Order={unit.Order}, position={unit.X:0.000},{unit.Y:0.000}, elevation={unit.Elevation:0.000}, " +
            $"path={unit.PathIndex}/{unit.Path.Count}, next={(unit.PathIndex < unit.Path.Count ? unit.Path[unit.PathIndex] : null)}, goal={target.Cell}.");
        Assert.InRange(WallNetwork.PointSegmentDistance(new((float)unit.X, (float)unit.Y), target.Start!.Value, target.End!.Value),
            0, WallDamageLayout.WalkwayWidth);
        Assert.True(unit.Elevation > 1);
    }

    [Fact]
    public void NetworkSavesAndUndoRetainGeometryAndDamageAndRejectTheOldSchema()
    {
        var game = Founded();
        var session = new GameSession(game);
        session.PlaceCursor(new(10, 30));
        session.BeginWallPlacement();
        Assert.True(session.ConfirmPreview().Success);
        session.MoveFixedPlacement(new(20, 35));
        Assert.True(session.ConfirmPreview().Success);
        Assert.True(session.WallToolActive);
        var wall = game.Settlement!.Walls.Last();
        wall.Health *= .7;
        var saved = SaveGameStore.Serialize(game);
        var loaded = SaveGameStore.Deserialize(saved);
        Assert.Equal(wall.Start, loaded.Settlement!.Walls.Last().Start);
        Assert.Equal(wall.End, loaded.Settlement.Walls.Last().End);
        Assert.Equal(wall.Health, loaded.Settlement.Walls.Last().Health);
        session.CancelPreview();
        session.RequestUndo();
        session.SelectPrompt(0);
        Assert.All(session.Game.Settlement!.Walls, w => Assert.True(w.IsOriginal));
        var json = JsonNode.Parse(saved)!;
        var settlement = json.AsObject().First(pair => pair.Key.Equals("settlement", StringComparison.OrdinalIgnoreCase)).Value!;
        settlement.AsObject().Remove(settlement.AsObject().First(pair =>
            pair.Key.Equals("wallNetworkVersion", StringComparison.OrdinalIgnoreCase)).Key);
        Assert.Throws<InvalidDataException>(() => SaveGameStore.Deserialize(json.ToJsonString()));
    }
}
