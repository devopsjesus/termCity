using TermCity.Core.Rendering;
using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class FootpathArtworkTests
{
    [Theory]
    [InlineData(FootpathArtwork.FreshColor)]
    [InlineData(FootpathArtwork.WornColor)]
    public void DirtTextureHasStableOpaqueEarthClustersInsteadOfFlatColorOrSpeckles(int color)
    {
        var pixels = FootpathArtwork.MaterialTexture(color);
        Assert.Equal(pixels, FootpathArtwork.MaterialTexture(color));
        Assert.Equal(FootpathArtwork.TextureSize * FootpathArtwork.TextureSize * 4, pixels.Length);
        var tones = new HashSet<byte>();
        for (int y = 0; y < FootpathArtwork.TextureSize; y++)
            for (int x = 0; x < FootpathArtwork.TextureSize; x++)
            {
                int pixel = (y * FootpathArtwork.TextureSize + x) * 4;
                int cluster = (y / FootpathArtwork.ClusterSize * FootpathArtwork.ClusterSize *
                    FootpathArtwork.TextureSize + x / FootpathArtwork.ClusterSize * FootpathArtwork.ClusterSize) * 4;
                Assert.Equal(pixels.AsSpan(cluster, 4).ToArray(), pixels.AsSpan(pixel, 4).ToArray());
                Assert.Equal(255, pixels[pixel + 3]);
                Assert.True(pixels[pixel] > pixels[pixel + 1] && pixels[pixel + 1] > pixels[pixel + 2]);
                tones.Add(pixels[pixel]);
            }
        Assert.Equal(4, tones.Count);
        Assert.InRange(tones.Max() - tones.Min(), 10, 20);
        Assert.True(FootpathArtwork.ClusterSize >= 4);
    }

    [Fact]
    public void WearPalettesAreSharedAndInvalidMaterialColorsAreRejected()
    {
        Assert.Equal(FootpathArtwork.FreshColor, FootpathArtwork.ColorForWear(1));
        Assert.Equal(FootpathArtwork.WornColor, FootpathArtwork.ColorForWear(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => FootpathArtwork.MaterialTexture(0));
    }

    [Fact]
    public void ClustersRemainBroadInWorldSpaceAtOrdinaryZoom()
    {
        float width = FootpathArtwork.ClusterSize / (FootpathArtwork.TextureSize * FootpathArtwork.UvScale);
        Assert.InRange(width, .15f, .25f);
        Assert.InRange(1 / FootpathArtwork.UvScale, 10, 12);
    }

    [Theory]
    [InlineData(FootpathArtwork.FreshColor)]
    [InlineData(FootpathArtwork.WornColor)]
    public void DirtPatchesDoNotRepeatAsShortAxialOrDiagonalBands(int color)
    {
        var pixels = FootpathArtwork.MaterialTexture(color);
        int size = FootpathArtwork.TextureSize / FootpathArtwork.ClusterSize;
        double Tone(int x, int y) => pixels[((y % size * FootpathArtwork.ClusterSize) *
            FootpathArtwork.TextureSize + x % size * FootpathArtwork.ClusterSize) * 4];
        double mean = Enumerable.Range(0, size * size).Average(i => Tone(i % size, i / size));
        double variance = Enumerable.Range(0, size * size).Average(i => Math.Pow(Tone(i % size, i / size) - mean, 2));
        foreach (int offset in new[] { 8, 16 })
            foreach (var direction in new[] { (X: 1, Y: 0), (X: 0, Y: 1), (X: 1, Y: 1), (X: 1, Y: -1) })
            {
                double covariance = 0;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        covariance += (Tone(x, y) - mean) *
                            (Tone(x + direction.X * offset, y + size + direction.Y * offset) - mean);
                double correlation = covariance / (size * size * variance);
                Assert.InRange(correlation, -.12, .12);
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.08f)]
    [InlineData(.119f)]
    public void ShallowShouldersKeepTheFootprintAndExistingGradeBudget(float grade)
    {
        var layout = new FootpathSurfaceLayout([new FootpathLayout.Stroke([new(0, 0), new(1, 0)], 3)], null);
        float half = FootpathLayout.WidthForWear(3) / 2;
        var source = new[]
        {
            new FootpathReliefTriangle(new(0, 0, -half), new(1, grade, -half), new(1, grade, half), FootpathArtwork.WornColor),
            new FootpathReliefTriangle(new(0, 0, -half), new(1, grade, half), new(0, 0, half), FootpathArtwork.WornColor),
        };
        var faces = FootpathReliefMesh.Build(source, point => layout.BoundaryDistance(point, FootpathArtwork.ShoulderWidth));
        Assert.Equal(12, faces.Count);
        double area = 0;
        foreach (var face in faces)
        {
            area += Math.Abs((face.B.X - face.A.X) * (face.C.Z - face.A.Z) -
                (face.C.X - face.A.X) * (face.B.Z - face.A.Z)) / 2;
            Assert.InRange(Math.Max(face.A.Y, Math.Max(face.B.Y, face.C.Y)) -
                Math.Min(face.A.Y, Math.Min(face.B.Y, face.C.Y)), 0, TravelRoutes.MaximumFootpathStep);
            foreach (var point in new[] { face.A, face.B, face.C })
            {
                Assert.InRange(point.X, 0, 1);
                Assert.InRange(point.Z, -half, half);
                Assert.InRange(point.Y - point.X * grade,
                    FootpathArtwork.SurfaceLift - .000001f, FootpathArtwork.SurfaceLift + FootpathArtwork.ReliefHeight + .000001f);
                if (Math.Abs(Math.Abs(point.Z) - half) < .00001f)
                    Assert.InRange(point.Y - point.X * grade, FootpathArtwork.SurfaceLift - .000001f,
                        FootpathArtwork.SurfaceLift + .00001f);
            }
        }
        Assert.InRange(area, half * 2 - .00001, half * 2 + .00001);
        if (grade == 0) Assert.Contains(faces, face => face.A.Y > FootpathArtwork.SurfaceLift + FootpathArtwork.ReliefHeight * .8f);
        Assert.Equal(FootpathArtwork.ShoulderWidth, layout.BoundaryDistance(new(.5f, 0), FootpathArtwork.ShoulderWidth));
    }

    [Fact]
    public void ReliefNeverRestoresAnUnsupportedTerrainTriangle()
    {
        var faces = new[] { new FootpathReliefTriangle(Vector3.Zero, new(1, .2f, 0), new(0, 0, 1), FootpathArtwork.FreshColor) };
        Assert.Empty(FootpathReliefMesh.Build(faces, _ => FootpathArtwork.ShoulderWidth));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidBoundaryDistancesAreReported(float distance)
    {
        var faces = new[] { new FootpathReliefTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, FootpathArtwork.FreshColor) };
        Assert.Throws<InvalidOperationException>(() => FootpathReliefMesh.Build(faces, _ => distance));
    }
}
