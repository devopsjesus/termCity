using System.Numerics;
using TermCity.Core.Rendering;

namespace TermCity.Tests;

public class MinimapProjectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void RotatedNonSquareMapRoundTripsAndFillsSquare(int turns)
    {
        var projection = new MinimapProjection(320, 192, turns);
        foreach (var point in new[] { Vector2.Zero, new(320, 0), new(320, 192), new(0, 192), new(83.5f, 151.5f) })
        {
            var fraction = projection.Fraction(point);
            Assert.InRange(fraction.X, -0.00001f, 1.00001f);
            Assert.InRange(fraction.Y, -0.00001f, 1.00001f);
            Assert.InRange(Vector2.Distance(point, projection.MapPoint(fraction)), 0, 0.0001f);
        }
        var corners = new[] { Vector2.Zero, new Vector2(320, 0), new Vector2(320, 192), new Vector2(0, 192) }
            .Select(projection.Fraction).ToHashSet();
        Assert.Equal(4, corners.Count);
        foreach (var corner in new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY }) Assert.Contains(corner, corners);
    }

    [Fact]
    public void QuarterTurnRotatesBothAxesWithoutChangingSquareBounds()
    {
        var original = new MinimapProjection(320, 192, 0);
        var rotated = new MinimapProjection(320, 192, 1);
        var origin = rotated.Fraction(new(100, 100));
        Assert.True(rotated.Fraction(new(110, 100)).Y > origin.Y);
        Assert.True(rotated.Fraction(new(100, 110)).X < origin.X);
        Assert.Equal(Vector2.Zero, original.MapPoint(Vector2.Zero));
    }

    [Fact]
    public void FourQuarterTurnsRestoreOriginalCoordinates()
    {
        var first = new MinimapProjection(320, 192, 0);
        var final = new MinimapProjection(320, 192, 4);
        Assert.InRange(Vector2.Distance(first.Fraction(new(83, 151)), final.Fraction(new(83, 151))), 0, 0.00001f);
    }

    [Fact]
    public void ViewOutlineEdgesClipWithoutChangingTheirAngle()
    {
        var clipped = MinimapProjection.ClipSegment(new(-0.5f, 0), new(1.5f, 1))!.Value;
        Assert.Equal(new Vector2(0, 0.25f), clipped.Start);
        Assert.Equal(new Vector2(1, 0.75f), clipped.End);
        Assert.Null(MinimapProjection.ClipSegment(new(-1, 0), new(-1, 1)));
        Assert.Null(MinimapProjection.ClipSegment(new(0, 2), new(1, 2)));
        var reverse = MinimapProjection.ClipSegment(new(1.5f, 1), new(-0.5f, 0))!.Value;
        Assert.Equal(clipped.Start, reverse.End);
        Assert.Equal(clipped.End, reverse.Start);
    }
}
