using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private static readonly Lazy<OrcMotionLibrary> OrcMotion = new(() =>
    {
        using var stream = new MemoryStream(Godot.FileAccess.GetFileAsBytes("res://Assets/OrcMotion/hammer.orcmotion"));
        return OrcMotionLibrary.Load(stream);
    });
    private OrcMotionTransform[]? _orcPose;
    internal int OrcPartsDrawn { get; private set; }
    internal int OrcsDrawn { get; private set; }

    private void DrawOrc(Dictionary<int, List<Transform3D>> batches, Vector3 ground, SiegeEnemyPose pose,
        SiegeEnemy enemy, SiegeDeath? death = null, double deathSeconds = 0)
    {
        var library = OrcMotion.Value;
        _orcPose ??= new OrcMotionTransform[library.Parts.Count];
        int clip = death is null ? pose.OrcAttacking ? 1 : 0 : death.Cause switch
        {
            SiegeDeathCause.Arrow => 2,
            SiegeDeathCause.Blade => 3,
            _ => 4,
        };
        double seconds = death is not null ? Math.Min(deathSeconds, 4) :
            pose.OrcAttacking ? pose.OrcAnimationSeconds :
            pose.WalkDistance % 1.4 / 1.4 * library.Clips[0].Duration;
        if (!SiegeAnimationEnabled) seconds = death is null ? 0 : 3.9;
        library.Clips[clip].Sample(seconds, _orcPose);
        float size = WorldScale.Feet((float)enemy.HeightFeet) / 2.25f;
        var facing = Basis.LookingAt(new Vector3(pose.Forward.X, 0, pose.Forward.Y), Vector3.Up) *
            new Basis(Vector3.Up, Mathf.Pi);
        for (int i = 0; i < _orcPose.Length; i++)
        {
            var part = _orcPose[i];
            var q = part.Rotation;
            var rotation = new Basis(new Quaternion(q.X, q.Y, q.Z, q.W));
            var scale = part.Scale;
            var basis = facing * new Basis(rotation.X * scale.X, rotation.Y * scale.Y, rotation.Z * scale.Z);
            var origin = ground + facing * new Vector3(part.Position.X, part.Position.Y, part.Position.Z) * size;
            var transform = new Transform3D(new(basis.X * size, basis.Y * size, basis.Z * size), origin);
            int color = death?.Cause == SiegeDeathCause.Oil ? 0x493b29 : library.Parts[i].Color;
            if (!batches.TryGetValue(color, out var batch)) batches[color] = batch = [];
            batch.Add(transform);
            if (death is null)
            {
                CaptureActorPart(color, transform);
                _siegeActorPickBoxes.Add((transform, enemy.Id, false));
            }
            OrcPartsDrawn++;
        }
        OrcsDrawn++;
    }
}
