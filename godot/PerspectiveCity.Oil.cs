using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly StandardMaterial3D _oilMaterial = new()
    {
        AlbedoColor = new Color(.46f, .27f, .105f, .60f),
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = .38f, MetallicSpecular = .28f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    private readonly MeshInstance3D _oilSheetMesh = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private SurfaceTool? _oilSurface;
    internal int OilSheetTriangles { get; private set; }
    internal bool OilSheetVisible => _oilSheetMesh.Visible && _oilSheetMesh.Mesh is not null;

    private SurfaceTool OilSurface()
    {
        if (_oilSurface is not null) return _oilSurface;
        _oilSurface = new SurfaceTool();
        _oilSurface.Begin(Mesh.PrimitiveType.Triangles);
        return _oilSurface;
    }

    private void AppendOilSheet(SiegeProjectile shot, double elapsed) => AppendOilSheet(
        new((float)shot.StartX, shot.WorldStartHeight, (float)shot.StartY),
        new((float)shot.EndX, shot.WorldEndHeight, (float)shot.EndY), elapsed, (float)shot.TravelSeconds,
        new(shot.FootprintForward.X, 0, shot.FootprintForward.Y));

    private void AppendOilSheet(Vector3 start, Vector3 end, double elapsed, float fallSeconds, Vector3 forward)
    {
        var sections = TowerDefenseArtwork.OilSheet(
            new(start.X, start.Y, start.Z), new(end.X, end.Y, end.Z), elapsed, fallSeconds);
        if (sections.Count == 0) return;
        var surface = OilSurface();
        Vector3 Point(System.Numerics.Vector3 p) => new(p.X, p.Y, p.Z);
        void Triangle(Vector3 a, Vector3 b, Vector3 c, float shade)
        {
            surface.SetColor(new Color(shade, shade, shade));
            surface.AddVertex(a); surface.AddVertex(b); surface.AddVertex(c);
            OilSheetTriangles++;
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade)
        {
            Triangle(a, b, c, shade);
            Triangle(a, c, d, shade);
        }
        for (int i = 1; i < sections.Count; i++)
        {
            var a = sections[i - 1]; var b = sections[i];
            float shade = .91f + MathF.Sin(i * .6f - (float)elapsed * 5) * .06f;
            Quad(Point(a.Left), Point(a.Ridge), Point(b.Ridge), Point(b.Left), shade);
            Quad(Point(a.Ridge), Point(a.Right), Point(b.Right), Point(b.Ridge), shade + .14f);
            Quad(Point(a.Left) - forward * a.Thickness, Point(b.Left) - forward * b.Thickness,
                Point(b.Right) - forward * b.Thickness, Point(a.Right) - forward * a.Thickness, shade * .9f);
            Quad(Point(a.Left), Point(b.Left), Point(b.Left) - forward * b.Thickness,
                Point(a.Left) - forward * a.Thickness, shade);
            Quad(Point(a.Right), Point(a.Right) - forward * a.Thickness,
                Point(b.Right) - forward * b.Thickness, Point(b.Right), shade);
        }
        OilFlowSegmentsDrawn += sections.Count - 1;
    }

    private void AppendOilSplash(SiegeProjectile shot, double elapsed)
    {
        double age = elapsed - shot.ContactSeconds;
        if (age < 0 || age > TowerDefenseArtwork.PourSeconds) return;
        var surface = OilSurface();
        var forward = new Vector3(shot.FootprintForward.X, 0, shot.FootprintForward.Y);
        var right = new Vector3(forward.Z, 0, -forward.X);
        var impact = new Vector3((float)shot.EndX, shot.WorldEndHeight, (float)shot.EndY);
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            surface.SetColor(Colors.White);
            surface.AddVertex(a); surface.AddVertex(b); surface.AddVertex(c);
            OilSheetTriangles++;
        }
        for (int drop = 0; drop < 3; drop++)
        {
            float t = (float)((age * 2.8 + drop * .37) % 1);
            float side = drop % 2 == 0 ? 1 : -1;
            var center = impact + right * (side * .5f * t) + forward * ((drop - 1) * .2f * t);
            center.Y += .07f + .3f * 4 * t * (1 - t);
            float radius = .09f - t * .025f;
            float height = radius * 2.3f;
            for (int face = 0; face < 6; face++)
            {
                float a = face * Mathf.Tau / 6, b = (face + 1) * Mathf.Tau / 6;
                var p = center + new Vector3(Mathf.Cos(a) * radius, -height * .2f, Mathf.Sin(a) * radius);
                var q = center + new Vector3(Mathf.Cos(b) * radius, -height * .2f, Mathf.Sin(b) * radius);
                Triangle(center + Vector3.Up * height * .8f, p, q);
                Triangle(center - Vector3.Up * height * .5f, q, p);
            }
        }
    }

    private void SyncOilSheet()
    {
        _oilSheetMesh.Mesh?.Dispose();
        _oilSheetMesh.Mesh = null;
        if (_oilSurface is null) { _oilSheetMesh.Visible = false; return; }
        _oilSurface.GenerateNormals();
        _oilSheetMesh.Mesh = _oilSurface.Commit();
        _oilSurface.Dispose();
        _oilSurface = null;
        _oilSheetMesh.Visible = true;
    }
}
