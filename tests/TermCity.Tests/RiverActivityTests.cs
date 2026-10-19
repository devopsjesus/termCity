using TermCity.Core.Rendering;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class RiverActivityTests
{
    [Fact]
    public void RiverActivityIsSparseVariedAndDeterministic()
    {
        int fish = 0, splashes = 0, samples = 0;
        var starts = new HashSet<int>();
        for (int site = 0; site < 32; site++)
        {
            var cell = new Pos(site * 8, 17);
            bool active = false;
            for (int tick = 0; tick < 1280; tick++)
            {
                double clock = tick / 10.0;
                var activity = RiverActivityLayout.At(cell, clock);
                Assert.Equal(activity, RiverActivityLayout.At(cell, clock));
                if (activity is { } current)
                {
                    samples++;
                    Assert.InRange(current.Progress, 0, 1);
                    Assert.InRange(current.Position.X, cell.X + 0.35f, cell.X + 0.65f);
                    Assert.InRange(current.Position.Y, cell.Y + 0.35f, cell.Y + 0.65f);
                    if (!active)
                    {
                        starts.Add(tick % 320);
                        if (current.Fish) fish++; else splashes++;
                    }
                }
                active = activity is not null;
            }
        }
        Assert.True(fish > 10 && splashes > fish);
        Assert.True(starts.Count > 30);
        Assert.InRange(samples / (32 * 1280.0), 0.01, 0.05);
    }
}
