using System.Numerics;
using TermCity.Core.Simulation;

namespace TermCity.Core.Rendering;

public enum ArcherBowPhase { Ready, Drawing, FollowThrough, Nocking }

public readonly record struct ArcherBowPose(
    Vector3 LeftShoulder, Vector3 LeftElbow, Vector3 LeftHand,
    Vector3 RightShoulder, Vector3 RightElbow, Vector3 RightHand,
    Vector3 UpperTip, Vector3 UpperLimb, Vector3 LowerLimb, Vector3 LowerTip,
    Vector3 StringNock, Vector3 ArrowNock, Vector3 ArrowTip,
    float TorsoTurn, float Draw, bool ArrowVisible, ArcherBowPhase Phase);

public static class ArcherArtwork
{
    public const float ModelScale = 1.8f;
    public const float UpperArmLength = .18f;
    public const float ForearmLength = .19f;
    public const float ArrowLength = .36f;
    public const float StringLength = .60f;
    public const float UpperArmThickness = .075f;
    public const float ForearmThickness = .065f;
    public static Vector3 TorsoCenter => new(0, .40f, 0);
    public static Vector3 TorsoSize => new(.26f, .32f, .18f);
    public static Vector3 BowHandSize => new(.075f, .065f, .07f);
    public static Vector3 StringHandSize => new(.065f, .055f, .065f);

    public static ArcherBowPose Pose(double elapsed, bool shooting, bool animated = true)
    {
        if (!double.IsFinite(elapsed) || elapsed < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        float windup = (float)CombatProfiles.Archer.Windup;
        float cycle = (float)CombatProfiles.Archer.Interval;
        float t = shooting ? Math.Clamp((float)elapsed, 0, cycle) : cycle;
        if (!animated) t = shooting ? windup * .9f : cycle;
        var carry = new Vector3(.005f, .46f, -.21f);
        var aim = new Vector3(.10f, .58f, -.41f);
        var anchor = new Vector3(.14f, .62f, -.12f);
        var quiver = new Vector3(.21f, .34f, .13f);
        Vector3 grip, hand, stringNock;
        float draw, turn, rearArm;
        bool arrow;
        ArcherBowPhase phase;
        if (t < windup)
        {
            float raise = Ease(t / (windup * .85f));
            draw = raise;
            grip = Vector3.Lerp(carry, aim, raise);
            hand = Vector3.Lerp(grip + new Vector3(0, 0, .055f), anchor, draw);
            stringNock = hand;
            turn = -raise * .8f;
            rearArm = draw;
            arrow = true;
            phase = ArcherBowPhase.Drawing;
        }
        else
        {
            float release = Ease((t - windup) / .055f);
            float lower = Ease((t - windup - .25f) / .35f);
            float nock = Ease((t - cycle * .63f) / (cycle * .23f));
            draw = 1 - release;
            grip = Vector3.Lerp(aim + new Vector3(-.012f * release, -.008f * release, .012f * release), carry, lower);
            var follow = anchor + new Vector3(.10f * release, 0, .07f * release);
            var loweringHand = Vector3.Lerp(follow, quiver, lower) +
                Vector3.UnitX * (.10f * MathF.Sin(lower * MathF.PI));
            var readyHand = carry + new Vector3(0, 0, .055f);
            var nockGuide = new Vector3(.32f, .43f, -.26f);
            var nockingHand = Vector3.Lerp(Vector3.Lerp(quiver, nockGuide, nock),
                Vector3.Lerp(nockGuide, readyHand, nock), nock);
            hand = t >= cycle * .63f ? nockingHand : loweringHand;
            stringNock = Vector3.Lerp(anchor, grip + new Vector3(0, 0, .055f), release);
            turn = -.8f * (1 - lower);
            rearArm = 1 - lower;
            arrow = t >= cycle * .63f;
            phase = t >= cycle * .86f ? ArcherBowPhase.Ready :
                t >= cycle * .63f ? ArcherBowPhase.Nocking : ArcherBowPhase.FollowThrough;
        }
        var torso = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);
        var leftShoulder = Vector3.Transform(new Vector3(-.17f, .55f, .005f), torso);
        var rightShoulder = Vector3.Transform(new Vector3(.17f, .55f, .005f), torso);
        var leftElbow = Elbow(leftShoulder, grip, new(-.35f, .40f, -.14f));
        var rightElbow = Elbow(rightShoulder, hand, rightShoulder +
            Vector3.Lerp(new(.32f, -.15f, -.20f), new(.30f, .05f, .32f), rearArm));
        float tipZ = .055f + draw * .12f;
        var pullOffset = stringNock - grip;
        float halfString = StringLength / 2;
        float radialSquared = pullOffset.X * pullOffset.X + MathF.Pow(pullOffset.Z - tipZ, 2);
        float tipHeight = MathF.Sqrt(halfString * halfString -
            radialSquared / (1 - pullOffset.Y * pullOffset.Y / (halfString * halfString)));
        float limbHeight = .16f - (.30f - tipHeight) * .16f;
        var upperLimb = grip + new Vector3(0, limbHeight, .012f + draw * .065f);
        var lowerLimb = grip + new Vector3(0, -limbHeight, .012f + draw * .065f);
        var upperTip = grip + new Vector3(0, tipHeight, tipZ);
        var lowerTip = grip + new Vector3(0, -tipHeight, tipZ);
        var arrowNock = t < windup ? stringNock : hand;
        var arrowDirection = grip - arrowNock;
        if (arrowDirection.LengthSquared() < .000001f) arrowDirection = -Vector3.UnitZ;
        return new(leftShoulder, leftElbow, grip, rightShoulder, rightElbow, hand,
            upperTip, upperLimb, lowerLimb, lowerTip, stringNock,
            arrowNock, arrowNock + Vector3.Normalize(arrowDirection) * ArrowLength,
            turn, draw, arrow, phase);
    }

    private static float Ease(float value)
    {
        float t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static Vector3 Elbow(Vector3 shoulder, Vector3 hand, Vector3 pole)
    {
        var delta = hand - shoulder;
        float distance = delta.Length();
        if (distance <= .00001f || distance >= UpperArmLength + ForearmLength)
            throw new InvalidOperationException("The bow pose exceeds the archer's articulated arm reach.");
        var direction = delta / distance;
        float along = (UpperArmLength * UpperArmLength - ForearmLength * ForearmLength + distance * distance) /
            (2 * distance);
        var bend = pole - shoulder;
        bend -= direction * Vector3.Dot(bend, direction);
        float height = MathF.Sqrt(Math.Max(0, UpperArmLength * UpperArmLength - along * along));
        return shoulder + direction * along + Vector3.Normalize(bend) * height;
    }
}
