using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace TermCity.Core.Rendering;

public readonly record struct OrcMotionPart(int Color, string Owner, string Tag);

public readonly record struct OrcMotionTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    internal static OrcMotionTransform Interpolate(OrcMotionTransform a, OrcMotionTransform b, float fraction) =>
        new(Vector3.Lerp(a.Position, b.Position, fraction),
            Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, fraction)),
            Vector3.Lerp(a.Scale, b.Scale, fraction));
}

public readonly record struct OrcMotionArm(Vector3 Shoulder, Vector3 Elbow, Vector3 Wrist, Vector3 PalmForward)
{
    public float WristAngleDegrees => MathF.Acos(Math.Clamp(
        Vector3.Dot(Vector3.Normalize(Wrist - Elbow), PalmForward), -1, 1)) * 180 / MathF.PI;

    internal static OrcMotionArm Interpolate(OrcMotionArm a, OrcMotionArm b, float fraction)
    {
        var shoulder = Vector3.Lerp(a.Shoulder, b.Shoulder, fraction);
        var upper = Vector3.Normalize(Vector3.Lerp(a.Elbow - a.Shoulder, b.Elbow - b.Shoulder, fraction));
        var lower = Vector3.Normalize(Vector3.Lerp(a.Wrist - a.Elbow, b.Wrist - b.Elbow, fraction));
        var elbow = shoulder + upper * .56f;
        return new(shoulder, elbow, elbow + lower * .56f,
            Vector3.Normalize(Vector3.Lerp(a.PalmForward, b.PalmForward, fraction)));
    }
}

public sealed class OrcMotionClip
{
    private readonly OrcMotionTransform[] _frames;
    private readonly OrcMotionArm[] _arms;
    private readonly OrcMotionPart[] _parts;
    public string Name { get; }
    public float Duration { get; }
    public int FrameCount { get; }
    public int PartCount { get; }

    internal OrcMotionClip(string name, float duration, int count, int parts,
        OrcMotionTransform[] frames, OrcMotionArm[] arms, OrcMotionPart[] definitions)
    {
        Name = name; Duration = duration; FrameCount = count; PartCount = parts;
        _frames = frames; _arms = arms; _parts = definitions;
    }

    public void Sample(double seconds, Span<OrcMotionTransform> destination)
    {
        if (destination.Length != PartCount) throw new ArgumentException("Incorrect part buffer size.", nameof(destination));
        var (frame, next, fraction) = FrameAt(seconds);
        var right = SampleArm(seconds, true); var left = SampleArm(seconds, false);
        for (int i = 0; i < PartCount; i++)
        {
            var a = _frames[frame * PartCount + i]; var b = _frames[next * PartCount + i];
            var pose = OrcMotionTransform.Interpolate(a, b, fraction);
            var definition = _parts[i];
            int side = definition.Owner == "arm-1" ? 0 : definition.Owner == "arm--1" ? 1 : -1;
            if (side >= 0)
            {
                var start = _arms[frame * 2 + side]; var end = _arms[next * 2 + side];
                var arm = side == 0 ? right : left;
                Vector3 anchorA, anchorB, anchor;
                Vector3? axis = null;
                if (definition.Tag.StartsWith("upper-arm", StringComparison.Ordinal))
                {
                    anchorA = start.Shoulder; anchorB = end.Shoulder; anchor = arm.Shoulder;
                    axis = Vector3.Normalize(arm.Elbow - arm.Shoulder);
                }
                else if (definition.Tag.StartsWith("forearm", StringComparison.Ordinal) ||
                         definition.Tag.StartsWith("bracer", StringComparison.Ordinal))
                {
                    anchorA = start.Elbow; anchorB = end.Elbow; anchor = arm.Elbow;
                    axis = Vector3.Normalize(arm.Wrist - arm.Elbow);
                }
                else if (definition.Tag.StartsWith("palm", StringComparison.Ordinal))
                {
                    anchorA = start.Wrist; anchorB = end.Wrist; anchor = arm.Wrist;
                }
                else { destination[i] = pose; continue; }
                var rotation = pose.Rotation;
                if (axis is { } direction)
                {
                    var current = Vector3.Transform(Vector3.UnitY, rotation);
                    var cross = Vector3.Cross(current, direction);
                    var correction = Quaternion.Normalize(new(cross, 1 + Vector3.Dot(current, direction)));
                    rotation = Quaternion.Normalize(correction * rotation);
                }
                var offsetA = Vector3.Transform(a.Position - anchorA, Quaternion.Conjugate(a.Rotation));
                var offsetB = Vector3.Transform(b.Position - anchorB, Quaternion.Conjugate(b.Rotation));
                var position = anchor + Vector3.Transform(Vector3.Lerp(offsetA, offsetB, fraction), rotation);
                pose = pose with { Position = position, Rotation = rotation };
            }
            destination[i] = pose;
        }
    }

    public OrcMotionArm SampleArm(double seconds, bool right)
    {
        var (frame, next, fraction) = FrameAt(seconds);
        int side = right ? 0 : 1;
        return OrcMotionArm.Interpolate(_arms[frame * 2 + side], _arms[next * 2 + side], fraction);
    }

    private (int Frame, int Next, float Fraction) FrameAt(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        double position = Duration == 0 ? 0 : Math.Clamp(seconds / Duration, 0, 1) * (FrameCount - 1);
        int frame = (int)position;
        return (frame, Math.Min(frame + 1, FrameCount - 1), (float)(position - frame));
    }
}

public sealed class OrcMotionLibrary
{
    public IReadOnlyList<OrcMotionPart> Parts { get; }
    public IReadOnlyList<OrcMotionClip> Clips { get; }
    public int SampleRate { get; }

    private OrcMotionLibrary(OrcMotionPart[] parts, OrcMotionClip[] clips, int rate)
    {
        Parts = Array.AsReadOnly(parts); Clips = Array.AsReadOnly(clips); SampleRate = rate;
    }

    public static OrcMotionLibrary Load(Stream compressed)
    {
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
        if (ReadText(reader) != "TermCity.OrcMotion.1") throw new InvalidDataException("Unsupported orc motion asset.");
        int rate = Count(reader, 240), parts = Count(reader, 2048), clips = Count(reader, 32);
        var definitions = new OrcMotionPart[parts];
        for (int i = 0; i < parts; i++)
        {
            uint color = reader.ReadUInt32();
            if (color > 0xffffff) throw new InvalidDataException("Invalid orc part color.");
            definitions[i] = new((int)color, ReadText(reader), ReadText(reader));
        }
        var animations = new OrcMotionClip[clips];
        long totalTransforms = 0;
        for (int c = 0; c < clips; c++)
        {
            string name = ReadText(reader);
            float duration = Number(reader);
            int frames = Count(reader, 10000);
            if (duration < 0 || duration > 60 || (duration == 0) != (frames == 1))
                throw new InvalidDataException("Invalid orc clip duration.");
            totalTransforms += (long)frames * parts;
            if (totalTransforms > 3_000_000) throw new InvalidDataException("Orc motion asset is too large.");
            var transforms = new OrcMotionTransform[frames * parts];
            var arms = new OrcMotionArm[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float time = Number(reader);
                float expected = frames == 1 ? 0 : duration * f / (frames - 1);
                if (MathF.Abs(time - expected) > .0001f) throw new InvalidDataException("Invalid orc frame timing.");
                for (int p = 0; p < parts; p++)
                {
                    var position = Vector(reader);
                    var rotation = new Quaternion(Number(reader), Number(reader), Number(reader), Number(reader));
                    var scale = Vector(reader);
                    if (MathF.Abs(rotation.LengthSquared() - 1) > .001f ||
                        MathF.Abs(scale.X) < .000001f || MathF.Abs(scale.Y) < .000001f || MathF.Abs(scale.Z) < .000001f)
                        throw new InvalidDataException("Invalid orc part transform.");
                    transforms[f * parts + p] = new(position, Quaternion.Normalize(rotation), scale);
                }
                for (int side = 0; side < 2; side++)
                {
                    var shoulder = Vector(reader); var elbow = Vector(reader); var wrist = Vector(reader);
                    var forward = Vector(reader);
                    if (MathF.Abs(forward.LengthSquared() - 1) > .001f ||
                        MathF.Abs(Vector3.Distance(shoulder, elbow) - .56f) > .00001f ||
                        MathF.Abs(Vector3.Distance(elbow, wrist) - .56f) > .00001f)
                        throw new InvalidDataException("Invalid orc arm diagnostics.");
                    arms[f * 2 + side] = new(shoulder, elbow, wrist, Vector3.Normalize(forward));
                }
            }
            animations[c] = new(name, duration, frames, parts, transforms, arms, definitions);
        }
        if (reader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Unexpected trailing orc motion data.");
        return new(definitions, animations, rate);
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        uint count = reader.ReadUInt32();
        if (count == 0 || count > maximum) throw new InvalidDataException("Invalid orc motion count.");
        return (int)count;
    }

    private static string ReadText(BinaryReader reader)
    {
        int count = Count(reader, 512);
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static float Number(BinaryReader reader)
    {
        float value = reader.ReadSingle();
        if (!float.IsFinite(value) || MathF.Abs(value) > 1000) throw new InvalidDataException("Invalid orc motion number.");
        return value;
    }

    private static Vector3 Vector(BinaryReader reader) => new(Number(reader), Number(reader), Number(reader));
}
