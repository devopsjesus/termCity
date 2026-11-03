using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal DepthTreatment DepthTreatment { get; init; } = DepthStudyArtwork.DefaultTreatment;
    internal IReadOnlyList<WallComponent> DepthStudyWalls { get; init; } = [];
    internal bool DepthStudyPalisade { get; init; }
    internal float DepthStudyFocusHeight { get; init; }
    private DirectionalLight3D _sunlight = null!;
    internal bool DirectionalShadowsEnabled => _sunlight.ShadowEnabled;

    internal void SetDepthStudyShadows(bool enabled)
    {
        if (!DepthStudyArtwork.UsesLighting(DepthTreatment))
            throw new InvalidOperationException("Shadow comparison requires a lighting treatment.");
        _sunlight.ShadowEnabled = enabled;
        RequestRender();
    }

    internal void SetDepthStudyCamera(float azimuth, float elevation)
    {
        Azimuth = azimuth;
        Elevation = elevation;
    }

    private IEnumerable<PalisadeWallPart> WallModelParts(WallComponent wall, GameMap? map = null, bool foundations = false)
    {
        bool relief = DepthStudyArtwork.UsesRelief(DepthTreatment);
        var fragments = DepthStudyArtwork.WallFragments(wall, relief, map);
        if (wall.Material == WallMaterial.Wood && relief)
        {
            foreach (var part in PalisadeArtwork.WallParts(wall, fragments)) yield return part;
        }
        else
            foreach (var fragment in fragments) yield return new(fragment, WallPartColor(wall, fragment));
        if (foundations)
        {
            if (map is null) throw new InvalidOperationException("Wall foundations require their terrain map.");
            foreach (var fragment in WallDamageLayout.Foundations(wall, map))
                yield return new(fragment, wall.Material == WallMaterial.Wood
                    ? PalisadeArtwork.BarkShadeColor : WallPartColor(wall, fragment));
        }
    }

    private static int WallPartColor(WallComponent wall, WallFragment fragment)
    {
        var material = wall.Material;
        bool wood = material == WallMaterial.Wood;
        int band = WallDamageLayout.DamageBand(wall);
        int bodyColor = wood ? band < 4 ? 0x79634b : 0x4b3b2c : band < 4 ? 0xa4a599 : 0x79796e;
        if (WallDamageLayout.IsBurning(wall)) bodyColor = wood ? 0x302a23 : 0x55544d;
        return fragment.Kind switch
        {
            WallFragmentKind.Trim => material == WallMaterial.ReinforcedStone ? 0x586b71 : wood ? 0xa69666 : bodyColor,
            WallFragmentKind.Door => band < 4 ? 0x443d32 : 0x302a23,
            WallFragmentKind.DoorBrace => band < 4 ? wood ? 0x79634b : 0x586b71 : 0x4b3b2c,
            WallFragmentKind.DoorStud => 0xc6bf9c,
            WallFragmentKind.Grate => 0x343b3e,
            _ => bodyColor,
        };
    }

    private void DrawDepthStudySubjects()
    {
        foreach (var wall in DepthStudyWalls)
        {
            var previous = _buildingTransform;
            _buildingTransform = new(WallBasis(wall), new(wall.Center.X,
                _map is null ? 0 : WallDamageLayout.GroundHeight(wall, _map), wall.Center.Y));
            try
            {
                foreach (var part in WallModelParts(wall, _map))
                {
                    var fragment = part.Fragment;
                    Box(new(fragment.Center.X, fragment.Center.Y, fragment.Center.Z),
                        new(fragment.Size.X, fragment.Size.Y, fragment.Size.Z), part.Color, wall.Cell);
                }
            }
            finally { _buildingTransform = previous; }
        }
        if (DepthStudyPalisade)
            foreach (var part in PalisadeArtwork.Parts(2.2f, 1.6f, seed: 42))
                Box(new(24 + part.Center.X * 5, part.Center.Y * 5, 24 + part.Center.Z * 5),
                    new(part.Size.X * 5, part.Size.Y * 5, part.Size.Z * 5), part.Color, new Pos(24, 24));
    }
}
