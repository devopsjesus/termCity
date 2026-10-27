using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Tests;

public class CompactBuildingArtworkTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void EveryHouseVariantFitsItsPlotWithReadableDoorsAndLevelIndependentExpansion(int width, int depth)
    {
        var silhouettes = new HashSet<HouseDimensions>();
        for (int level = 1; level <= 3; level++)
            for (int variant = 0; variant < 64; variant++)
            {
                var appearance = HouseAppearance.At(variant % 8, variant / 8, level);
                var dimensions = appearance.OnPlot(width, depth, level);
                Assert.InRange(dimensions.Width, width * 0.6f, width * 0.8f);
                Assert.InRange(dimensions.Depth, depth * 0.6f, depth * 0.8f);
                Assert.True(dimensions.Width + 0.08f < width);
                Assert.True(dimensions.Depth + 0.08f < depth);
                Assert.InRange(dimensions.Height + dimensions.RoofRise, 0.6f, 1.05f);
                Assert.Equal(appearance.Height, dimensions.Height);
                Assert.Equal(appearance.RoofRise, dimensions.RoofRise);
                Assert.Equal(level > 1 ? 2 : 1, dimensions.Stories);
                if (width == 1 || depth == 1) Assert.False(dimensions.Wing);
                if (dimensions.Wing)
                {
                    Assert.True(dimensions.Width * 0.58f + 0.04f < width / 2f);
                    Assert.True(dimensions.Depth * 0.58f + 0.04f < depth / 2f);
                }
                float floorHeight = dimensions.Height / dimensions.Stories;
                float windowHeight = Math.Min(0.13f, floorHeight * 0.39f);
                float windowBottom = floorHeight * 0.59f - windowHeight / 2 - 0.025f;
                float windowTop = floorHeight * 0.59f + (windowHeight + 0.028f) / 2;
                Assert.True(windowBottom > 0.071f);
                Assert.True(windowTop < floorHeight - 0.016f);
                Assert.True(floorHeight - 0.07f >= 0.24f);
                silhouettes.Add(dimensions);
            }
        Assert.True(silhouettes.Count >= 12);
    }

    [Theory]
    [InlineData(0.4464)]
    [InlineData(0.504)]
    [InlineData(0.64)]
    [InlineData(0.66)]
    [InlineData(0.74)]
    [InlineData(0.76)]
    [InlineData(1.28)]
    [InlineData(1.52)]
    public void CompactWindowsAndCentralDoorClearTheirTimberBays(double width)
    {
        var bays = BuildingDetailLayout.CottageFacade((float)width);
        Assert.Equal(1, bays.Count % 2);
        Assert.InRange(bays.Count, 1, 5);
        for (int bay = 0; bay < bays.Count; bay++)
        {
            float detailWidth = bay == bays.Count / 2 ? BuildingDetailLayout.CottageDoorWidth : 0.135f;
            Assert.True(bays.Center(bay) - detailWidth / 2 > bays.Post(bay) + BuildingDetailLayout.CottageBeamWidth / 2);
            Assert.True(bays.Center(bay) + detailWidth / 2 < bays.Post(bay + 1) - BuildingDetailLayout.CottageBeamWidth / 2);
        }
    }

    [Fact]
    public void WorkplaceFormsArePeriodSpecificVariedAndNeverGrowFactoryStacks()
    {
        var content = new GameContent();
        var forms = new HashSet<WorkplaceForm>();
        foreach (var type in content.Buildings.Where(type => type.Zone is ZoneType.Commercial or ZoneType.Industrial))
            foreach (var plot in new[] { (1, 1), (1, 2), (2, 1), (2, 2) })
                for (int variant = 0; variant < 36; variant++)
                {
                    var look = WorkplaceAppearance.At(type, variant % 6, variant / 6, plot.Item1, plot.Item2);
                    Assert.Equal(look, WorkplaceAppearance.At(type, variant % 6, variant / 6, plot.Item1, plot.Item2));
                    Assert.True(look.Width + 0.07f < plot.Item1);
                    Assert.True(look.Depth + 0.10f < plot.Item2);
                    Assert.InRange(look.Height + look.RoofRise, 0.4f, 0.84f);
                    Assert.True(HouseAppearance.IsTexturedColor(look.WallColor));
                    Assert.True(HouseAppearance.IsRoofColor(look.RoofColor));
                    forms.Add(look.Form);
                }
        Assert.Equal(Enum.GetValues<WorkplaceForm>().Order(), forms.Order());
    }

    [Fact]
    public void CompactZonedSpritesRemainDistinctStableAndEntirelyClickable()
    {
        var content = new GameContent();
        foreach (var plot in new[] { (1, 1), (1, 2), (2, 1), (2, 2) })
        {
            var signatures = new HashSet<string>();
            foreach (var type in content.Buildings.Where(type => type.Zone != ZoneType.None))
            {
                var area = new CellRect(10, 10, plot.Item1, plot.Item2);
                var variants = new HashSet<string>();
                for (int variant = 0; variant < 8; variant++)
                {
                    var sprite = BuildingSprite.Render(type, area.Width, area.Height, variant);
                    Assert.Equal(sprite.Pixels, BuildingSprite.Render(type, area.Width, area.Height, variant).Pixels);
                    Assert.Equal(type.Name is "Shanty Tower" or "Wall House" ? 18 : 8, sprite.Lift);
                    variants.Add(Convert.ToBase64String(sprite.Pixels));
                    if (variant == 0 && !type.SettlementOnly) signatures.Add(Convert.ToBase64String(sprite.Pixels));
                    int opaque = 0;
                    for (int y = 0; y < sprite.Height; y++)
                        for (int x = 0; x < sprite.Width; x++)
                            if (sprite.Pixels[(y * sprite.Width + x) * 4 + 3] >= 128)
                            {
                                Assert.Equal(new Pos(area.X, area.Y), sprite.Pick(area,
                                    area.X * RoadCurves.CellWidth + x + 0.5,
                                    area.Y * RoadCurves.CellHeight - sprite.Lift + y + 0.5));
                                opaque++;
                            }
                    Assert.True(opaque > 100, type.Name);
                }
                Assert.True(variants.Count >= 3, type.Name);
            }
            Assert.Equal(9, signatures.Count);
        }
    }

    [Fact]
    public void LegacyLargerPlotsExpandHorizontallyWithoutStretchingTheirHeights()
    {
        var appearance = HouseAppearance.At(10, 12, 3);
        var compact = appearance.OnPlot(1, 1, 3);
        var legacy = appearance.OnPlot(2, 4, 3);
        Assert.Equal(compact.Width * 2, legacy.Width);
        Assert.Equal(compact.Depth * 4, legacy.Depth);
        Assert.Equal(compact.Height, legacy.Height);
        Assert.Equal(compact.RoofRise, legacy.RoofRise);
        Assert.Throws<ArgumentOutOfRangeException>(() => appearance.OnPlot(0, 1, 1));
    }

    [Fact]
    public void HouseNeighborhoodsIncludeEveryHandcraftedMedievalStyle()
    {
        var styles = Enumerable.Range(0, 64)
            .Select(index => HouseAppearance.At(index % 8, index / 8, 1).Style)
            .ToHashSet();
        foreach (var type in new GameContent().Buildings.Where(type => type.Zone == ZoneType.Residential))
            styles.Add(HouseAppearance.At(type, 0, 0).Style);

        Assert.Equal(Enum.GetValues<HouseStyle>().Order(), styles.Order());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void HousesUseSolidBlocksWithCoarseRoofStepsAndNonoverlappingWings(int width, int depth)
    {
        for (int level = 1; level <= 3; level++)
            for (int variant = 0; variant < 64; variant++)
            {
                var appearance = HouseAppearance.At(variant % 8, variant / 8, level);
                var blocks = appearance.BlocksOnPlot(width, depth, level);
                Assert.Equal(blocks, appearance.BlocksOnPlot(width, depth, level));
                Assert.Equal(appearance.OnPlot(width, depth, level).Wing ? 2 : 1, blocks.Count);
                foreach (var block in blocks)
                {
                    Assert.True(Math.Abs(block.X) + block.Width / 2 + 0.04f < width / 2f);
                    Assert.True(Math.Abs(block.Z) + block.Depth / 2 + 0.04f < depth / 2f);
                    Assert.InRange(block.RoofSteps, 3, 4);
                    Assert.Equal(block.RoofRise, block.RoofSteps * HouseBlock.RoofStepHeight, 5);
                    Assert.InRange(block.Height, 0.44f, 0.72f);
                }
                if (blocks.Count == 2)
                {
                    var main = blocks[0];
                    var wing = blocks[1];
                    Assert.Equal(main.Z - main.Depth / 2, wing.Z + wing.Depth / 2, 5);
                    Assert.Equal(main.X - main.Width / 2, wing.X - wing.Width / 2, 5);
                    Assert.True(wing.Width < main.Width);
                    Assert.True(wing.Height <= main.Height);
                }
                else if (width != depth)
                    Assert.Equal(depth > width, blocks[0].CrosswiseRoof);
            }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void HalfTimberFramesHaveCleanDisjointJointsAndFlatPanels(int width, int depth)
    {
        for (int level = 1; level <= 3; level++)
            for (int variant = 0; variant < 24; variant++)
            {
                var appearance = HouseAppearance.At(variant % 6, variant / 6, level);
                foreach (var block in appearance.BlocksOnPlot(width, depth, level))
                {
                    var parts = appearance.Parts(block);
                    Assert.Equal(parts, appearance.Parts(block));
                    Assert.All(parts, part =>
                    {
                        Assert.True(part.Size.X > 0 && part.Size.Y > 0 && part.Size.Z > 0);
                        Assert.False(HouseAppearance.IsTexturedColor(part.Color));
                        Assert.True(part.Center.Y - part.Size.Y / 2 >= -0.00001f);
                    });
                    var walls = parts.Where(part => !part.Roof).ToArray();
                    var corners = walls.Where(part => part.Color == HouseAppearance.FrameColor &&
                        Math.Abs(part.Size.Y - block.Height) < 0.001f).ToArray();
                    Assert.Equal(4, corners.Length);
                    Assert.All(corners, part =>
                    {
                        Assert.Equal(BuildingDetailLayout.HalfTimberPostWidth, part.Size.X);
                        Assert.Equal(BuildingDetailLayout.HalfTimberPostWidth, part.Size.Z);
                        Assert.Equal((block.Width - part.Size.X) / 2, Math.Abs(part.Center.X), 5);
                        Assert.Equal((block.Depth - part.Size.Z) / 2, Math.Abs(part.Center.Z), 5);
                    });
                    Assert.All(walls, part =>
                    {
                        Assert.True(Math.Abs(part.Center.X) + part.Size.X / 2 <= block.Width / 2 + 0.00001f);
                        Assert.True(Math.Abs(part.Center.Z) + part.Size.Z / 2 <= block.Depth / 2 + 0.00001f);
                        Assert.True(part.Center.Y + part.Size.Y / 2 <= block.Height + 0.00001f);
                    });
                    for (int a = 0; a < walls.Length; a++)
                        for (int b = a + 1; b < walls.Length; b++)
                        {
                            var first = walls[a]; var second = walls[b];
                            var overlap = System.Numerics.Vector3.Min(first.Center + first.Size / 2,
                                second.Center + second.Size / 2) -
                                System.Numerics.Vector3.Max(first.Center - first.Size / 2, second.Center - second.Size / 2);
                            Assert.True(overlap.X <= 0.00001f || overlap.Y <= 0.00001f || overlap.Z <= 0.00001f,
                                "Frame joints and plaster panels must meet without intersecting duplicate geometry.");
                        }
                    var plaster = walls.Where(part => part.Color == appearance.PlasterColor).ToArray();
                    int panels = (BuildingDetailLayout.HalfTimberFacade(block.Width).Count +
                        BuildingDetailLayout.HalfTimberFacade(block.Depth).Count) * 2 * block.Stories;
                    Assert.Equal(panels, plaster.Length);
                    for (int floor = 0; floor < block.Stories; floor++)
                    {
                        var panel = block.WallPanel(floor);
                        Assert.InRange(panel.Height, 0.199f, 0.321f);
                    }
                    for (int step = 0; step < block.RoofSteps; step++)
                    {
                        float height = block.Height + (step + 0.5f) * HouseBlock.RoofStepHeight;
                        var course = parts.Where(part => part.Roof && Math.Abs(part.Center.Y - height) < 0.001f).ToArray();
                        float fraction = (block.RoofSteps - step) / (float)block.RoofSteps;
                        float roofArea = (block.Width + 0.08f) * (block.Depth + 0.08f) * fraction;
                        Assert.Equal(roofArea, course.Sum(part => part.Size.X * part.Size.Z), 5);
                        Assert.All(course, part =>
                        {
                            Assert.Equal(HouseBlock.RoofStepHeight, part.Size.Y);
                            Assert.InRange(part.Size.X, 0.06f, HouseAppearance.VoxelSpan + 0.001f);
                            Assert.InRange(part.Size.Z, 0.06f, HouseAppearance.VoxelSpan + 0.001f);
                        });
                        Assert.True(course.Length > 1);
                    }
                }
            }
    }

    [Fact]
    public void HalfTimberHousesUseOneCleanPlasterColorAndOneDarkFrameColor()
    {
        var colors = new HashSet<int>();
        for (int variant = 0; variant < 64; variant++)
        {
            var appearance = HouseAppearance.At(variant % 8, variant / 8, 1);
            colors.Add(appearance.PlasterColor);
            var block = appearance.BlocksOnPlot(1, 1, 1).Single();
            Assert.All(appearance.Parts(block).Where(part => !part.Roof), part =>
                Assert.True(part.Color == appearance.PlasterColor || part.Color == HouseAppearance.FrameColor));
        }
        Assert.Equal(4, colors.Count);
        var look = HouseAppearance.At(0, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => look.Parts(default));
    }

    [Theory]
    [InlineData(0.60f)]
    [InlineData(0.64f)]
    [InlineData(0.72f)]
    [InlineData(1.28f)]
    [InlineData(1.44f)]
    public void HalfTimberOpeningsFitBetweenTheThickPosts(float width)
    {
        var bays = BuildingDetailLayout.HalfTimberFacade(width);
        Assert.Equal(1, bays.Count % 2);
        for (int bay = 0; bay < bays.Count; bay++)
        {
            float clearHalfWidth = (bays.Spacing - BuildingDetailLayout.HalfTimberPostWidth) / 2;
            Assert.True(clearHalfWidth > 0.09f);
            Assert.True(bays.Center(bay) - 0.09f > bays.Post(bay) + BuildingDetailLayout.HalfTimberPostWidth / 2);
            Assert.True(bays.Center(bay) + 0.09f < bays.Post(bay + 1) - BuildingDetailLayout.HalfTimberPostWidth / 2);
        }
    }
}
