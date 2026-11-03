using Godot;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed record AimedPart(WallComponent Wall, Vector3 Origin, Transform3D Canonical,
        int Color, int Instance, int Pick, int Highlight);
    private readonly List<AimedPart> _aimedParts = [];
    private readonly Dictionary<int, float> _aimAngles = [];

    private Basis WeaponAim(WallComponent wall)
    {
        var shot = _renderedSettlement?.Projectiles.LastOrDefault(s => s.Id > 0 && s.SourceWallId == wall.Id &&
            s.Kind is WallDefenseKind.Ballista or WallDefenseKind.Trebuchet);
        float angle = _aimAngles.GetValueOrDefault(wall.Id);
        if (shot is not null)
        {
            float target = Mathf.Atan2(-(float)(shot.EndX - wall.Center.X), -(float)(shot.EndY - wall.Center.Y));
            float fraction = (float)Math.Clamp(shot.Elapsed / Math.Max(.01, shot.Windup), 0, 1);
            angle = Mathf.LerpAngle(angle, target, fraction);
            _aimAngles[wall.Id] = angle;
        }
        return new Basis(Vector3.Up, angle);
    }

    private void AnimateAimedWeapons()
    {
        foreach (var group in _aimedParts.GroupBy(part => part.Wall))
        {
            var basis = WeaponAim(group.Key);
            foreach (var part in group)
            {
                var transform = new Transform3D(basis * part.Canonical.Basis,
                    part.Origin + basis * (part.Canonical.Origin - part.Origin));
                _wallInstances[part.Color].Multimesh.SetInstanceTransform(part.Instance, transform);
                _siegePickBoxes[part.Pick] = (DefenseBounds(transform), part.Wall.Cell, part.Color);
                _siegePickTransforms[part.Pick] = transform;
                _wallHighlightBoxes[part.Wall.Id][part.Highlight] = transform;
            }
        }
    }
}
