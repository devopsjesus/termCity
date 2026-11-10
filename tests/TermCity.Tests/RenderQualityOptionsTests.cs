using TermCity.GodotApp;

namespace TermCity.Tests;

public class RenderQualityOptionsTests
{
    [Fact]
    public void NormalQualityRemainsReferenceAndComparisonsRequireDiagnostics()
    {
        Assert.Equal(DiagnosticRenderQuality.Reference, GodotOptions.Parse([]).RenderQuality);
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--render-quality", "Native4x"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--camera-performance-test", "--render-quality", "bad"]));
        Assert.Throws<ArgumentException>(() => GodotOptions.Parse(["--camera-performance-test", "--render-quality", "99"]));
    }

    [Theory]
    [InlineData("Reference", DiagnosticRenderQuality.Reference)]
    [InlineData("Native8x", DiagnosticRenderQuality.Native8x)]
    [InlineData("Native4x", DiagnosticRenderQuality.Native4x)]
    public void ExplicitCameraComparisonsUseRequestedQuality(string value, DiagnosticRenderQuality expected)
    {
        var options = GodotOptions.Parse(["--camera-performance-test", "--render-quality", value]);
        Assert.Equal(expected, options.RenderQuality);
        Assert.True(options.CameraPerformanceTest);
    }
}
