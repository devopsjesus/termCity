using System.Buffers.Binary;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.GodotApp;

namespace TermCity.Tests;

public class PlacementClickTests
{
    private static short Sample(byte[] pcm, int i) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));

    [Fact]
    public void ClickClackIsShortAudibleAndReproducible()
    {
        for (int v = 0; v < PlacementClick.Variants; v++)
        {
            var pcm = PlacementClick.Render(v);
            Assert.Equal(pcm, PlacementClick.Render(v));
            Assert.InRange(pcm.Length / (2.0 * PlacementClick.SampleRate), 0.08, 0.2);
            int frames = pcm.Length / 2;
            int peak = Enumerable.Range(0, frames).Max(i => Math.Abs((int)Sample(pcm, i)));
            Assert.InRange(peak, 4000, short.MaxValue);
            Assert.True(Math.Abs((int)Sample(pcm, frames - 1)) < 200, "the sound must fade out without a pop");
        }

        Assert.NotEqual(PlacementClick.Render(0), PlacementClick.Render(1));
    }

    [Fact]
    public void ClickClackHasTwoSeparatedStrikes()
    {
        var pcm = PlacementClick.Render(0);
        int frames = pcm.Length / 2, window = PlacementClick.SampleRate / 200;
        var energy = new List<double>();
        for (int i = 0; i + window <= frames; i += window)
        {
            energy.Add(Enumerable.Range(i, window).Sum(k => Math.Abs((double)Sample(pcm, k))) / window);
        }

        int strikes = 0;
        for (int i = 0; i < energy.Count; i++)
        {
            double before = i == 0 ? 0 : energy[i - 1];
            if (energy[i] > 1200 && before < energy[i] * 0.5) strikes++;
        }

        Assert.True(strikes >= 2, $"expected a click and a clack, found {strikes}");
    }

    [Fact]
    public void SessionAnnouncesSuccessfulPlacementsOnly()
    {
        var session = new GameSession(TestCity.Flat(), Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N") + ".json"));
        session.SetViewport(80, 24);
        int placed = 0;
        session.Placed += () => placed++;
        session.BeginDrag(new Pos(10, 10));
        session.UpdateDrag(new Pos(11, 11));
        session.EndSelection();
        Assert.True(session.Zone(ZoneType.Residential).Success);
        Assert.Equal(1, placed);
        session.SelectCell(new Pos(-5, -5));
        session.Demolish();
        Assert.True(placed <= 2);
    }
}
