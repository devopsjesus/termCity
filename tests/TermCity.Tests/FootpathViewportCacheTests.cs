using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class FootpathViewportCacheTests
{
    [Fact]
    public void BoundedSurfacesRetainExactVisiblePolygonsAndShoulderDistances()
    {
        var map = TestCity.Flat().Map;
        foreach (int road in map.RoadCells.ToArray()) map.SetRoad(road % map.Width, road / map.Width, false);
        for (int x = 5; x <= 35; x++)
        {
            map.SetPathWear(x, 10, 4);
            if (x > 5) map.LinkPathCells(map.Index(x - 1, 10), map.Index(x, 10));
        }
        for (int y = 11; y <= 30; y++)
        {
            map.SetPathWear(35, y, 4);
            map.LinkPathCells(map.Index(35, y - 1), map.Index(35, y));
        }
        var view = new CellRect(25, 7, 15, 12);
        var full = new FootpathSurfaceLayout(map, null);
        var bounded = new FootpathSurfaceLayout(map, null, view);
        Assert.NotEmpty(full.Surfaces);
        foreach (var cell in view.Cells())
        {
            var expected = full.AtCell(cell.X, cell.Y);
            var actual = bounded.AtCell(cell.X, cell.Y);
            Assert.Equal(expected.Count, actual.Count);
            for (int index = 0; index < expected.Count; index++) Assert.Equal(expected[index], actual[index]);
            foreach (float offset in new[] { .1f, .4f, .5f, .7f, .9f })
            {
                var point = new Vector2(cell.X + offset, cell.Y + .5f);
                Assert.InRange(Math.Abs(full.BoundaryDistance(point, .12f) -
                    bounded.BoundaryDistance(point, .12f)), 0, .000001f);
            }
        }
    }
}
