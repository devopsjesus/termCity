using Godot;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly Shader _weaponRangeShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never;
            uniform vec2 center;
            uniform float outer_radius;
            uniform float inner_radius;
            uniform vec3 tint = vec3(1.0, 0.79, 0.3);
            uniform bool footprint = false;
            varying vec2 ground_position;
            void vertex() { ground_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz; }
            void fragment() {
                float dist = length(ground_position - center);
                if (!footprint && (dist > outer_radius || dist < inner_radius)) { discard; }
                float feather = max(fwidth(dist), 0.025);
                float outer = 1.0 - smoothstep(outer_radius - feather, outer_radius, dist);
                float inner = inner_radius > 0.0 ? smoothstep(inner_radius, inner_radius + feather, dist) : 1.0;
                float edge = 1.0 - smoothstep(feather, feather * 3.0, outer_radius - dist);
                if (inner_radius > 0.0) {
                    edge = max(edge, 1.0 - smoothstep(feather, feather * 3.0, dist - inner_radius));
                }
                ALBEDO = tint;
                ALPHA = footprint ? 0.28 : edge * 0.45 * outer * inner;
            }
            """,
    };
    private ShaderMaterial _weaponRangeMaterial = null!;
    private readonly MeshInstance3D _weaponRangeMesh = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private (int Geometry, int WallId, Pos Cell, WallDefenseKind Kind, int Facing, int Topology)? _weaponRangeState;
    internal bool WeaponRangeVisible => _weaponRangeMesh.Visible && _weaponRangeMesh.Mesh is not null;
    internal double WeaponRangeOuter { get; private set; }
    internal double WeaponRangeInner { get; private set; }
    internal int WeaponRangeMeshBuilds { get; private set; }
    internal int WeaponRangeTriangles { get; private set; }
    internal IReadOnlyList<float> WeaponRangeHeights => _weaponRangeMesh.Mesh is ArrayMesh mesh
        ? mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(vertex => vertex.Y).ToArray() : [];

    private void InitializeWeaponRange()
    {
        _weaponRangeMaterial = new ShaderMaterial { Shader = _weaponRangeShader };
        _weaponRangeMesh.MaterialOverride = _weaponRangeMaterial;
        _weaponRangeMesh.Visible = false;
        _viewport.AddChild(_weaponRangeMesh);
    }

    private bool SyncWeaponRange(GameSession session)
    {
        if (session.ActiveWallDefenseGuide is not { } selected)
        {
            bool visible = _weaponRangeMesh.Visible;
            _weaponRangeMesh.Visible = false;
            _weaponRangeState = null;
            return visible;
        }
        var wall = session.SelectedWallComponent;
        int facing = session.WallDefenseToolActive ? session.Preview!.Rotation == 2 ? -1 : 1 : wall?.DefenseFacing ?? 1;
        var state = (GeometryBuilds, wall?.Id ?? 0, selected.Cell, selected.Kind, facing, SiegeGeometryBuilds);
        bool invalid = session.WallDefenseToolActive && session.FixedPlacementError is not null;
        _weaponRangeMaterial.SetShaderParameter("tint", invalid ? new Vector3(.9f, .35f, .3f) : new Vector3(1, .79f, .3f));
        if (_weaponRangeState == state && WeaponRangeVisible) return false;
        _weaponRangeState = state;
        var weapon = WallDefenses.Get(selected.Kind);
        WeaponRangeOuter = weapon.Range;
        WeaponRangeInner = weapon.MinimumRange;
        _weaponRangeMaterial.SetShaderParameter("center", wall is null ? new Vector2(selected.Cell.X + .5f, selected.Cell.Y + .5f) :
            new Vector2(wall.Center.X, wall.Center.Y));
        _weaponRangeMaterial.SetShaderParameter("outer_radius", (float)weapon.Range);
        _weaponRangeMaterial.SetShaderParameter("inner_radius", (float)weapon.MinimumRange);
        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        WeaponRangeTriangles = 0;
        bool dropped = selected.Kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks && wall is not null;
        _weaponRangeMaterial.SetShaderParameter("footprint", dropped);
        if (dropped)
        {
            foreach (var polygon in session.Game.Settlement!.DropCoverage(session.Game, wall!, selected.Kind, facing))
            {
                var boundary = polygon.Select(p => new Vector2(p.X, p.Y)).ToArray();
                int left = Math.Max(0, (int)MathF.Floor(boundary.Min(p => p.X)));
                int right = Math.Min(session.Game.Map.Width - 1, (int)MathF.Floor(boundary.Max(p => p.X)));
                int top = Math.Max(0, (int)MathF.Floor(boundary.Min(p => p.Y)));
                int bottom = Math.Min(session.Game.Map.Height - 1, (int)MathF.Floor(boundary.Max(p => p.Y)));
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        if (!_groundFaces.TryGetValue(session.Game.Map.Index(x, y), out var faces)) continue;
                        foreach (var point in ClipGroundOverlay(boundary, faces))
                        {
                            surface.SetNormal(Vector3.Up);
                            surface.AddVertex(point);
                            WeaponRangeTriangles++;
                        }
                    }
            }
            WeaponRangeTriangles /= 3;
        }
        int reach = (int)Math.Ceiling(weapon.Range) + 1;
        var map = session.Game.Map;
        for (int y = Math.Max(0, selected.Cell.Y - reach); !dropped && y <= Math.Min(map.Height - 1, selected.Cell.Y + reach); y++)
            for (int x = Math.Max(0, selected.Cell.X - reach); x <= Math.Min(map.Width - 1, selected.Cell.X + reach); x++)
            {
                if (!_groundFaces.TryGetValue(map.Index(x, y), out var faces)) continue;
                for (int face = 0; face + 2 < faces.Length; face += 3)
                {
                    var a = faces[face]; var b = faces[face + 1]; var c = faces[face + 2];
                    var normal = (b - a).Cross(c - a);
                    if (Math.Abs(normal.Y) < .000001f) continue;
                    surface.SetNormal(Vector3.Up);
                    surface.AddVertex(a + Vector3.Up * .035f);
                    surface.AddVertex(b + Vector3.Up * .035f);
                    surface.AddVertex(c + Vector3.Up * .035f);
                    WeaponRangeTriangles++;
                }
            }
        _weaponRangeMesh.Mesh?.Dispose();
        _weaponRangeMesh.Mesh = WeaponRangeTriangles > 0 ? surface.Commit() : null;
        _weaponRangeMesh.Visible = WeaponRangeTriangles > 0;
        WeaponRangeMeshBuilds++;
        return true;
    }

    internal static IReadOnlyList<Vector3> ClipGroundOverlay(Vector2[] boundary, Vector3[] faces)
    {
        var result = new List<Vector3>();
        for (int face = 0; face + 2 < faces.Length; face += 3)
        {
            var a = faces[face]; var b = faces[face + 1]; var c = faces[face + 2];
            var normal = (b - a).Cross(c - a);
            if (Math.Abs(normal.Y) < .000001f) continue;
            Vector2[] triangle = [new(a.X, a.Z), new(b.X, b.Z), new(c.X, c.Z)];
            foreach (var polygon in Geometry2D.IntersectPolygons(boundary, triangle))
            {
                var indices = Geometry2D.TriangulatePolygon(polygon);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    var vertices = new Vector3[3];
                    for (int j = 0; j < 3; j++)
                    {
                        var p = polygon[indices[i + j]];
                        float height = a.Y - (normal.X * (p.X - a.X) + normal.Z * (p.Y - a.Z)) / normal.Y;
                        vertices[j] = new(p.X, height + .035f, p.Y);
                    }
                    if ((vertices[1] - vertices[0]).Cross(vertices[2] - vertices[0]).Y > 0)
                        (vertices[1], vertices[2]) = (vertices[2], vertices[1]);
                    result.AddRange(vertices);
                }
            }
        }
        return result;
    }

    private void DisposeWeaponRange()
    {
        _weaponRangeMesh.Mesh?.Dispose();
        _weaponRangeMaterial.Dispose();
        _weaponRangeShader.Dispose();
    }
}
