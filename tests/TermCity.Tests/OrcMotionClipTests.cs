using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class OrcMotionClipTests
{
    private static string RepositoryFile(params string[] path)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "godot", "Assets", "OrcMotion", "hammer.orcmotion")))
            root = Directory.GetParent(root)?.FullName ?? throw new FileNotFoundException("Orc preview asset not found.");
        return Path.Combine([root, .. path]);
    }

    private static readonly Lazy<OrcMotionLibrary> Library = new(() =>
    {
        using var stream = File.OpenRead(RepositoryFile("godot", "Assets", "OrcMotion", "hammer.orcmotion"));
        return OrcMotionLibrary.Load(stream);
    });

    [Fact]
    public void BakedAssetMatchesItsPreservedAuthoringSource()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(RepositoryFile("godot", "Assets", "OrcMotion", "hammer.json")));
        var source = SHA256.HashData(File.ReadAllBytes(RepositoryFile("tools", "orc-motion", "source.html")));
        var asset = SHA256.HashData(File.ReadAllBytes(RepositoryFile("godot", "Assets", "OrcMotion", "hammer.orcmotion")));
        Assert.Equal(manifest.RootElement.GetProperty("sourceSha256").GetString(), Convert.ToHexString(source).ToLowerInvariant());
        Assert.Equal(manifest.RootElement.GetProperty("assetSha256").GetString(), Convert.ToHexString(asset).ToLowerInvariant());
    }

    [Fact]
    public void OriginalPartsAndAllApprovedClipsArePresent()
    {
        var library = Library.Value;
        Assert.Equal(251, library.Parts.Count);
        Assert.Equal(11, library.Clips.Count);
        Assert.Equal(120, library.SampleRate);
        Assert.Equal("Hammer combo", library.Clips[1].Name);
        Assert.InRange(library.Clips[1].Duration, 3.73332f, 3.73335f);
        Assert.All(library.Clips.Skip(5), clip => Assert.Equal(0, clip.Duration));
        Assert.Contains(library.Parts, part => part.Tag == "weapon-head");
        Assert.Contains(library.Parts, part => part.Tag.StartsWith("skull-shoulder"));
    }

    [Fact]
    public void EveryAuthoredFrameRetainsFixedArmLengths()
    {
        foreach (var clip in Library.Value.Clips)
            for (int frame = 0; frame < clip.FrameCount; frame++)
                foreach (bool right in new[] { true, false })
                {
                    var arm = clip.SampleArm(clip.Duration * (double)frame / Math.Max(1, clip.FrameCount - 1), right);
                    Assert.InRange(Vector3.Distance(arm.Shoulder, arm.Elbow), .55999f, .56001f);
                    Assert.InRange(Vector3.Distance(arm.Elbow, arm.Wrist), .55999f, .56001f);
                }
    }

    [Fact]
    public void ApprovedRightElbowAndWristCorrectionSurvivesNativeBake()
    {
        var clip = Library.Value.Clips[1];
        Assert.InRange(clip.SampleArm(77 / 30d, true).WristAngleDegrees, 18, 21);
        Assert.InRange(clip.SampleArm(79 / 30d, true).WristAngleDegrees, 16, 19);
        Assert.InRange(clip.SampleArm(71 / 30d, true).WristAngleDegrees, 22, 25);
    }

    [Fact]
    public void NativePartInterpolationKeepsTheVisibleBonesAttached()
    {
        var library = Library.Value;
        var buffer = new OrcMotionTransform[library.Parts.Count];
        foreach (var clip in library.Clips)
            for (int frame = 0; frame < clip.FrameCount; frame += 3)
            {
                double time = clip.Duration * (frame + .37) / Math.Max(1, clip.FrameCount - 1);
                clip.Sample(time, buffer);
                Assert.All(buffer, part =>
                {
                    Assert.True(float.IsFinite(part.Position.X + part.Position.Y + part.Position.Z));
                    Assert.InRange(part.Rotation.LengthSquared(), .9999f, 1.0001f);
                });
                foreach (bool right in new[] { true, false })
                {
                    var arm = clip.SampleArm(time, right);
                    Assert.InRange(Vector3.Distance(arm.Shoulder, arm.Elbow), .55999f, .56001f);
                    Assert.InRange(Vector3.Distance(arm.Elbow, arm.Wrist), .55999f, .56001f);
                    string side = right ? "1" : "-1";
                    int upper = Enumerable.Range(0, buffer.Length).First(i => library.Parts[i].Tag == "upper-arm-" + side);
                    int lower = Enumerable.Range(0, buffer.Length).First(i => library.Parts[i].Tag == "forearm-" + side);
                    Assert.InRange(Vector3.Distance(Vector3.Transform(new(0, -.5f, 0), buffer[upper].Matrix), arm.Shoulder), 0, .00001);
                    Assert.InRange(Vector3.Distance(Vector3.Transform(new(0, .5f, 0), buffer[upper].Matrix), arm.Elbow), 0, .00001);
                    Assert.InRange(Vector3.Distance(Vector3.Transform(new(0, -.5f, 0), buffer[lower].Matrix), arm.Elbow), 0, .00001);
                    Assert.InRange(Vector3.Distance(Vector3.Transform(new(0, .5f, 0), buffer[lower].Matrix), arm.Wrist), 0, .00001);
                }
            }
    }

    [Fact]
    public void EndpointsClampAndInvalidInputsAreExplicit()
    {
        var clip = Library.Value.Clips[1];
        var a = new OrcMotionTransform[clip.PartCount];
        var b = new OrcMotionTransform[clip.PartCount];
        clip.Sample(-1, a); clip.Sample(0, b); Assert.Equal(b, a);
        clip.Sample(clip.Duration + 1, a); clip.Sample(clip.Duration, b); Assert.Equal(b, a);
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.Sample(double.NaN, a));
        Assert.Throws<ArgumentException>(() => clip.Sample(0, new OrcMotionTransform[1]));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
            gzip.Write([255, 255, 255, 255]);
        output.Position = 0;
        Assert.Throws<InvalidDataException>(() => OrcMotionLibrary.Load(output));
    }

    [Fact]
    public void WallStandOffClearsTheActualApprovedBodySwingEnvelope()
    {
        var library = Library.Value;
        var clip = library.Clips[1];
        var buffer = new OrcMotionTransform[library.Parts.Count];
        float bodyReach = 0, hammerReach = 0;
        for (int frame = 0; frame < clip.FrameCount; frame++)
        {
            clip.Sample(clip.Duration * (double)frame / (clip.FrameCount - 1), buffer);
            for (int part = 0; part < buffer.Length; part++)
            {
                var m = buffer[part].Matrix;
                float reach = (buffer[part].Position.Z +
                    (Math.Abs(m.M13) + Math.Abs(m.M23) + Math.Abs(m.M33)) / 2) *
                    TermCity.Core.World.WorldScale.Feet(6) / 2.25f;
                if (library.Parts[part].Owner == "weapon") hammerReach = Math.Max(hammerReach, reach);
                else bodyReach = Math.Max(bodyReach, reach);
            }
        }
        Assert.True(bodyReach + .15 < OrcCombatAnimation.WallStandOff, $"Body reaches {bodyReach}");
        Assert.True(hammerReach >= OrcCombatAnimation.WallReach, $"Hammer reaches {hammerReach}");
    }

    [Fact]
    public void UprightHammerIsCenteredAboveTheHeadBeforeTheTorsoMoves()
    {
        var library = Library.Value;
        var buffer = new OrcMotionTransform[library.Parts.Count];
        library.Clips[1].Sample(79 / 30d, buffer);
        int head = Enumerable.Range(0, buffer.Length).First(i => library.Parts[i].Tag == "body-head");
        int hammer = Enumerable.Range(0, buffer.Length).First(i => library.Parts[i].Tag == "weapon-head");
        Assert.InRange(Math.Abs(buffer[hammer].Position.X - buffer[head].Position.X), 0, .00001);
        Assert.InRange(Math.Abs(buffer[hammer].Position.Z - buffer[head].Position.Z), 0, .00001);
        Assert.True(buffer[hammer].Position.Y > buffer[head].Position.Y + 1);
    }
}
