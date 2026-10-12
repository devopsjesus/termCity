using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Tests;

public class RoadMeshLayoutTests
{
    private static RoadPath Path(GameMap map, bool closed, params Vector2[] points) =>
        new(map.Content.Roads.Default, true, closed, points.Select(p => (double)p.X * 12).ToArray(),
            points.Select(p => (double)p.Y * 22).ToArray());

    [Fact]
    public void RoadAndShoulderEdgesAreOffsetsOfTheSameCenterline()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        Assert.NotEmpty(layout.Segments);
        foreach (var segment in layout.Segments)
        {
            Assert.All(segment.Road, p => Assert.Equal(RoadMeshLayout.HalfWidth, Math.Abs(p.Y - 5), 5));
            Assert.All(segment.Corridor, p => Assert.Equal(RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth,
                Math.Abs(p.Y - 5), 5));
        }
        Assert.NotNull(layout.RoadAt(new(10, 5.2f)));
        Assert.Null(layout.RoadAt(new(10, 5.35f)));
        Assert.True(layout.InCorridor(new(10, 5.35f)));
        Assert.False(layout.InCorridor(new(10, 5.5f)));
    }

    [Fact]
    public void BentRoadEdgesMeetExactlyWithoutShoulderGaps()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(10, 5), new(10, 12))]);
        for (int i = 1; i < layout.Segments.Count; i++)
        {
            var previous = layout.Segments[i - 1]; var current = layout.Segments[i];
            Assert.Equal(previous.B, current.A);
            foreach (var point in previous.Road.Where(p => current.Road.Contains(p)))
                Assert.True(layout.InCorridor(point));
        }
        Assert.NotNull(layout.RoadAt(new(10, 5)));
    }

    [Fact]
    public void GradeQueriesAreContinuousAcrossCellAndChunkBoundaries()
    {
        var map = new GameMap(64, 48, new GameContent());
        for (int y = 7; y < 23; y++)
            for (int x = 8; x < 28; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var layout = new RoadMeshLayout(map, [Path(map, false, new(4, 14.5f), new(34, 14.5f))]);
        Assert.True(layout.HeightAt(new(16, 14.5f)) > layout.HeightAt(new(4, 14.5f)));
        for (int x = 5; x < 33; x++)
            Assert.InRange(Math.Abs(layout.HeightAt(new(x - 0.00001f, 14.5f)) -
                layout.HeightAt(new(x + 0.00001f, 14.5f))), 0, 0.0001f);
    }

    [Fact]
    public void RoadSettlesOntoTheSummitWithoutASparseSamplingTail()
    {
        var map = new GameMap(80, 64, new GameContent());
        for (int y = 8; y < 56; y++)
            for (int x = 8; x < 72; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        var layout = new RoadMeshLayout(map, [Path(map, false, new(3.5f, 32.5f), new(76.5f, 32.5f))]);
        for (float x = 16; x <= 64; x += 0.25f)
        {
            float terrace = TerrainSprite.Elevation(map, (int)x, 32);
            Assert.Equal(terrace, layout.HeightAt(new(x, 32.5f)), 5);
            Assert.Equal(terrace, layout.SupportedShoulderHeight(new(x, 32.92f), terrace), 5);
        }
        Assert.True(layout.Segments.Count < 45, "Level stretches must still coalesce for cached terrain rendering.");
    }

    [Theory]
    [InlineData(0.03f)]
    [InlineData(-0.03f)]
    [InlineData(0.1f)]
    [InlineData(-0.1f)]
    public void ShallowRoadsideCutsMeetTheTerraceInsteadOfLeavingThinWalls(float terrace)
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        foreach (int side in new[] { -1, 1 })
        {
            Assert.Equal(0, layout.SupportedShoulderHeight(new(10, 5 + side * RoadMeshLayout.HalfWidth), terrace), 5);
            Assert.Equal(terrace / 2,
                layout.SupportedShoulderHeight(new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth / 2)), terrace), 5);
            Assert.Equal(terrace,
                layout.SupportedShoulderHeight(new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth)), terrace), 5);
        }
    }

    [Fact]
    public void SteepCuttingsKeepSolidEarthFacesRatherThanSmallShoulderWedges()
    {
        var map = TestCity.Flat().Map;
        var layout = new RoadMeshLayout(map, [Path(map, false, new(5, 5), new(15, 5))]);
        foreach (int side in new[] { -1, 1 })
            Assert.Equal(0, layout.SupportedShoulderHeight(
                new(10, 5 + side * (RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth)),
                TerrainSprite.HillStepHeight), 5);
    }

    [Fact]
    public void JunctionsResolveRoadTypeWithoutConflictingHeightSources()
    {
        var map = TestCity.Flat().Map;
        var minor = Path(map, false, new(5, 10), new(15, 10));
        var major = new RoadPath(map.Content.Roads.OrderByDescending(r => r.Rank).First(), true, false,
            [10 * 12.0, 10 * 12.0], [5 * 22.0, 15 * 22.0]);
        var layout = new RoadMeshLayout(map, [minor, major]);
        Assert.Same(major.Type, layout.RoadAt(new(10, 10))!.Type);
        Assert.Equal(RoadMeshLayout.SurfaceLift, layout.HeightAt(new(10, 10)), 5);
        Assert.True(layout.InRoadInterior(new(10, 10)));
    }

    [Fact]
    public void ClosedRoadsAndSingleCellsHaveValidCompleteRibbons()
    {
        var map = TestCity.Flat().Map;
        var loop = new RoadMeshLayout(map, [Path(map, true, new(5, 5), new(10, 5), new(10, 10), new(5, 10))]);
        Assert.Equal(loop.Segments[0].A, loop.Segments[^1].B);
        Assert.Equal(loop.Segments[0].HeightA, loop.Segments[^1].HeightB);
        Assert.NotNull(loop.RoadAt(new(5, 7)));
        var isolated = new RoadMeshLayout(map, [Path(map, false, new Vector2(20.5f, 10.5f))]);
        Assert.NotNull(isolated.RoadAt(new(20.5f, 10.5f)));
        Assert.True(isolated.InCorridor(new(20.5f, 10.8f)));
    }

    [Fact]
    public void WaterCrossingsRemainRaisedAndDoNotChangeBridgeGameplay()
    {
        var map = TestCity.Flat().Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 12; x < 18; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int x = 8; x < 22; x++) map.SetRoad(x, 10, true);
        var layout = new RoadMeshLayout(map, [Path(map, false, new(8.5f, 10.5f), new(21.5f, 10.5f))]);
        Assert.True(layout.HeightAt(new(15.5f, 10.5f)) > 0.05f);
        Assert.True(map.HasRoad(15, 10));
        Assert.False(map.TerrainAt(15, 10).Buildable);
        Assert.NotNull(layout.RoadAt(new(15.5f, 10.5f)));
        Assert.True(layout.RoadAt(new(15.5f, 10.5f))!.Bridge);
    }

    [Fact]
    public void PlayerRoadStrokeAcrossWaterBuildsAndRendersTheEntireBridge()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        for (int y = 2; y < map.Height - 2; y++)
            for (int x = 12; x < 24; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var stroke = Enumerable.Range(8, 20).Select(x => new TermCity.Core.Util.Pos(x, 10)).ToArray();
        var quote = game.QuoteRoad(stroke);
        Assert.Equal(stroke.Length, quote.Cells);
        var result = game.BuildRoad(stroke);
        Assert.True(result.Success, result.Message);
        var layout = new RoadMeshLayout(map, game.Network.DrawnPaths);
        foreach (var p in stroke)
        {
            Assert.True(map.HasRoad(p.X, p.Y));
            var segment = layout.RoadAt(new(p.X + 0.5f, p.Y + 0.5f));
            Assert.NotNull(segment);
            if (!map.TerrainAt(p.X, p.Y).Buildable)
            {
                Assert.True(segment.Bridge);
                Assert.True(layout.HeightAt(new(p.X + 0.5f, p.Y + 0.5f)) > 0.17f);
            }
        }
        var restored = TermCity.Core.Persistence.SaveGameStore.Deserialize(
            TermCity.Core.Persistence.SaveGameStore.Serialize(game));
        foreach (var p in stroke) Assert.True(restored.Map.HasRoad(p.X, p.Y));
    }

    [Theory]
    [InlineData(8, 10, 28, 26)]
    [InlineData(28, 26, 8, 10)]
    [InlineData(8, 26, 28, 10)]
    [InlineData(28, 10, 8, 26)]
    [InlineData(8, 10, 28, 30)]
    public void AngledPlayerCrossingsRemainOneConnectedRoadWithBothBridgeApproaches(
        int ax, int ay, int bx, int by)
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        for (int y = 0; y < map.Height; y++)
            for (int x = 14; x <= 21; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var session = new TermCity.Core.Session.GameSession(game);
        session.PlaceCursor(new(ax, ay));
        session.BeginRoadLine();
        session.UpdateDrag(new(bx, by));
        var stroke = TermCity.Core.Util.CellLines.Between(new(ax, ay), new(bx, by));
        Assert.Equal(stroke.Count, session.Preview!.Quote.Cells);
        var result = session.ConfirmPreview();
        Assert.True(result.Success, result.Message);
        foreach (var p in stroke) Assert.True(map.HasRoad(p.X, p.Y), $"Stroke lost {p}.");
        var paths = game.Network.DrawnPaths;
        var layout = new RoadMeshLayout(map, paths);
        Assert.Single(paths);
        Assert.Contains(layout.Segments, s => s.Bridge);
        for (int i = 1; i < layout.Segments.Count; i++)
            Assert.Equal(layout.Segments[i - 1].B, layout.Segments[i].A);
    }

    [Theory]
    [InlineData(8, 10, 28, 26)]
    [InlineData(28, 26, 8, 10)]
    [InlineData(8, 26, 28, 10)]
    public void SelectedRiverCrossingBuildsOneStrokeInsteadOfScatteredRoadsInTheRectangle(
        int ax, int ay, int bx, int by)
    {
        var game = TestCity.Flat();
        var map = game.Map;
        foreach (int cell in map.RoadCells.ToArray()) map.SetRoad(cell % map.Width, cell / map.Width, false);
        for (int y = 0; y < map.Height; y++)
            for (int x = 14; x <= 21; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        game.Touch();
        var session = new TermCity.Core.Session.GameSession(game);
        session.BeginDrag(new(ax, ay));
        session.UpdateDrag(new(bx, by));
        session.EndSelection();
        session.PreviewRoad();
        var stroke = TermCity.Core.Util.CellLines.Between(new(ax, ay), new(bx, by));
        Assert.Equal(stroke.Count, session.Preview!.Quote.Cells);
        Assert.True(session.ConfirmPreview().Success);
        Assert.Equal(stroke.Count, map.RoadCells.Count);
        Assert.Single(game.Network.DrawnPaths);
    }

    [Fact]
    public void ShortDeepValleysBecomeBridgeSpansInsteadOfSteepDips()
    {
        var map = new GameMap(48, 32, new GameContent());
        var hill = map.Content.Terrains.Get("Hill");
        for (int y = 5; y < 25; y++)
            for (int x = 4; x < 35; x++)
                if (x < 17 || x > 20) map.SetTerrain(x, y, hill);
        var layout = new RoadMeshLayout(map, [Path(map, false, new(8.5f, 15.5f), new(31.5f, 15.5f))]);
        Assert.Contains(layout.Segments, segment => segment.Bridge && segment.A.X < 19 && segment.B.X > 17);
        Assert.True(layout.HeightAt(new(19, 15.5f)) > 0.3f);
        Assert.Equal(0, TerrainSprite.Elevation(map, 19, 15));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(80)]
    public void ThinTerraceDetectionIsIndependentOfRoadAngle(float angle)
    {
        var rotation = Matrix3x2.CreateRotation(angle * MathF.PI / 180);
        Vector2[] thin = [new(0, 0), new(2, 0), new(2, 0.1f), new(0, 0.1f)];
        Vector2[] broad = [new(0, 0), new(2, 0), new(2, 0.6f), new(0, 0.6f)];
        Assert.True(RoadMeshLayout.IsThinRemnant(thin.Select(p => Vector2.Transform(p, rotation)).ToArray()));
        Assert.False(RoadMeshLayout.IsThinRemnant(broad.Select(p => Vector2.Transform(p, rotation)).ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(80)]
    public void TerraceJoinsReplaceSharpTipsWithABoundedSmoothCurve(float angle)
    {
        var rotation = Matrix3x2.CreateRotation(angle * MathF.PI / 180);
        var before = Vector2.Transform(new(1, 0), rotation);
        var after = Vector2.Transform(new(0, 1), rotation);
        var clearance = RoadMeshLayout.RoundedTerraceCorner(before, Vector2.Zero, after);
        Assert.Equal(Vector2.Zero, clearance[0]);
        Assert.Equal(before * 0.35f, clearance[1]);
        Assert.Equal(after * 0.35f, clearance[^1]);
        for (int i = 2; i < clearance.Length - 1; i++)
        {
            var incoming = Vector2.Normalize(clearance[i] - clearance[i - 1]);
            var outgoing = Vector2.Normalize(clearance[i + 1] - clearance[i]);
            Assert.True(Vector2.Dot(incoming, outgoing) > MathF.Cos(15 * MathF.PI / 180),
                "Rounded cut joins must not introduce another sharp corner.");
            Assert.InRange(clearance[i].Length(), 0.12f, 0.35f);
        }
    }

    [Fact]
    public void GeometryIsDerivedWithoutChangingSavedGameplayLayers()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        string before = TermCity.Core.Persistence.SaveGameStore.Serialize(game);
        var layout = new RoadMeshLayout(game.Map, game.Network.DrawnPaths);
        Assert.NotEmpty(layout.Segments);
        foreach (var segment in layout.Segments)
            Assert.True(float.IsFinite(layout.HeightAt((segment.A + segment.B) / 2)));
        Assert.Equal(before, TermCity.Core.Persistence.SaveGameStore.Serialize(game));
    }
}
