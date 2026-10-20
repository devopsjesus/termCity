using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class PixelTerrainAndPickingTests
{
    [Fact]
    public void IsolatedCoastalHillCapsCollapseWithoutChangingLogicalTerrain()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 16; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        map.SetTerrain(15, 12, map.Content.Terrains.Get("Hill"));
        Assert.Equal(0, TerrainSprite.Elevation(map, 15, 12));
        Assert.Equal("Hill", map.TerrainAt(15, 12).Name);
        map.SetTerrain(14, 12, map.Content.Terrains.Get("Hill"));
        Assert.True(TerrainSprite.Elevation(map, 15, 12) > 0);
    }

    [Fact]
    public void SupportedCoastalTerracesRetainTheirDistinctLevelsAndLogicalTerrain()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 4; y < 28; y++)
            for (int x = 4; x < 16; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        for (int y = 0; y < map.Height; y++)
            for (int x = 16; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int column = 12; column < 16; column++)
        {
            float expected = TerrainSprite.HillBaseHeight +
                Math.Min(15 - column, 3) * TerrainSprite.HillStepHeight;
            Assert.Equal(expected, TerrainSprite.Elevation(map, column, 15), 5);
            Assert.Equal("Hill", map.TerrainAt(column, 15).Name);
        }
    }

    [Fact]
    public void ShorelineWaterColorCannotSpillOntoRaisedHillTops()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        map.SetTerrain(10, 10, map.Content.Terrains.Get("Hill"));
        var sprite = TerrainSprite.Ground(map, 10, 10, hillEdges: false);
        Assert.All(Enumerable.Range(0, sprite.Width * sprite.Height), i =>
            Assert.True(sprite.Pixels[i * 4 + 1] > sprite.Pixels[i * 4 + 2],
                "A raised hill's surface must remain grass, not blue water at hill height."));
    }

    [Fact]
    public void OpenWaterCornersRemainContinuousAcrossLogicalCellBoundaries()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        for (int y = 1; y < 31; y++)
            for (int x = 1; x < 31; x++)
                Assert.Equal(-0.16f, TerrainSprite.WaterCornerLevel(map, x, y), 5);
    }

    [Fact]
    public void PerspectiveRangeOffsetsRemainCircularWithoutTerminalAspectScaling()
    {
        for (int n = 0; n < 64; n++)
        {
            var point = AreaOfEffect.GroundOffset(27, n * MathF.Tau / 64);
            Assert.Equal(27, Math.Sqrt(point.X * point.X + point.Y * point.Y), 4);
        }
        var north = AreaOfEffect.GroundOffset(27, MathF.PI / 2);
        Assert.Equal(0, north.X, 4);
        Assert.Equal(27, north.Y, 4);
    }

    [Fact]
    public void SmoothedWaterMaskKeepsThinRiversAndJoinsAcrossCellEdges()
    {
        var map = new GameMap(32, 32, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int x = 0; x < map.Width; x++) map.SetTerrain(x, 15, water);
        Assert.True(TerrainSprite.WaterSurface(map, 15.5, 15.5) > 0.5);
        Assert.Equal(TerrainSprite.WaterSurface(map, 16 - 0.000001, 15.5),
            TerrainSprite.WaterSurface(map, 16 + 0.000001, 15.5), 5);
        Assert.True(TerrainSprite.WaterSurface(map, 15.5, 14.5) < 0.34);
        map.SetTerrain(10, 5, water);
        Assert.True(TerrainSprite.WaterSurface(map, 10.5, 5.5) >= 0.4);
        var isolated = TerrainSprite.Ground(map, 10, 5);
        Assert.Contains(Enumerable.Range(0, isolated.Width * isolated.Height),
            i => isolated.Pixels[i * 4 + 2] > isolated.Pixels[i * 4]);
        Assert.Equal("Water", map.TerrainAt(10, 5).Name);
        Assert.False(map.TerrainAt(10, 5).Buildable);
    }

    [Fact]
    public void WideRiverBankUsesBroadSmoothingInsteadOfCenterWeightedCellBulges()
    {
        var map = new GameMap(32, 32, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, water);
        Assert.Equal(11 / 16d, TerrainSprite.WaterSurface(map, 10.5, 15.5), 8);
        Assert.Equal(5 / 16d, TerrainSprite.WaterSurface(map, 10.5, 16.5), 8);
        Assert.Equal(0.5, TerrainSprite.WaterSurface(map, 10.5, 16), 8);
        Assert.Equal(TerrainSprite.WaterSurface(map, 15.999999, 16),
            TerrainSprite.WaterSurface(map, 16.000001, 16), 8);
        map.SetTerrain(10, 15, map.Content.Terrains.Get("Meadow"));
        Assert.True(TerrainSprite.WaterSurface(map, 10.5, 15.5) < 11 / 16d);
    }

    [Fact]
    public void MeadowUsesVisibleCoarseBlocksWithoutBrightPixelFlecks()
    {
        var map = new GameMap(32, 32, new GameContent());
        var chunk = TerrainSprite.Chunk(map, 0, 0, 1, 0);
        var colors = new HashSet<(byte R, byte G, byte B)>();
        for (int y = 0; y < chunk.Height; y++)
            for (int x = 0; x < chunk.Width; x++)
            {
                int pixel = (y * chunk.Width + x) * 4;
                Assert.InRange(chunk.Pixels[pixel + 1], (byte)0x5c, (byte)0x78);
                Assert.True((chunk.Pixels[pixel + 1] - chunk.Pixels[pixel + 2]) /
                    (double)chunk.Pixels[pixel + 1] >= 0.45, "Meadow greens must not become gray/desaturated.");
                colors.Add((chunk.Pixels[pixel], chunk.Pixels[pixel + 1], chunk.Pixels[pixel + 2]));
                if (x % 4 != 0)
                    Assert.Equal(chunk.Pixels[pixel + 1], chunk.Pixels[pixel - 3]);
                if (y % 8 != 0)
                    Assert.Equal(chunk.Pixels[pixel + 1], chunk.Pixels[pixel - chunk.Width * 4 + 1]);
            }
        Assert.InRange(colors.Count, 3, 5);
    }

    [Fact]
    public void OpenWaterUsesCoarseBlocksInsteadOfOnePixelWaveLines()
    {
        var map = new GameMap(32, 32, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, water);
        var chunk = TerrainSprite.Chunk(map, 0, 0, 1, 0);
        var colors = new HashSet<byte>();
        for (int y = 0; y < chunk.Height; y++)
            for (int x = 0; x < chunk.Width; x++)
            {
                int i = (y * chunk.Width + x) * 4;
                colors.Add(chunk.Pixels[i + 2]);
                Assert.True((chunk.Pixels[i + 2] - chunk.Pixels[i]) /
                    (double)chunk.Pixels[i + 2] >= 0.6, "Open water must retain a clear blue/teal color.");
                if (x % 4 != 0) Assert.Equal(chunk.Pixels[i + 2], chunk.Pixels[i - 2]);
                if (y % 8 != 0) Assert.Equal(chunk.Pixels[i + 2], chunk.Pixels[i - chunk.Width * 4 + 2]);
            }
        Assert.Equal(5, colors.Count);
    }

    [Theory]
    [InlineData(0x365f3f)]
    [InlineData(0x4e7646)]
    [InlineData(0x73854e)]
    public void FoliageUsesDeterministicFourPixelLeafClusters(int rgb)
    {
        var pixels = TerrainSprite.FoliageTexture(rgb);
        Assert.Equal(pixels, TerrainSprite.FoliageTexture(rgb));
        var colors = new HashSet<byte>();
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                colors.Add(pixels[i + 1]);
                Assert.Equal(255, pixels[i + 3]);
                if (x % 4 != 0) Assert.Equal(pixels[i + 1], pixels[i - 3]);
                if (y % 4 != 0) Assert.Equal(pixels[i + 1], pixels[i - 16 * 4 + 1]);
            }
        Assert.InRange(colors.Count, 3, 5);
    }
    [Theory]
    [InlineData("Meadow", 0)]
    [InlineData("Hill", 0.3)]
    [InlineData("Water", -0.04)]
    public void SubtleTerrainElevationIsVisualOnly(string name, double expected)
    {
        var map = new GameMap(32, 32, new GameContent());
        var terrain = map.Content.Terrains.Get(name);
        map.SetTerrain(10, 10, terrain);
        int revision = map.TerrainVersion;
        Assert.Equal(expected, TerrainSprite.Elevation(map, 10, 10), 5);
        Assert.Same(terrain, map.TerrainAt(10, 10));
        Assert.Equal(revision, map.TerrainVersion);
        Assert.Equal(name != "Water", terrain.Buildable);
    }

    [Fact]
    public void BroadHillsHaveFourTerracesAndTerrainEditsInvalidateTheirHeights()
    {
        var game = TestCity.Flat();
        var map = game.Map;
        var hill = map.Content.Terrains.Get("Hill");
        for (int y = 5; y < 16; y++)
            for (int x = 5; x < 16; x++) map.SetTerrain(x, y, hill);
        Assert.Equal(new[] { 1, 2, 3, 4 },
            Enumerable.Range(5, 11).Select(x => TerrainSprite.HillLevel(map, x, 10)).Distinct().Order());
        Assert.Equal(1.35, TerrainSprite.Elevation(map, 10, 10), 5);
        Assert.Equal(TerrainSprite.Ground(TestCity.Flat().Map, 10, 10).Pixels,
            TerrainSprite.Ground(map, 10, 10).Pixels);
        map.SetTerrain(10, 9, map.Content.Terrains.Get("Meadow"));
        Assert.Equal(1, TerrainSprite.HillLevel(map, 10, 10));
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        for (int y = 5; y < 16; y++)
            for (int x = 5; x < 16; x++)
                Assert.Equal(TerrainSprite.Elevation(map, x, y), TerrainSprite.Elevation(loaded.Map, x, y));
    }

    [Fact]
    public void ShoreSmoothingCannotPullHillCornersDownToWaterLevel()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        map.SetTerrain(16, 13, map.Content.Terrains.Get("Hill"));
        Assert.True(TerrainSprite.WaterSurface(map, 16, 13) > 0);
        Assert.False(TerrainSprite.TryShoreCornerHeight(map, 16, 13, out _));
        Assert.False(TerrainSprite.TryShoreCornerHeight(map, 17, 13, out _));
        Assert.True(TerrainSprite.TryShoreCornerHeight(map, 10, 12, out float shore));
        Assert.InRange(shore, -0.16f, 0);
        Assert.Equal(TerrainSprite.HillBaseHeight, TerrainSprite.Elevation(map, 16, 13));
        map.SetTerrain(16, 13, map.Content.Terrains.Get("Meadow"));
        Assert.True(TerrainSprite.TryShoreCornerHeight(map, 16, 13, out _));
    }

    [Fact]
    public void PackedTerrainBlocksExactlyMatchPixelSamplingOnLandAndOpenWater()
    {
        var map = new GameMap(32, 32, new GameContent());
        foreach (string name in new[] { "Meadow", "Water" })
        {
            var terrain = map.Content.Terrains.Get(name);
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++) map.SetTerrain(x, y, terrain);
            for (int y = 8; y < 12; y++)
                for (int x = 8; x < 12; x++)
                    Assert.Equal(TerrainSprite.Ground(map, x, y, 2).Pixels,
                        TerrainSprite.Ground(map, x, y, 2, hillEdges: false).Pixels);
        }
    }

    [Fact]
    public void RoadCornersAverageTerraceHeightsToMakeContinuousRamps()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 5; y < 16; y++)
            for (int x = 5; x < 16; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        for (int x = 3; x < 18; x++) map.SetRoad(x, 10, true);
        for (int x = 4; x < 18; x++)
        {
            Assert.True(TerrainSprite.TryRoadCornerHeight(map, x, 10, out float north));
            Assert.True(TerrainSprite.TryRoadCornerHeight(map, x, 11, out float south));
            Assert.Equal(north, south);
            Assert.Equal((TerrainSprite.Elevation(map, x - 1, 10) +
                TerrainSprite.Elevation(map, x, 10)) / 2, north);
        }
        Assert.False(TerrainSprite.TryRoadCornerHeight(map, 22, 22, out _));
        Assert.False(TerrainSprite.TryRoadCornerHeight(map, 6, 9, out _));
        Assert.False(TerrainSprite.TryRoadCornerHeight(map, 6, 7, out _));
        map.SetTerrain(4, 10, map.Content.Terrains.Get("Water"));
        Assert.True(TerrainSprite.TryRoadCornerHeight(map, 5, 10, out float bank));
        Assert.Equal(TerrainSprite.Elevation(map, 5, 10), bank);
    }

    [Fact]
    public void RoadCuttingLowersAdjacentHillShouldersWithoutFlatteningDistantTerraces()
    {
        var map = new GameMap(32, 32, new GameContent());
        for (int y = 6; y < 18; y++)
            for (int x = 5; x < 20; x++) map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        for (int x = 3; x < 22; x++) map.SetRoad(x, 5, true);
        float hill = TerrainSprite.Elevation(map, 10, 6);
        Assert.True(hill > 0);
        Assert.True(TerrainSprite.TryRoadCornerHeight(map, 10, 6, out float cut));
        Assert.Equal(0, cut);
        Assert.False(TerrainSprite.TryRoadCornerHeight(map, 10, 7, out _));
        Assert.Equal(hill, TerrainSprite.Elevation(map, 10, 6));
        Assert.Equal(TerrainSprite.MaximumHillHeight, TerrainSprite.Elevation(map, 10, 11));
    }

    [Fact]
    public void GeneratedHillRegionsIncludeMultipleHeightLevels()
    {
        var game = CityGame.New(new GameConfig { Seed = 42 });
        var levels = Enumerable.Range(0, game.Map.Width * game.Map.Height)
            .Select(game.Map.PosOf).Select(p => TerrainSprite.HillLevel(game.Map, p.X, p.Y))
            .Where(level => level > 0).Distinct().Order().ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4 }, levels);
    }

    [Theory]
    [InlineData("Meadow")]
    [InlineData("Hill")]
    [InlineData("Water")]
    public void GroundIsOpaqueShadedPixelArtAndDoesNotChangeGameplay(string name)
    {
        var game = TestCity.Flat(config: new GameConfig { MapWidth = 80, MapHeight = 48 });
        game.Map.SetTerrain(10, 10, game.Map.Content.Terrains.Get(name));
        string before = SaveGameStore.Serialize(game);
        var sprite = TerrainSprite.Ground(game.Map, 10, 10);
        Assert.Equal(12, sprite.Width);
        Assert.Equal(22, sprite.Height);
        Assert.All(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => Assert.Equal(255, alpha));
        Assert.True(Enumerable.Range(0, sprite.Width * sprite.Height)
            .Select(i => (sprite.Pixels[i * 4], sprite.Pixels[i * 4 + 1], sprite.Pixels[i * 4 + 2]))
            .Distinct().Count() >= (name == "Meadow" ? 2 : 4));
        Assert.Equal(sprite.Pixels, TerrainSprite.Ground(game.Map, 10, 10).Pixels);
        Assert.Equal(before, SaveGameStore.Serialize(game));
    }

    [Fact]
    public void ShorelinesAndRaisedHillEdgesFollowNeighboringTerrain()
    {
        var map = new GameMap(32, 32, new GameContent());
        var water = map.Content.Terrains.Get("Water");
        var hill = map.Content.Terrains.Get("Hill");
        map.SetTerrain(10, 10, water);
        var shore = TerrainSprite.Ground(map, 10, 10);
        map.SetTerrain(9, 10, water);
        var innerWater = TerrainSprite.Ground(map, 10, 10);
        int westMidpoint = shore.Height / 2 * shore.Width * 4;
        Assert.NotEqual(shore.Pixels.Skip(westMidpoint).Take(4), innerWater.Pixels.Skip(westMidpoint).Take(4));
        map.SetTerrain(10, 10, hill);
        var slope = TerrainSprite.Ground(map, 10, 10);
        map.SetTerrain(10, 11, hill);
        var plateau = TerrainSprite.Ground(map, 10, 10);
        int bottom = ((slope.Height - 1) * slope.Width + slope.Width / 2) * 4;
        Assert.NotEqual(slope.Pixels.Skip(bottom).Take(4), plateau.Pixels.Skip(bottom).Take(4));
    }

    [Fact]
    public void OnlyWaterChangesItsTextureAcrossAnimationPhases()
    {
        var map = new GameMap(32, 32, new GameContent());
        Assert.Equal(TerrainSprite.Ground(map, 10, 10, 0).Pixels, TerrainSprite.Ground(map, 10, 10, 1).Pixels);
        map.SetTerrain(10, 10, map.Content.Terrains.Get("Water"));
        Assert.NotEqual(TerrainSprite.Ground(map, 10, 10, 0).Pixels, TerrainSprite.Ground(map, 10, 10, 1).Pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TerrainChunksUseWorldAnchoredPixelsAtEveryZoom(int stride)
    {
        var map = new GameMap(80, 48, new GameContent());
        map.SetTerrain(stride, stride, map.Content.Terrains.Get("Water"));
        var chunk = TerrainSprite.Chunk(map, 0, 0, stride, 2);
        Assert.Equal(16 * 12, chunk.Width);
        Assert.Equal(16 * 22, chunk.Height);
        Assert.True(chunk.AnimatedWater);
        var tile = TerrainSprite.Ground(map, stride, stride, 2);
        int offset = (22 * chunk.Width + 12) * 4;
        Assert.Equal(tile.Pixels.Take(12 * 4), chunk.Pixels.Skip(offset).Take(12 * 4));
    }

    [Theory]
    [InlineData("Tree")]
    [InlineData("Rock")]
    public void NaturalObjectsHaveRaisedShadedSilhouettes(string name)
    {
        var feature = new GameContent().Features.Get(name);
        var sprite = TerrainSprite.Feature(feature, 0);
        Assert.True(sprite.Lift > 0);
        Assert.Contains(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha == 0);
        Assert.Contains(sprite.Pixels.Where((_, i) => i % 4 == 3), alpha => alpha == 255);
    }

    [Fact]
    public void RockVariantsHaveSixDistinctSilhouettesAndStonePalettes()
    {
        var feature = new GameContent().Features.Get("Rock");
        var sprites = Enumerable.Range(0, 6).Select(variant => TerrainSprite.Feature(feature, variant)).ToArray();
        Assert.Equal(6, sprites.Select(sprite => Convert.ToHexString(sprite.Pixels
            .Where((_, i) => i % 4 == 3).ToArray())).Distinct().Count());
        Assert.Equal(6, Enumerable.Range(0, 6).Select(TerrainSprite.RockColor).Distinct().Count());
        for (int variant = 0; variant < 6; variant++)
            Assert.Equal(sprites[variant].Pixels, TerrainSprite.Feature(feature, variant).Pixels);
    }

    [Fact]
    public void PerspectiveHillTopsOmitLegacyTanEdgeStrokes()
    {
        var map = new GameMap(32, 32, new GameContent());
        var meadow = TerrainSprite.Ground(map, 10, 10);
        map.SetTerrain(10, 10, map.Content.Terrains.Get("Hill"));
        var top = TerrainSprite.Ground(map, 10, 10, hillEdges: false);
        Assert.All(Enumerable.Range(0, top.Width * top.Height), i =>
        {
            Assert.True(top.Pixels[i * 4 + 1] > top.Pixels[i * 4]);
            Assert.True(top.Pixels[i * 4 + 1] >= meadow.Pixels[i * 4 + 1]);
        });
        Assert.NotEqual(meadow.Pixels, TerrainSprite.Ground(map, 10, 10).Pixels);
    }

    [Fact]
    public void HigherTerracesCastBroadGreenShadowsOnTheirLowerNeighbors()
    {
        var map = new GameMap(32, 32, new GameContent());
        var original = TerrainSprite.Ground(map, 10, 10, hillEdges: false);
        var hill = map.Content.Terrains.Get("Hill");
        for (int y = 6; y <= 9; y++)
            for (int x = 6; x <= 14; x++) map.SetTerrain(x, y, hill);
        var shaded = TerrainSprite.Ground(map, 10, 10, hillEdges: false);
        int near = (2 * shaded.Width + 6) * 4;
        int far = (18 * shaded.Width + 6) * 4;
        Assert.True(shaded.Pixels[near + 1] <= original.Pixels[near + 1] * 0.94);
        Assert.Equal(original.Pixels[far + 1], shaded.Pixels[far + 1]);
        Assert.True(shaded.Pixels[near + 1] > shaded.Pixels[near]);
        Assert.Equal(shaded.Pixels, TerrainSprite.Ground(map, 10, 10, hillEdges: false).Pixels);
        Assert.Equal(0, TerrainSprite.Elevation(map, 10, 10));
        map.SetTerrain(10, 9, map.Content.Terrains.Get("Meadow"));
        Assert.Equal(original.Pixels, TerrainSprite.Ground(map, 10, 10, hillEdges: false).Pixels);
    }

    [Fact]
    public void EveryOpaqueBuildingPixelIsClickableAcrossTheEntireRender()
    {
        foreach (var type in new GameContent().Buildings)
        {
            var area = new CellRect(10, 10, type.Width, type.Height);
            var sprite = BuildingSprite.Render(type, area.Width, area.Height);
            int hits = 0;
            for (int y = 0; y < sprite.Height; y++)
                for (int x = 0; x < sprite.Width; x++)
                {
                    double worldX = area.X * 12 + x + 0.5, worldY = area.Y * 22 - sprite.Lift + y + 0.5;
                    var hit = sprite.Pick(area, worldX, worldY);
                    if (sprite.Pixels[(y * sprite.Width + x) * 4 + 3] >= 128)
                    {
                        Assert.Equal(new Pos(area.X, area.Y), hit);
                        hits++;
                    }
                    else Assert.Null(hit);
                }
            Assert.True(hits > 0, type.Name);
            Assert.Null(sprite.Pick(area, double.NaN, 0));
        }
    }

    [Fact]
    public void ObliqueBuildingArtworkHasDistinctLayoutsAndStableVariations()
    {
        var content = new GameContent();
        var signatures = new HashSet<string>();
        foreach (var type in content.Buildings)
        {
            var sprite = BuildingSprite.Render(type, type.Width, type.Height);
            Assert.Equal(8, sprite.Lift);
            Assert.Equal(sprite.Pixels, BuildingSprite.Render(type, type.Width, type.Height).Pixels);
            signatures.Add(Convert.ToBase64String(sprite.Pixels));
        }
        Assert.True(signatures.Count >= content.Buildings.Count - 3,
            "Building types should use distinct roof, courtyard, utility and workshop layouts.");
        var cottage = content.Buildings.Get("Burgage House");
        Assert.NotEqual(BuildingSprite.Render(cottage, 2, 2, 0).Pixels, BuildingSprite.Render(cottage, 2, 2, 1).Pixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingSprite.Render(cottage, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildingSprite.Render(cottage, 2, 0));
    }
}
