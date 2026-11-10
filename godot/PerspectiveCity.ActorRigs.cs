using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed class ActorRig
    {
        internal readonly List<(int Color, Transform3D Local)> Parts = [];
        internal int Signature, Arms, Strings, Arrows;
        internal Basis Facing;
    }
    private readonly Dictionary<(bool Friendly, int Id), ActorRig> _actorRigs = [];
    private ActorRig? _capturingRig;
    private Transform3D _rigInverse;
    private int _rigArms, _rigStrings, _rigArrows;
    internal int ActorRigsReusedLastFrame { get; private set; }

    private bool ReuseInvisibleRig(Dictionary<int, List<Transform3D>> batches, bool friendly, int id,
        int signature, Vector3 position)
    {
        // A generous screen margin refreshes joints before any part enters the visible panel.
        if (new Rect2(Vector2.Zero, DisplaySize).Grow(200).HasPoint(ScreenPoint(position + Vector3.Up)) ||
            !_actorRigs.TryGetValue((friendly, id), out var rig) || rig.Signature != signature) return false;
        var root = new Transform3D(rig.Facing, position);
        foreach (var part in rig.Parts)
        {
            var transform = root * part.Local;
            if (!batches.TryGetValue(part.Color, out var batch)) batches[part.Color] = batch = [];
            batch.Add(transform);
            _siegeActorPickBoxes.Add((transform, id, friendly));
        }
        ArcherArmSegmentsDrawn += rig.Arms;
        ArcherStringSegmentsDrawn += rig.Strings;
        NockedArrowsDrawn += rig.Arrows;
        ActorRigsReusedLastFrame++;
        return true;
    }

    private void BeginActorRig(bool friendly, int id, int signature, Transform3D root)
    {
        if (!_actorRigs.TryGetValue((friendly, id), out var rig)) _actorRigs[(friendly, id)] = rig = new();
        rig.Signature = signature;
        rig.Facing = root.Basis;
        rig.Parts.Clear();
        _capturingRig = rig;
        _rigInverse = root.AffineInverse();
        _rigArms = ArcherArmSegmentsDrawn;
        _rigStrings = ArcherStringSegmentsDrawn;
        _rigArrows = NockedArrowsDrawn;
    }

    private void CaptureActorPart(int color, Transform3D transform) =>
        _capturingRig?.Parts.Add((color, _rigInverse * transform));

    private void EndActorRig()
    {
        if (_capturingRig is { } rig)
        {
            rig.Arms = ArcherArmSegmentsDrawn - _rigArms;
            rig.Strings = ArcherStringSegmentsDrawn - _rigStrings;
            rig.Arrows = NockedArrowsDrawn - _rigArrows;
        }
        _capturingRig = null;
    }
}
