using System.Numerics;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class ArcherArtworkTests
{
    [Fact]
    public void FullDrawBracesTheBowAndAnchorsTheDrawingHandAtTheCheek()
    {
        var ready = ArcherArtwork.Pose(0, shooting: false);
        var draw = ArcherArtwork.Pose(CombatProfiles.Archer.Windup - .01, shooting: true);
        Assert.True(draw.Draw > .99f);
        Assert.InRange(draw.RightHand.X, .12f, .15f);
        Assert.InRange(draw.RightHand.Y, .60f, .65f);
        Assert.InRange(draw.RightHand.Z, -.14f, -.10f);
        Assert.Equal(draw.RightHand, draw.StringNock);
        Assert.Equal(draw.StringNock, draw.ArrowNock);
        Assert.True(draw.RightHand.Z - draw.LeftHand.Z > .2f);
        Assert.True(Vector3.Distance(draw.LeftHand, ready.LeftHand) > .15f);
        Assert.True(draw.RightElbow.Z > draw.RightHand.Z + .08f,
            $"Draw elbow={draw.RightElbow}, hand={draw.RightHand}.");
        Assert.True(Vector3.Dot(Vector3.Normalize(draw.LeftShoulder - draw.LeftElbow),
            Vector3.Normalize(draw.LeftHand - draw.LeftElbow)) < -.9f);
        Assert.True(Vector3.Dot(Vector3.Normalize(draw.RightShoulder - draw.RightElbow),
            Vector3.Normalize(draw.RightHand - draw.RightElbow)) > 0);
    }

    [Fact]
    public void EveryPosePreservesArmLengthsTautStringAndContinuousJointMotion()
    {
        var previous = ArcherArtwork.Pose(0, shooting: true);
        for (int step = 0; step <= 1500; step++)
        {
            double elapsed = step * CombatProfiles.Archer.Interval / 1500;
            var pose = ArcherArtwork.Pose(elapsed, shooting: true);
            Assert.Equal(ArcherArtwork.UpperArmLength, Vector3.Distance(pose.LeftShoulder, pose.LeftElbow), 4);
            Assert.Equal(ArcherArtwork.ForearmLength, Vector3.Distance(pose.LeftElbow, pose.LeftHand), 4);
            Assert.Equal(ArcherArtwork.UpperArmLength, Vector3.Distance(pose.RightShoulder, pose.RightElbow), 4);
            Assert.Equal(ArcherArtwork.ForearmLength, Vector3.Distance(pose.RightElbow, pose.RightHand), 4);
            Assert.Equal(ArcherArtwork.StringLength,
                Vector3.Distance(pose.UpperTip, pose.StringNock) + Vector3.Distance(pose.StringNock, pose.LowerTip), 4);
            Assert.Equal(ArcherArtwork.ArrowLength, Vector3.Distance(pose.ArrowNock, pose.ArrowTip), 4);
            Assert.True(Vector3.Distance(previous.LeftHand, pose.LeftHand) < .02f);
            Assert.True(Vector3.Distance(previous.RightHand, pose.RightHand) < .02f);
            Assert.True(Vector3.Distance(previous.LeftElbow, pose.LeftElbow) < .02f);
            Assert.True(Vector3.Distance(previous.RightElbow, pose.RightElbow) < .02f,
                $"Elbow jumped at {elapsed:F3}: {previous.RightElbow}->{pose.RightElbow}.");
            previous = pose;
        }
    }

    [Fact]
    public void ArmsAndHandsClearTheActualRotatedTorsoThroughoutTheWholeCycle()
    {
        for (int step = 0; step <= 1500; step++)
        {
            double elapsed = step * CombatProfiles.Archer.Interval / 1500;
            var pose = ArcherArtwork.Pose(elapsed, shooting: true);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pose.TorsoTurn);
            Vector3[] torsoAxes = [
                Vector3.Transform(Vector3.UnitX, rotation),
                Vector3.UnitY,
                Vector3.Transform(Vector3.UnitZ, rotation),
            ];
            Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
            void Arm(Vector3 start, Vector3 end, float thickness, string name, float socket = 0)
            {
                var direction = Vector3.Normalize(end - start);
                start += direction * socket;
                var up = Math.Abs(direction.Y) > .98f ? Vector3.UnitX : Vector3.UnitY;
                var x = Vector3.Normalize(Vector3.Cross(direction, up));
                var y = Vector3.Normalize(Vector3.Cross(x, direction));
                Assert.False(Overlap(ArcherArtwork.TorsoCenter, ArcherArtwork.TorsoSize / 2, torsoAxes,
                    (start + end) / 2, new(thickness / 2, thickness / 2, Vector3.Distance(start, end) / 2),
                    [x, y, direction]), $"{name} clips the torso at {elapsed:F3}s: {start}->{end}.");
                Assert.False(Overlap(new(0, .66f, 0), new(.09f), axes,
                    (start + end) / 2, new(thickness / 2, thickness / 2, Vector3.Distance(start, end) / 2),
                    [x, y, direction]), $"{name} clips the head at {elapsed:F3}s.");
            }
            // The arm roots meet the garment at the shoulder socket.
            Arm(pose.LeftShoulder, pose.LeftElbow, ArcherArtwork.UpperArmThickness, "Left upper arm", .04f);
            Arm(pose.RightShoulder, pose.RightElbow, ArcherArtwork.UpperArmThickness, "Right upper arm", .04f);
            Arm(pose.LeftElbow, pose.LeftHand, ArcherArtwork.ForearmThickness, "Left forearm");
            Arm(pose.RightElbow, pose.RightHand, ArcherArtwork.ForearmThickness, "Right forearm");
            Assert.False(Overlap(ArcherArtwork.TorsoCenter, ArcherArtwork.TorsoSize / 2, torsoAxes,
                pose.LeftHand, ArcherArtwork.BowHandSize / 2, axes), $"Bow hand clips at {elapsed:F3}s.");
            Assert.False(Overlap(ArcherArtwork.TorsoCenter, ArcherArtwork.TorsoSize / 2, torsoAxes,
                pose.RightHand, ArcherArtwork.StringHandSize / 2, axes), $"String hand clips at {elapsed:F3}s.");
            Assert.False(Overlap(new(0, .66f, 0), new(.09f), axes,
                pose.LeftHand, ArcherArtwork.BowHandSize / 2, axes), $"Bow hand clips the head at {elapsed:F3}s.");
            Assert.False(Overlap(new(0, .66f, 0), new(.09f), axes,
                pose.RightHand, ArcherArtwork.StringHandSize / 2, axes), $"String hand clips the head at {elapsed:F3}s.");
        }
    }

    private static bool Overlap(Vector3 a, Vector3 halfA, Vector3[] axesA,
        Vector3 b, Vector3 halfB, Vector3[] axesB)
    {
        var axes = axesA.Concat(axesB).Concat(axesA.SelectMany(x => axesB.Select(y => Vector3.Cross(x, y))));
        foreach (var axis in axes)
        {
            if (axis.LengthSquared() < .000001f) continue;
            float radiusA = Math.Abs(Vector3.Dot(axis, axesA[0])) * halfA.X +
                Math.Abs(Vector3.Dot(axis, axesA[1])) * halfA.Y + Math.Abs(Vector3.Dot(axis, axesA[2])) * halfA.Z;
            float radiusB = Math.Abs(Vector3.Dot(axis, axesB[0])) * halfB.X +
                Math.Abs(Vector3.Dot(axis, axesB[1])) * halfB.Y + Math.Abs(Vector3.Dot(axis, axesB[2])) * halfB.Z;
            if (Math.Abs(Vector3.Dot(b - a, axis)) >= radiusA + radiusB - .0002f * axis.Length()) return false;
        }
        return true;
    }

    [Fact]
    public void ArrowLeavesAtCommittedReleaseThenTheArcherRecoversAndNocksTheNextArrow()
    {
        double release = CombatProfiles.Archer.Windup;
        var before = ArcherArtwork.Pose(release - .001, shooting: true);
        var released = ArcherArtwork.Pose(release, shooting: true);
        var follow = ArcherArtwork.Pose(release + .1, shooting: true);
        var nock = ArcherArtwork.Pose(CombatProfiles.Archer.Interval * .75, shooting: true);
        Assert.True(before.ArrowVisible);
        Assert.False(released.ArrowVisible);
        Assert.False(follow.ArrowVisible);
        Assert.Equal(ArcherBowPhase.FollowThrough, follow.Phase);
        Assert.True(follow.RightHand.Z > before.RightHand.Z);
        Assert.True(follow.Draw < .001f);
        Assert.True(nock.ArrowVisible);
        Assert.Equal(ArcherBowPhase.Nocking, nock.Phase);
        Assert.Equal(ArcherArtwork.Pose(0, shooting: false),
            ArcherArtwork.Pose(CombatProfiles.Archer.Interval, shooting: true));
        Assert.Equal(.3, CombatProfiles.Archer.Windup);
        Assert.Equal(1.5, CombatProfiles.Archer.Interval);
        Assert.Equal(40, CombatProfiles.Archer.Damage);
    }

    [Fact]
    public void ReducedMotionKeepsAStaticAimingPoseAndInvalidClocksFailExplicitly()
    {
        Assert.Equal(ArcherArtwork.Pose(.05, shooting: true, animated: false),
            ArcherArtwork.Pose(1.1, shooting: true, animated: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArcherArtwork.Pose(double.NaN, shooting: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArcherArtwork.Pose(-.1, shooting: true));
    }
}
