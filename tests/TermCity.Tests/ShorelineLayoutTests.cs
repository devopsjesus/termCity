using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.Tests;

public class ShorelineLayoutTests
{
    [Fact]
    public void BankColorCoversEveryStraightAndSteppedWaterEdge()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int column = 0; column < map.Width; column++)
        {
            int edge = 8 + column / 4 % 2;
            for (int row = edge; row < map.Height; row++)
                map.SetTerrain(column, row, map.Content.Terrains.Get("Water"));
            Assert.True(ShorelineLayout.BankCell(map, column, edge - 1));
        }
        for (int row = 0; row < map.Height; row++)
            for (int column = 0; column < map.Width; column++)
            {
                bool expected = map.TerrainAt(column, row).Buildable &&
                    new[] { (column - 1, row), (column + 1, row), (column, row - 1), (column, row + 1) }
                        .Any(neighbor => map.InBounds(neighbor.Item1, neighbor.Item2) &&
                            !map.TerrainAt(neighbor.Item1, neighbor.Item2).Buildable);
                Assert.Equal(expected, ShorelineLayout.BankCell(map, column, row));
            }
    }

    [Fact]
    public void BankColorExcludesDiagonalOnlyContactsAndOffMapAtlasPadding()
    {
        var map = new GameMap(24, 24, new GameContent());
        map.SetTerrain(0, 0, map.Content.Terrains.Get("Water"));
        map.SetTerrain(1, 0, map.Content.Terrains.Get("Hill"));
        Assert.False(ShorelineLayout.BankCell(map, 0, 0));
        Assert.False(ShorelineLayout.BankCell(map, 1, 0));
        Assert.True(ShorelineLayout.BankCell(map, 0, 1));
        Assert.False(ShorelineLayout.BankCell(map, 1, 1));
        Assert.False(ShorelineLayout.BankCell(map, 2, 0));
        Assert.False(ShorelineLayout.BankCell(map, -1, 0));
        Assert.False(ShorelineLayout.BankCell(map, 0, -1));
        Assert.False(ShorelineLayout.BankCell(map, map.Width, 0));
        Assert.False(ShorelineLayout.BankCell(map, 0, map.Height));
    }

    [Fact]
    public void RiverBanksDoNotRecolorHilltops()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int row = 0; row < map.Height; row++)
        {
            map.SetTerrain(12, row, map.Content.Terrains.Get("Water"));
            map.SetRiverWater(12, row, true);
            map.SetTerrain(11, row, map.Content.Terrains.Get("Hill"));
            Assert.False(ShorelineLayout.BankCell(map, 11, row));
            Assert.True(ShorelineLayout.BankCell(map, 13, row));
        }
    }

    [Fact]
    public void BlockyShorelinesUseWholeTerrainBlocksWithoutChangingRoadProfiles()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int row = 0; row < map.Height; row++)
            for (int column = Math.Max(1, row / 2); column < map.Width; column++)
                map.SetTerrain(column, row, map.Content.Terrains.Get("Water"));
        map.SetTerrain(8, 16, map.Content.Terrains.Get("Hill"));
        var path = new RoadPath(map.Content.Roads.Default, true, false, [6 * 12d, 14 * 12d], [16 * 22d, 16 * 22d]);
        var before = new RoadMeshLayout(map, [path]).Segments.ToArray();
        var elevations = Enumerable.Range(0, map.Width * map.Height)
            .Select(index => TerrainSprite.Elevation(map, index % map.Width, index / map.Width)).ToArray();
        for (int row = 0; row < map.Height; row++)
            for (int column = 0; column < map.Width; column++)
            {
                var patch = Assert.Single(ShorelineLayout.BlockyCell(map, column, row));
                Assert.Equal(!map.TerrainAt(column, row).Buildable, patch.Water);
                Assert.Equal(1, Area(patch.Polygon), 5);
                Assert.All(patch.Polygon, point =>
                {
                    Assert.Equal(MathF.Round(point.X), point.X);
                    Assert.Equal(MathF.Round(point.Y), point.Y);
                });
                for (int edge = 0; edge < patch.Polygon.Length; edge++)
                {
                    var start = patch.Polygon[edge];
                    var end = patch.Polygon[(edge + 1) % patch.Polygon.Length];
                    Assert.True(start.X == end.X || start.Y == end.Y);
                }
                Assert.Equal(elevations[map.Index(column, row)], TerrainSprite.Elevation(map, column, row));
            }
        var after = new RoadMeshLayout(map, [path]).Segments.ToArray();
        Assert.Equal(before.Length, after.Length);
        for (int segment = 0; segment < before.Length; segment++)
        {
            Assert.Equal(before[segment].A, after[segment].A);
            Assert.Equal(before[segment].B, after[segment].B);
            Assert.Equal(before[segment].HeightA, after[segment].HeightA);
            Assert.Equal(before[segment].HeightB, after[segment].HeightB);
            Assert.Equal(before[segment].Bridge, after[segment].Bridge);
            Assert.Equal(before[segment].Road, after[segment].Road);
            Assert.Equal(before[segment].Corridor, after[segment].Corridor);
        }
    }

    [Fact]
    public void ShorelinePartitionsCellsWithoutOverlappingLandAndWater()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int y = 0; y < 24; y++)
            for (int x = 12; x < 24; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int x = 9; x <= 14; x++)
        {
            var patches = ShorelineLayout.Cell(map, x, 12);
            Assert.InRange(Math.Abs(patches.Sum(p => Area(p.Polygon)) - 1), 0, 0.00001);
            foreach (var patch in patches)
            {
                var center = patch.Polygon.Aggregate(Vector2.Zero, (sum, p) => sum + p) / patch.Polygon.Length;
                Assert.Equal(patch.Water,
                    TerrainSprite.WaterSurface(map, center.X, center.Y) >= ShorelineLayout.WaterThreshold);
            }
        }
        Assert.Contains(ShorelineLayout.Cell(map, 11, 12), p => p.Water);
        Assert.Contains(ShorelineLayout.Cell(map, 11, 12), p => !p.Water);
    }

    [Fact]
    public void AdjacentShorelineCellsUseIdenticalCrossings()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int y = 0; y < 24; y++)
            for (int x = Math.Max(1, y / 2); x < 24; x++)
                map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int y = 1; y < 22; y++)
            for (int x = 1; x < 22; x++)
            {
                var left = ShorelineLayout.Cell(map, x, y).SelectMany(p => p.Polygon)
                    .Where(p => p.X == x + 1 && p.Y > y && p.Y < y + 1).Distinct().OrderBy(p => p.Y).ToArray();
                var right = ShorelineLayout.Cell(map, x + 1, y).SelectMany(p => p.Polygon)
                    .Where(p => p.X == x + 1 && p.Y > y && p.Y < y + 1).Distinct().OrderBy(p => p.Y).ToArray();
                if (left.Length > 0 && right.Length > 0) Assert.Equal(left, right);
            }
    }

    private static double Area(Vector2[] polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i] - polygon[0]; var b = polygon[(i + 1) % polygon.Length] - polygon[0];
            sum += a.X * b.Y - a.Y * b.X;
        }
        return Math.Abs(sum) / 2;
    }

    [Fact]
    public void IslandsPondsAndFragmentedBanksKeepCompleteNonoverlappingCoverage()
    {
        for (int seed = 0; seed < 8; seed++)
        {
            var map = new GameMap(16, 16, new GameContent());
            var random = new Random(seed);
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    if (random.NextDouble() < 0.45) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    Assert.InRange(Math.Abs(ShorelineLayout.Cell(map, x, y).Sum(p => Area(p.Polygon)) - 1), 0, 0.00001);
        }
    }

    [Fact]
    public void BeachRegionsAreBroadContinuousPatchesRatherThanAnEverywhereFringe()
    {
        int dry = 0, sandy = 0, longestSandyRun = 0;
        for (int y = 0; y < 160; y += 4)
        {
            int run = 0;
            for (int x = 0; x < 160; x++)
            {
                float strength = ShorelineLayout.BeachStrength(x, y);
                Assert.InRange(strength, 0, 1);
                Assert.InRange(Math.Abs(strength - ShorelineLayout.BeachStrength(x + 0.01f, y)), 0, 0.01f);
                if (strength == 0) dry++;
                if (strength == 1) sandy++;
                run = strength > 0.8f ? run + 1 : 0;
                longestSandyRun = Math.Max(longestSandyRun, run);
            }
        }
        Assert.True(dry > 100);
        Assert.True(sandy > 100);
        Assert.True(longestSandyRun >= 8);
    }

    [Fact]
    public void PerspectiveAtlasHasNoBakedSandOrMixedShorelinePixels()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 12; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        foreach (int x in new[] { 10, 11, 12, 13 })
        {
            var tile = TerrainSprite.Ground(map, x, 12, hillEdges: false, shorelineBlend: false);
            for (int pixel = 0; pixel < tile.Pixels.Length; pixel += 4)
                if (x < 12)
                    Assert.True(tile.Pixels[pixel + 1] > tile.Pixels[pixel] &&
                        tile.Pixels[pixel + 1] > tile.Pixels[pixel + 2], "Land atlas pixels must remain grass, not a baked tan stripe.");
                else
                    Assert.True(tile.Pixels[pixel + 2] > tile.Pixels[pixel + 1] &&
                        tile.Pixels[pixel + 2] > tile.Pixels[pixel], "Water atlas pixels must remain blue up to the mesh boundary.");
        }
    }

    [Fact]
    public void BeachEligibilityIncludesInlandCellsWhoseEdgesStillReachTheSlope()
    {
        var map = new GameMap(64, 64, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 32; x < map.Width; x++)
            {
                map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
                map.SetSeaWater(x, y, true);
            }
        int covered = 0;
        for (int y = 1; y < 63; y++)
            for (int x = 28; x < 32; x++)
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 3; column++)
                        if (ShorelineLayout.BeachAmount(map, x + column * 0.5f, y + row * 0.5f) > 0)
                        {
                            Assert.True(ShorelineLayout.BeachTouchesCell(map, x, y));
                            covered++;
                        }
        Assert.True(covered > 0);
    }

    [Fact]
    public void NaturalFeaturesMustFitEntirelyOnTheRenderedLandSide()
    {
        var map = new GameMap(24, 24, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 12; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        Assert.False(ShorelineLayout.NaturalFeatureFits(map, 12, 12));
        Assert.False(ShorelineLayout.NaturalFeatureFits(map, 11, 12));
        Assert.True(ShorelineLayout.NaturalFeatureFits(map, 10, 12));
        var tree = map.Content.Features.Get("Tree");
        var generator = new TermCity.Core.Features.ScatterFeatureGenerator { BaseDensity = 1 };
        generator.Generate(new TermCity.Core.Terrain.GenerationContext(map, 42), tree);
        Assert.Null(map.FeatureAt(11, 12));
        Assert.NotNull(map.FeatureAt(10, 12));
    }

    [Fact]
    public void NaturalFeatureClearanceMatchesAllFootprintSamplesOnFragmentedBanks()
    {
        for (int seed = 0; seed < 8; seed++)
        {
            var map = new GameMap(24, 24, new GameContent());
            var random = new Random(seed);
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                    if (random.NextDouble() < 0.2) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    bool expected = map.TerrainAt(x, y).AllowsFeatures;
                    for (int row = -1; row <= 1; row++)
                        for (int column = -1; column <= 1; column++)
                            expected &= TerrainSprite.WaterSurface(map, x + 0.5f + column * 0.4f,
                                y + 0.5f + row * 0.4f) < ShorelineLayout.WaterThreshold;
                    Assert.Equal(expected, ShorelineLayout.NaturalFeatureFits(map, x, y));
                }
        }
    }

    [Fact]
    public void SandyBeachesRequireSeaAndExtendInlandBeyondTheNarrowDarkBank()
    {
        var map = new GameMap(64, 64, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 32; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int y = 0; y < map.Height; y++)
            for (int x = 28; x < 32; x++)
                Assert.Equal(0, ShorelineLayout.BeachAmount(map, x + 0.5f, y + 0.5f));
        for (int y = 0; y < map.Height; y++)
            for (int x = 32; x < map.Width; x++) map.SetSeaWater(x, y, true);
        int inlandSand = 0;
        for (int y = 1; y < map.Height - 1; y++)
            if (ShorelineLayout.BeachAmount(map, 29.5f, y + 0.5f) > 0.99f)
            {
                Assert.Equal(0, ShorelineLayout.BankAmount(map, 29.5f, y + 0.5f));
                inlandSand++;
            }
        Assert.True(inlandSand > 0);
        Assert.Equal(0, ShorelineLayout.BankAmount(map, 30.5f, 20.5f));
        Assert.InRange(ShorelineLayout.BankAmount(map, 31.9f, 20.5f), 0.5f, 1);
    }

    [Fact]
    public void SeaClassificationSurvivesSavesAndDoesNotTurnRiversIntoSea()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        map.SetTerrain(5, 5, map.Content.Terrains.Get("Water"));
        map.SetSeaWater(5, 5, true);
        map.SetTerrain(6, 5, map.Content.Terrains.Get("Water"));
        var restored = TermCity.Core.Persistence.SaveGameStore.Deserialize(
            TermCity.Core.Persistence.SaveGameStore.Serialize(game));
        Assert.True(restored.Map.IsSea(5, 5));
        Assert.False(restored.Map.IsSea(6, 5));
        restored.Map.SetTerrain(5, 5, restored.Map.Content.Terrains.Base);
        Assert.False(restored.Map.IsSea(5, 5));
    }

    [Fact]
    public void GeneratedSeaClassificationUsesBodyKindNotWaterSizeOrRiverConnections()
    {
        int seas = 0, fresh = 0;
        for (int seed = 0; seed < 12; seed++)
        {
            var map = new GameMap(160, 96, new GameContent());
            var plan = TermCity.Core.Terrain.WaterGenerator.Build(map, map.Content.Terrains.Get("Water"), seed);
            var seaIds = plan.Bodies.Where(b => b.Kind == TermCity.Core.Terrain.WaterKind.Sea).Select(b => b.Id).ToHashSet();
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    Assert.Equal(seaIds.Contains(plan.IdAt(x, y)), map.IsSea(x, y));
                    if (map.IsSea(x, y)) seas++;
                    else if (!map.TerrainAt(x, y).Buildable) fresh++;
                }
        }
        Assert.True(seas > 0);
        Assert.True(fresh > 0);
    }

    [Fact]
    public void SavesWithoutSeaMetadataRemainValidAndDoNotGuessThatLakesAreOcean()
    {
        var game = TestCity.Flat();
        game.Map.SetTerrain(5, 5, game.Map.Content.Terrains.Get("Water"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(TermCity.Core.Persistence.SaveGameStore.Serialize(game))!;
        Assert.True(json.AsObject().Remove("Sea"));
        var restored = TermCity.Core.Persistence.SaveGameStore.Deserialize(json.ToJsonString());
        Assert.False(restored.Map.IsSea(5, 5));
        Assert.Equal(0, ShorelineLayout.BeachAmount(restored.Map, 4.5f, 5.5f));
    }
}
