using TermCity.Core.Terrain;

namespace TermCity.Tests;

public class RiverContourTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.125)]
    [InlineData(0.8)]
    [InlineData(1)]
    public void MeanderTransitionsHaveNoAbruptChangeInBankDirection(double progress)
    {
        const double delta = 0.00001;
        double center = WaterGenerator.RiverMeanderEnvelope(progress);
        double left = (center - WaterGenerator.RiverMeanderEnvelope(progress - delta)) / delta;
        double right = (WaterGenerator.RiverMeanderEnvelope(progress + delta) - center) / delta;
        Assert.InRange(Math.Abs(left - right), 0, 0.001);
        Assert.InRange(Math.Abs(left), 0, 0.001);
        Assert.InRange(Math.Abs(right), 0, 0.001);
    }

    [Fact]
    public void RiverMeanderEnvelopeKeepsTheChannelOpenAndEndpointsAnchored()
    {
        Assert.Equal(0, WaterGenerator.RiverMeanderEnvelope(0));
        Assert.Equal(0, WaterGenerator.RiverMeanderEnvelope(1));
        Assert.Equal(1, WaterGenerator.RiverMeanderEnvelope(0.5));
        for (int i = 0; i <= 1000; i++)
            Assert.InRange(WaterGenerator.RiverMeanderEnvelope(i / 1000d), 0, 1);
    }
}
