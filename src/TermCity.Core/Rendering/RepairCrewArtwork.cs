using TermCity.Core.Simulation;
using System.Numerics;

namespace TermCity.Core.Rendering;

public readonly record struct RepairBucketPose(float Lift, float Tilt, float Pour);
public readonly record struct BucketWaterSegment(Vector3 Start, Vector3 End, float Width);

public static class RepairCrewArtwork
{
    public const float WorkerHeight = 0.34f;
    public const float WorkerWidth = 0.11f;
    public const float BoardLength = 0.5f;
    public const float HammerHandleLength = 0.14f;
    public const float BucketWidth = 0.10f;
    public const float BucketHeight = 0.11f;

    public static bool FightingFire(WallComponent wall) => WallDamageLayout.IsBurning(wall);

    public static RepairBucketPose BucketPose(double seconds, int worker, bool animated = true)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!animated) return new(0.06f, 0, -1);
        float cycle = (float)((seconds + worker * 0.5) % 1);
        float lift = Math.Clamp(cycle / 0.3f, 0, 1) * Math.Clamp((1 - cycle) / 0.2f, 0, 1);
        float tip = Math.Clamp((cycle - 0.25f) / 0.15f, 0, 1) *
            Math.Clamp((0.85f - cycle) / 0.2f, 0, 1);
        return new(lift * 0.15f, tip * 1.7f,
            cycle is >= 0.4f and < 0.75f ? (cycle - 0.4f) / 0.35f : -1);
    }

    public static IReadOnlyList<BucketWaterSegment> BucketWater(Vector3 lip, Vector3 target, float pour)
    {
        if (!float.IsFinite(pour) || pour < 0 || pour > 1)
            throw new ArgumentOutOfRangeException(nameof(pour));
        float head = Math.Min(1, pour * 1.6f);
        float tail = Math.Clamp((pour - 0.45f) / 0.55f, 0, 1);
        if (head <= tail) return [];
        const int count = 12;
        var segments = new BucketWaterSegment[count];
        Vector3 Point(float t) => Vector3.Lerp(lip, target, t) + Vector3.UnitY * (0.18f * 4 * t * (1 - t));
        var start = Point(tail);
        for (int i = 0; i < count; i++)
        {
            float t = tail + (head - tail) * (i + 1) / count;
            var end = Point(t);
            float width = BucketWidth * (0.60f + MathF.Sin(t * MathF.PI) * 0.85f);
            segments[i] = new(start, end, width);
            start = end;
        }
        return segments;
    }
}
